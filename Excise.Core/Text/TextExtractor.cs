using System.Linq;
using System.Text;
using System.Threading;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Parsing;
using Excise.Core.Primitives;

namespace Excise.Core.Text;

/// <summary>
/// Extracts text and letter information from PDF pages.
/// </summary>
public class TextExtractor
{
    private readonly PdfPage _page;
    private readonly byte[] _contentStream;

    // Hostile-input guard (#982), the twin of ContentStreamParser's (#346).
    // The array nesting bound (#971) stops unbounded RECURSION but not
    // unbounded WORK: a large-but-shallow stream — millions of operators, a
    // huge TJ array, repeated Do chains inside the 64-deep form bound — ran
    // page.Letters to completion whatever it cost, on the GUI's background
    // indexing thread, with no timeout to hit (CLAUDE.md Pitfall 3). An
    // instance field rather than a parameter so the Do re-entry of
    // ParseContentBytes inherits it.
    private CancellationToken _cancellationToken;

    // Text state
    private double _fontSize = 12;
    private string _fontName = "";
    private PdfDictionary? _currentFont;
    // The nine-step code→Unicode cascade, shared with ContentStreamParser
    // since #981 (it had three steps and a comment claiming otherwise).
    // Everything it derives from the font dictionary — /ToUnicode, the
    // /Differences table, the embedded reverse cmap, the Mac glyph order and
    // the symbol cmap — lives in that object now. Rebuilt (from a per-font
    // cache) on each /Tf.
    private GlyphUnicodeDecoder _decoder = GlyphUnicodeDecoder.None;
    private readonly Dictionary<PdfDictionary, GlyphUnicodeDecoder> _decoderCache =
        new(ReferenceEqualityComparer.Instance);
    // Registered (predefined) CJK CMap support (#515 slice 2). When the Type0
    // font's /Encoding is a registered CMap NAME this build ships (e.g.
    // /UniGB-UCS2-H, /90ms-RKSJ-H), _registeredEncodingCMap decodes the raw
    // string bytes into (code, CID, byteLength) triples — including mixed
    // 1/2-byte codespaces — replacing the fixed 2-byte stride.
    // _registeredCidToUnicode is the Adobe-<Ordering>-UCS2 CID→Unicode map
    // selected from the descendant's /CIDSystemInfo (PDF §9.10.2 method (b));
    // it also fires for /Encoding /Identity-H|V fonts whose CIDSystemInfo names
    // a known ordering (there code == CID), and when /ToUnicode is a registered
    // CMap NAME (#715: the name declares the ordering; the CID comes from the
    // font's encoding). Both null whenever out of scope; recomputed on each /Tf.
    private CidCMap? _registeredEncodingCMap;
    private IReadOnlyDictionary<int, string>? _registeredCidToUnicode;
    private double _textLeading = 0;
    private double _charSpacing = 0;
    private double _wordSpacing = 0;
    private double _horizontalScaling = 100;
    private double _textRise;
    // Type 0 / CID font state (§9.7)
    private bool _is2ByteFont;
    private bool _isCidFont;
    private bool _isVerticalWriting;
    private Fonts.CidFontWidths? _cidMetrics;

    // Text matrix (position + transformation)
    private double _tm_a = 1, _tm_b = 0, _tm_c = 0, _tm_d = 1, _tm_e = 0, _tm_f = 0;

    // Line matrix (start of line position)
    private double _tlm_e = 0, _tlm_f = 0;

    // Graphics state stack
    private readonly Stack<GraphicsState> _stateStack = new();
    private double _ctm_a = 1, _ctm_b = 0, _ctm_c = 0, _ctm_d = 1, _ctm_e = 0, _ctm_f = 0;
    private readonly Stack<PdfDictionary> _resourcesStack = new();
    private readonly HashSet<PdfStream> _formXObjectStack = new();
    private readonly Stack<bool> _optionalContentHiddenStack = new();
    private int _formXObjectDepth;
    private const int MaxFormXObjectDepth = 64;

    private readonly List<Letter> _letters = new();

    // Marked-content nesting depth of /OC spans that are hidden. Maintained in
    // lock-step with _optionalContentHiddenStack (BDC/BMC push, EMC pop) so the
    // per-glyph "am I inside any hidden span?" check in ShowGlyph is O(1)
    // instead of a per-letter LINQ Any() over the stack (#600).
    private int _hiddenOptionalContentDepth;

    // Marked-content ID (/MCID) tracking for the accessibility MCID→letter
    // bridge (#776). Pushed/popped in lock-step with _optionalContentHiddenStack
    // (every BDC/BMC pushes, every EMC pops) so the nesting matches exactly.
    // Each entry is the EFFECTIVE MCID at that nesting level: a span carrying its
    // own /MCID sets it; a span without one inherits the enclosing level's value.
    // _currentMcid mirrors the top of the stack for O(1) per-glyph tagging.
    private readonly Stack<int?> _mcidStack = new();
    private int? _currentMcid;

    // The /MCID value found in the most recently parsed inline properties
    // dictionary (a BDC operand like /Span <</MCID 3>> BDC), or null. Reset
    // after every operator so a later property-less span (BMC, or a named-
    // property BDC) never inherits a stale id.
    private int? _lastDictMcid;

    // Scratch byte buffer reused by ParseStringLiteral/ParseHexString across
    // calls (#600): each call resets the length, appends its decoded bytes and
    // copies them into an exact-size result array before returning, so nothing
    // aliases the scratch. String/hex token parses never nest inside one
    // another, and a TextExtractor instance is single-threaded by construction
    // (all parse state already lives in instance fields), so instance-level
    // reuse is safe.
    private byte[] _stringScratch = new byte[128];
    private int _stringScratchLen;

    private void ScratchAdd(byte b)
    {
        if (_stringScratchLen == _stringScratch.Length)
            Array.Resize(ref _stringScratch, _stringScratch.Length * 2);
        _stringScratch[_stringScratchLen++] = b;
    }

    // Boxed-int cache for ParseNumber (#600): content streams carry huge
    // volumes of small integer operands (TJ kern values, Td/Tm coordinates);
    // boxing each one separately was a measurable share of extraction
    // allocation. Boxes are immutable and only ever read back via pattern
    // matching / ToDouble, so sharing instances is observably identical.
    private const int SmallIntBoxMin = -1024;
    private const int SmallIntBoxMax = 1024;
    private static readonly object[] SmallIntBoxes = CreateSmallIntBoxes();

    private static object[] CreateSmallIntBoxes()
    {
        var boxes = new object[SmallIntBoxMax - SmallIntBoxMin + 1];
        for (int i = 0; i < boxes.Length; i++)
            boxes[i] = SmallIntBoxMin + i;
        return boxes;
    }

    private static object BoxInt(int value) =>
        value >= SmallIntBoxMin && value <= SmallIntBoxMax
            ? SmallIntBoxes[value - SmallIntBoxMin]
            : value;


    public TextExtractor(PdfPage page)
    {
        _page = page;
        _contentStream = page.GetContentStreamBytes();
        if (page.Resources != null)
            _resourcesStack.Push(page.Resources);
    }

    /// <summary>
    /// When true, AcroForm field values and FreeText annotation content
    /// (§12.5.6.6 — text drawn directly on the page via an annotation rather
    /// than a content-stream Tj, e.g. sticky-note-style comments left by
    /// Acrobat/Preview/Foxit) whose widget/rect is on this page are emitted
    /// as synthetic Letters in addition to the content-stream text. This
    /// makes both visible to search, text extraction, and — critically —
    /// redaction (#660: FreeText content was previously findable by nothing,
    /// a `RedactText` blind spot). Defaults to true; the only reason to turn
    /// it off is to inspect raw content-stream output for diagnostics.
    /// </summary>
    public bool IncludeFormFieldValues { get; set; } = true;

    /// <summary>
    /// Extract all letters from the page.
    /// </summary>
    /// <param name="cancellationToken">Cooperatively abandons a runaway
    /// extraction of hostile/huge input (#982), the twin of
    /// <see cref="Content.ContentStreamParser.Parse"/>'s (#346). Defaults to
    /// <c>default</c>, so existing callers are unchanged.</param>
    public IReadOnlyList<Letter> ExtractLetters(CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
        _letters.Clear();
        ParseContentStream();
        // Restore logical character order for RTL (Arabic/Hebrew) runs (#632).
        // Content streams usually carry RTL text in VISUAL order (reversed);
        // stream-order extraction would make a logical-order search string —
        // and therefore RedactText — silently miss the word. Applied only to
        // content-stream letters: the synthetic AcroForm/annotation letters
        // emitted below are laid out from logical-order source strings.
        BidiReorderer.ReorderVisualRtlRuns(_letters);
        if (IncludeFormFieldValues)
        {
            EmitFormFieldLetters();
            EmitMarkupAnnotationLetters();
        }
        return _letters.AsReadOnly();
    }

    /// <summary>
    /// Walk the page's AcroForm fields and emit synthetic Letters for any with
    /// a string value (/V) and a widget rectangle. Positions are an estimate
    /// based on the widget rect — the real glyph layout would require parsing
    /// the field's appearance stream, which the read-only AcroForm slice
    /// deliberately defers. For search and redaction the rect-based positions
    /// are precise enough: the redaction rectangle still encloses the value,
    /// and search just needs the text content present.
    /// </summary>
    private void EmitFormFieldLetters()
    {
        IReadOnlyList<PdfField> fields;
        try { fields = _page.GetFormFields(); }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { return; }

        foreach (var field in fields)
        {
            if (field.Rect == null) continue;

            // List box (Choice, non-combo): the widget visually renders its
            // ENTIRE /Opt option list, with the current selection
            // highlighted — that's how list boxes work, unlike a closed
            // combo box which shows only the selected value. mutool
            // (render-based) sees every option string across all of a
            // page's list-box widgets; before this, excise only emitted the
            // selected /V, systematically under-extracting them (#661).
            // Combo boxes deliberately stay on the /V-only path below —
            // confirmed against mutool that a closed combo box renders
            // only its selected value, never its option list.
            if (field.FieldType == PdfFieldType.Choice && !field.IsComboBox &&
                field.Options is { Count: > 0 } options)
            {
                EmitMultiLineLettersInRect(
                    string.Join("\n", options), field.Rect.Value, $"AcroForm:{field.FieldType}");
                continue;
            }

            // Signature fields have no string /V (it's a signature dictionary,
            // not text) but their /AP/N appearance stream commonly draws real,
            // visible text — a "Digitally signed by X, date, reason" block
            // (#669). That text lives in a nested Form XObject the appearance
            // invokes, not anywhere EmitFormFieldLetters otherwise looks.
            if (field.FieldType == PdfFieldType.Signature)
            {
                EmitSignatureAppearanceLetters(field);
                continue;
            }

            var value = field.Value ?? field.DefaultValue;
            if (string.IsNullOrEmpty(value)) continue;
            // Buttons are off/on/checked/unchecked names — not human-readable text.
            if (field.FieldType == PdfFieldType.Button) continue;

            // Plain multiline text fields (/Ff bit 12) can hold far more than
            // fits on one line — the same reasoning as the Choice-listbox
            // branch above, just for /FT /Tx instead of /FT /Ch (#672). The
            // single-line EmitLettersInRect silently truncates to whatever
            // fits the rect's width, which is wrong for one long line.
            if (field.IsMultiline)
                EmitMultiLineLettersInRect(value, field.Rect.Value, $"AcroForm:{field.FieldType}");
            else
                EmitLettersInRect(value, field.Rect.Value, $"AcroForm:{field.FieldType}");
        }
    }

    /// <summary>
    /// Emit synthetic Letters for a Signature field's rendered appearance
    /// text (#669). A signature widget's <c>/V</c> is a signature dictionary,
    /// not a string, so the normal value-based path above never applies —
    /// but the widget's <c>/AP/N</c> appearance stream frequently draws real
    /// text ("Digitally signed by…", date, reason) that mutool's renderer
    /// (and a human) sees, which excise was blind to entirely before this.
    /// Font-name prefix is still "AcroForm:" (not a new prefix) so
    /// <c>PdfDocumentRedactionExtensions.IsInteractiveOnlyMatch</c> already
    /// routes a match here through <see cref="InteractiveRedactionScrubber"/>
    /// with no changes needed there beyond no longer skipping Signature
    /// fields when scrubbing (see that class).
    /// </summary>
    private void EmitSignatureAppearanceLetters(PdfField field)
    {
        if (field.Rect == null) return;

        foreach (var widget in field.WidgetDictionaries)
        {
            var text = ExtractWidgetAppearanceText(widget);
            if (string.IsNullOrEmpty(text)) continue;

            EmitMultiLineLettersInRect(text, field.Rect.Value, $"AcroForm:{field.FieldType}");
        }
    }

    /// <summary>
    /// Resolve a widget annotation's <c>/AP/N</c> appearance stream (direct
    /// Form XObject, or an appearance-state sub-dictionary keyed by name —
    /// ISO 32000-2 §12.5.5) and extract its text content by parsing it with
    /// the same machinery used for page-content <c>Do</c> targets
    /// (<see cref="ExtractFormXObjectContent"/>), which already knows how to
    /// walk into a nested Form XObject (the confirmed <c>bug854315.pdf</c>
    /// fixture routes <c>/AP/N</c> → <c>/FRM</c> → further nested forms
    /// before reaching the actual <c>Tj</c> calls).
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT try to map the appearance's own coordinate
    /// space (its <c>/BBox</c>/<c>/Matrix</c>) into the widget's <c>/Rect</c>
    /// — that mapping is real work with its own edge cases and buys nothing
    /// here, since the letters this parse produces are positioned in a
    /// throwaway, appearance-local space and then discarded. Only the
    /// decoded text VALUE survives; the caller re-lays it out with
    /// <see cref="EmitMultiLineLettersInRect"/> against the real page-space
    /// <c>/Rect</c>, exactly like the AcroForm value and FreeText annotation
    /// paths already do (#660, #661) — this keeps one positioning convention
    /// instead of two.
    /// </remarks>
    private string ExtractWidgetAppearanceText(PdfDictionary widgetDict)
    {
        var apObj = widgetDict.GetOptional("AP");
        if (apObj == null || _page.Document.Resolve(apObj) is not PdfDictionary ap)
            return string.Empty;

        var nObj = ap.GetOptional("N");
        if (nObj == null) return string.Empty;

        var nResolved = _page.Document.Resolve(nObj);
        var appearanceStream = nResolved as PdfStream;
        if (appearanceStream == null && nResolved is PdfDictionary states)
        {
            // /AP/N is a sub-dictionary of appearance states (one stream per
            // /AS name) rather than a single stream directly. Prefer the
            // widget's current /AS state; fall back to the first entry when
            // /AS is absent or doesn't match (mirrors the leniency of
            // ExtractWidgetExportValue in PdfAcroFormParser.cs).
            var asName = widgetDict.GetNameOrNull("AS");
            PdfObject? chosen = asName != null ? states.GetOptional(asName) : null;
            chosen ??= states.Values.FirstOrDefault();
            appearanceStream = chosen != null ? _page.Document.Resolve(chosen) as PdfStream : null;
        }

        if (appearanceStream == null || appearanceStream.GetNameOrNull("Subtype") != "Form")
            return string.Empty;

        var lettersBefore = _letters.Count;
        var savedCtm = (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f);
        // Reset to identity: this parse happens after the page's own content
        // stream has already been walked (ExtractLetters calls
        // EmitFormFieldLetters after ParseContentStream), so whatever CTM was
        // left behind is unrelated to the annotation and would just pollute
        // the (already-discarded) positions computed below.
        _ctm_a = 1; _ctm_b = 0; _ctm_c = 0; _ctm_d = 1; _ctm_e = 0; _ctm_f = 0;

        try
        {
            ExtractFormXObjectContent(appearanceStream);
        }
        finally
        {
            (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f) = savedCtm;
        }

        if (_letters.Count == lettersBefore) return string.Empty;

        var text = string.Concat(_letters.Skip(lettersBefore).Select(l => l.Value));
        // These letters were positioned in the appearance's own local space,
        // not the page's — discard them so they don't get returned twice
        // (once here, mispositioned; once properly via EmitMultiLineLettersInRect).
        _letters.RemoveRange(lettersBefore, _letters.Count - lettersBefore);
        return text;
    }

    /// <summary>
    /// Walk the page's markup annotations and emit synthetic Letters for
    /// FreeText content (§12.5.6.6 — text drawn directly on the page, not a
    /// popup/icon comment). #660: confirmed against mutool that FreeText
    /// content is genuinely visible page text (mutool's renderer draws it),
    /// while a plain `/Text` sticky-note annotation is NOT (mutool's `-F txt`
    /// never surfaces sticky-note `/Contents` — it's an icon+popup UI
    /// element, not inline content) — so only FreeText is in scope here,
    /// deliberately not every annotation subtype with a `/Contents` string.
    /// The synthesized "Annotation:FreeText" font-name prefix mirrors
    /// EmitFormFieldLetters's "AcroForm:" convention: <c>RedactText</c> uses
    /// it to route a match to <c>InteractiveRedactionScrubber</c> (which
    /// removes the whole annotation, /Contents and /AP together) instead of
    /// the content-stream glyph-removal pass, since there's no content-stream
    /// glyph here to remove.
    /// </summary>
    private void EmitMarkupAnnotationLetters()
    {
        IReadOnlyList<Document.PdfAnnotation> annotations;
        try { annotations = _page.GetAnnotations(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }

        foreach (var annot in annotations)
        {
            if (annot.Subtype != Document.PdfAnnotationSubtype.FreeText) continue;
            if (string.IsNullOrEmpty(annot.Contents)) continue;

            EmitMultiLineLettersInRect(annot.Contents, annot.Rect, "Annotation:FreeText");
        }
    }

    /// <summary>
    /// Like <see cref="EmitLettersInRect"/> but word-wraps across multiple
    /// lines within the rect (via the shared <see cref="Graphics.TextWrapper"/>)
    /// instead of truncating to one line. FreeText comments are commonly
    /// multi-line and can be much longer than a single-line form-field value
    /// — a single-line truncation would drop most of a real fixture's content
    /// (confirmed: a Arabic FreeText comment in the wild runs ~380 characters
    /// across several lines). Lines that don't fit vertically are dropped,
    /// same "truncate what doesn't fit" precedent as EmitLettersInRect's
    /// horizontal truncation.
    /// </summary>
    private void EmitMultiLineLettersInRect(string text, PdfRectangle rect, string fontName)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fontSize = Math.Min(rect.Height, 10.0);
        if (fontSize <= 0) return;

        var font = Graphics.PdfFont.Helvetica(fontSize);
        var advance = fontSize * 0.55; // same flat-advance approximation as EmitLettersInRect
        var lineHeight = fontSize * 1.2;

        var lines = Graphics.TextWrapper.Wrap(text, font, rect.Width);

        var y = rect.Top - fontSize;
        foreach (var line in lines)
        {
            if (y < rect.Bottom) break;
            if (line.Length == 0) { y -= lineHeight; continue; }

            var x = rect.Left;
            foreach (var ch in line)
            {
                var bbox = new PdfRectangle(x, y, x + advance, y + fontSize);
                _letters.Add(new Letter(ch.ToString(), bbox, fontSize, fontName, x, y, advance, ch));
                x += advance;
            }

            y -= lineHeight;
        }
    }

    private void EmitLettersInRect(string text, PdfRectangle rect, string fontName)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var fontSize = Math.Min(rect.Height * 0.85, 12.0);
        if (fontSize <= 0) return;

        // Approximation: assume average glyph advance of 0.55em. Real PDFs
        // vary, but for search/redaction we just need to land letters within
        // the widget's rect.
        var advance = fontSize * 0.55;
        var maxChars = (int)Math.Floor(rect.Width / advance);
        if (maxChars <= 0) return;

        // Truncate so we never paint outside the widget rect.
        if (text.Length > maxChars) text = text.Substring(0, maxChars);

        var x = rect.Left;
        var baselineY = rect.Bottom + (rect.Height - fontSize) * 0.5;

        foreach (var ch in text)
        {
            var bbox = new PdfRectangle(x, baselineY, x + advance, baselineY + fontSize);
            _letters.Add(new Letter(
                ch.ToString(),
                bbox,
                fontSize,
                fontName,
                x,
                baselineY,
                advance,
                ch));
            x += advance;
        }
    }

    /// <summary>
    /// Extract plain text from the page.
    /// </summary>
    /// <param name="cancellationToken">See <see cref="ExtractLetters"/> (#982).</param>
    public string ExtractText(CancellationToken cancellationToken = default)
    {
        var letters = ExtractLetters(cancellationToken);
        var sb = new StringBuilder(letters.Count + 16); // most letters are 1 char (#600)
        foreach (var letter in letters)
        {
            sb.Append(letter.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extract words from the page. Words are sequences of letters
    /// separated by whitespace or large gaps.
    /// </summary>
    /// <param name="cancellationToken">See <see cref="ExtractLetters"/> (#982).</param>
    public IReadOnlyList<Word> ExtractWords(CancellationToken cancellationToken = default)
    {
        var letters = ExtractLetters(cancellationToken);
        return BuildWords(letters);
    }

    internal static IReadOnlyList<Word> BuildWords(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0)
            return Array.Empty<Word>();

        var words = new List<Word>();
        var currentWordLetters = new List<Letter>();

        // Threshold for word separation (in points)
        // Typical space width is ~3-4 points at 12pt font
        const double wordGapThreshold = 3.0;
        const double lineGapThreshold = 5.0;

        Letter? prevLetter = null;

        foreach (var letter in letters)
        {
            bool startNewWord = false;

            if (prevLetter != null)
            {
                // Check for line break
                var yDiff = Math.Abs(letter.StartY - prevLetter.StartY);
                if (yDiff > lineGapThreshold)
                {
                    startNewWord = true;
                }
                else
                {
                    // Check for horizontal gap
                    var gap = letter.GlyphRectangle.Left - prevLetter.GlyphRectangle.Right;
                    if (gap > wordGapThreshold)
                    {
                        startNewWord = true;
                    }
                }
            }

            // Check if letter is whitespace
            if (letter.Value.Length == 1 && char.IsWhiteSpace(letter.Value[0]))
            {
                // Don't add whitespace to words, but end current word
                if (currentWordLetters.Count > 0)
                {
                    words.Add(new Word(currentWordLetters.ToArray()));
                    currentWordLetters.Clear();
                }
                prevLetter = letter;
                continue;
            }

            if (startNewWord && currentWordLetters.Count > 0)
            {
                words.Add(new Word(currentWordLetters.ToArray()));
                currentWordLetters.Clear();
            }

            currentWordLetters.Add(letter);
            prevLetter = letter;
        }

        // Don't forget the last word
        if (currentWordLetters.Count > 0)
        {
            words.Add(new Word(currentWordLetters.ToArray()));
        }

        return words;
    }

    private void ParseContentStream()
    {
        ParseContentBytes(_contentStream);
    }

    private void ParseContentBytes(byte[] contentBytes)
    {
        var content = Encoding.Latin1.GetString(contentBytes);
        var pos = 0;
        // Presized: content-stream operators rarely take more than a handful of
        // operands (Tm's 6 is the common max), so start past the default 4 to
        // avoid the first grow-and-copy on nearly every operator (#600).
        var operands = new List<object>(8);

        while (pos < content.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SkipWhitespaceAndComments(content, ref pos);
            if (pos >= content.Length) break;

            // Try to parse a token
            var token = ParseToken(content, ref pos);
            if (token == null) continue; // Skip null tokens (like dictionaries) but keep parsing

            if (token is string op && IsOperator(op))
            {
                if (op == "BI")
                {
                    // §8.9.7: the bytes between ID and EI are raw pixel data,
                    // NOT tokens. Without this they were tokenised: a stray '('
                    // in the samples opened a string literal that swallowed the
                    // rest of the stream, so every glyph AFTER an inline image
                    // vanished from page.Letters while ContentStreamParser (which
                    // has always parsed BI/ID/EI as one unit) saw them. Silent
                    // extraction blindness bounds redaction — #637's class, from
                    // #980's divergence.
                    SkipInlineImage(content, ref pos);
                    operands.Clear();
                    _lastDictMcid = null;
                    continue;
                }

                ExecuteOperator(op, operands);
                operands.Clear();
                _lastDictMcid = null; // consumed by the operator just executed (#776)
            }
            else if (IsUnknownOperatorKeyword(token))
            {
                // §7.8.2: an operator we do not implement still TERMINATES its
                // operands. Leaving them queued let a bare keyword's operands
                // be read by the next real operator — see the `sh` case in the
                // Operators set above. ContentStreamParser drops the keyword
                // and does the same clear (#980).
                operands.Clear();
                _lastDictMcid = null;
            }
            else
            {
                operands.Add(token);
            }
        }
    }

    /// <summary>
    /// Upper bound on an inline image's data scan when no <c>/L</c> length is
    /// declared, matching
    /// <see cref="Excise.Core.Content.ContentStreamParser"/>'s bound of the
    /// same name (#347): the two parse the same bytes and must bound them the
    /// same way (#980).
    /// </summary>
    private const int MaxInlineImageScanBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Advance <paramref name="pos"/> past an inline image (§8.9.7) whose
    /// <c>BI</c> token has just been consumed, so its raw sample bytes are
    /// never tokenised. Mirrors
    /// <c>ContentStreamParser.ParseInlineImage</c> byte-for-byte in where it
    /// leaves <paramref name="pos"/>; extraction has no use for the pixels, so
    /// only the position matters here (#980).
    /// </summary>
    private static void SkipInlineImage(string content, ref int pos)
    {
        // --- 1. Image parameters, up to the ID token ---
        long declaredLength = -1;
        string? pendingKey = null;

        while (pos < content.Length)
        {
            SkipInlineImageWhitespace(content, ref pos);
            if (pos >= content.Length) break;

            if (content[pos] == 'I' && pos + 1 < content.Length && content[pos + 1] == 'D' &&
                (pos + 2 >= content.Length || IsPdfWhitespace(content[pos + 2])))
            {
                pos += 2; // consume 'ID'
                // §8.9.7: exactly ONE whitespace byte separates ID from the
                // data. Producers write CRLF anyway, so treat the pair as one
                // separator — deliberately only the exact \r\n pair, never a
                // general whitespace skip, because unfiltered sample data may
                // legitimately begin with 0x0A or 0x20.
                if (pos < content.Length && IsPdfWhitespace(content[pos]))
                {
                    bool cr = content[pos] == '\r';
                    pos++;
                    if (cr && pos < content.Length && content[pos] == '\n')
                        pos++;
                }
                break;
            }

            var token = ParseInlineImageToken(content, ref pos);
            if (token is string name && name.Length > 1 && name[0] == '/')
            {
                pendingKey = name;
            }
            else
            {
                if (pendingKey is "/L" or "/Length" && token is int len)
                    declaredLength = len;
                pendingKey = null;
            }
        }

        int dataStart = pos;

        // --- 2a. Trust an explicit /L, confirming EI lines up after it ---
        if (declaredLength > 0 && dataStart + declaredLength <= content.Length)
        {
            int probe = dataStart + (int)declaredLength;
            while (probe < content.Length && IsPdfWhitespace(content[probe])) probe++;
            if (probe + 1 < content.Length && content[probe] == 'E' && content[probe + 1] == 'I' &&
                (probe + 2 >= content.Length || IsInlineImageWordBoundary(content[probe + 2])))
            {
                pos = probe + 2;
                return;
            }
            // length present but EI didn't line up → fall through to scanning
        }

        // --- 2b. Scan for EI at a word boundary ---
        while (pos < content.Length)
        {
            if (pos - dataStart > MaxInlineImageScanBytes)
                throw new PdfParseException(
                    $"Inline image (no /L) exceeded {MaxInlineImageScanBytes} bytes without an EI marker");

            if (IsPdfWhitespace(content[pos]) || pos == dataStart)
            {
                int wsPos = pos;
                if (pos != dataStart) pos++;

                if (pos + 1 < content.Length && content[pos] == 'E' && content[pos + 1] == 'I' &&
                    (pos + 2 >= content.Length || IsInlineImageWordBoundary(content[pos + 2])))
                {
                    pos += 2; // consume 'EI'
                    return;
                }

                pos = wsPos + 1;
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// One image-parameter token. Names and numbers are all this needs to read
    /// (only <c>/L</c> is acted on); everything else is consumed for position.
    /// </summary>
    private static object? ParseInlineImageToken(string content, ref int pos)
    {
        if (pos >= content.Length) return null;
        var c = content[pos];

        if (c == '/')
        {
            int start = pos;
            pos++;
            while (pos < content.Length)
            {
                var n = content[pos];
                if (char.IsWhiteSpace(n) || n == '/' || n == '[' || n == ']' ||
                    n == '<' || n == '>' || n == '(' || n == ')' || n == '{' || n == '}')
                    break;
                pos++;
            }
            return content.Substring(start, pos - start);
        }

        if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
        {
            int start = pos;
            bool hasDot = false;
            while (pos < content.Length)
            {
                var n = content[pos];
                if (char.IsDigit(n) || n == '-' || n == '+' || n == '.')
                {
                    if (n == '.') hasDot = true;
                    pos++;
                }
                else break;
            }
            var span = content.AsSpan(start, pos - start);
            if (hasDot)
                return double.TryParse(span, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            return int.TryParse(span, out var i) ? i : 0;
        }

        if (c == '[')
        {
            int depth = 0;
            while (pos < content.Length)
            {
                if (content[pos] == '[') depth++;
                else if (content[pos] == ']') { depth--; pos++; if (depth <= 0) break; continue; }
                pos++;
            }
            return null;
        }

        if (char.IsLetter(c))
        {
            while (pos < content.Length && char.IsLetterOrDigit(content[pos])) pos++;
            return null;
        }

        pos++;
        return null;
    }

    private static void SkipInlineImageWhitespace(string content, ref int pos)
    {
        while (pos < content.Length && IsPdfWhitespace(content[pos]))
            pos++;
    }

    private static bool IsPdfWhitespace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\0';

    private static bool IsInlineImageWordBoundary(char c) =>
        IsPdfWhitespace(c) || c == '/' || c == '(' || c == ')' || c == '[' || c == ']';

    /// <summary>
    /// True for a bare keyword token that is neither a recognised operator nor
    /// one of the three literal operand keywords. Names (which
    /// <see cref="ParseName"/> returns with their leading <c>/</c>) and every
    /// non-string token are operands and never match. #980
    /// </summary>
    private static bool IsUnknownOperatorKeyword(object token) =>
        token is string s
        && s.Length > 0
        && s[0] != '/'
        && s is not ("true" or "false" or "null")
        && !IsOperator(s);

    private void SkipWhitespaceAndComments(string content, ref int pos)
    {
        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c == '%')
            {
                // Skip comment to end of line
                while (pos < content.Length && content[pos] != '\n' && content[pos] != '\r')
                    pos++;
            }
            else
            {
                break;
            }
        }
    }

    private object? ParseToken(string content, ref int pos)
    {
        if (pos >= content.Length) return null;

        var c = content[pos];

        // String literal
        if (c == '(')
        {
            return ParseStringLiteral(content, ref pos);
        }

        // Hex string
        if (c == '<')
        {
            if (pos + 1 < content.Length && content[pos + 1] == '<')
            {
                // Dictionary - skip for now
                pos += 2;
                SkipDictionary(content, ref pos);
                return null;
            }
            return ParseHexString(content, ref pos);
        }

        // Array
        if (c == '[')
        {
            return ParseArray(content, ref pos);
        }

        // Name
        if (c == '/')
        {
            return ParseName(content, ref pos);
        }

        // Number or operator
        if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
        {
            return ParseNumber(content, ref pos);
        }

        // Keyword/operator
        if (char.IsLetter(c) || c == '\'' || c == '"' || c == '*')
        {
            return ParseKeyword(content, ref pos);
        }

        // Skip unknown
        pos++;
        return null;
    }

    private byte[] ParseStringLiteral(string content, ref int pos)
    {
        _stringScratchLen = 0; // reuse the scratch buffer across calls (#600)
        pos++; // Skip opening '('
        int parenDepth = 1;

        while (pos < content.Length && parenDepth > 0)
        {
            var c = content[pos];

            if (c == '\\' && pos + 1 < content.Length)
            {
                pos++;
                var escaped = content[pos];
                switch (escaped)
                {
                    case 'n': ScratchAdd((byte)'\n'); break;
                    case 'r': ScratchAdd((byte)'\r'); break;
                    case 't': ScratchAdd((byte)'\t'); break;
                    case 'b': ScratchAdd((byte)'\b'); break;
                    case 'f': ScratchAdd((byte)'\f'); break;
                    case '(': ScratchAdd((byte)'('); break;
                    case ')': ScratchAdd((byte)')'); break;
                    case '\\': ScratchAdd((byte)'\\'); break;
                    // REVERSE SOLIDUS followed by an end-of-line marker is a
                    // line-continuation: it produces NO character (PDF32000-1
                    // §7.3.4.2 Table 3). CRLF is one marker, not two — consume
                    // both bytes. Without this, a source-wrapped word like
                    // "Instruc\<LF>tions" decodes with a spurious literal
                    // newline splitting it in two, which breaks substring
                    // matching in RedactText (#637).
                    case '\r':
                        if (pos + 1 < content.Length && content[pos + 1] == '\n') pos++;
                        break;
                    case '\n':
                        break;
                    default:
                        // Octal escape: up to 3 octal digits, value truncated
                        // to a byte exactly like the previous
                        // (byte)Convert.ToInt32(..., 8) implementation.
                        if (escaped >= '0' && escaped <= '7')
                        {
                            int value = escaped - '0';
                            int digits = 1;
                            while (digits < 3 && pos + 1 < content.Length &&
                                   content[pos + 1] >= '0' && content[pos + 1] <= '7')
                            {
                                pos++;
                                value = value * 8 + (content[pos] - '0');
                                digits++;
                            }
                            ScratchAdd(unchecked((byte)value));
                        }
                        else
                        {
                            ScratchAdd((byte)escaped);
                        }
                        break;
                }
            }
            else if (c == '(')
            {
                parenDepth++;
                ScratchAdd((byte)c);
            }
            else if (c == ')')
            {
                parenDepth--;
                if (parenDepth > 0)
                    ScratchAdd((byte)c);
            }
            else
            {
                ScratchAdd((byte)c);
            }
            pos++;
        }

        return _stringScratch.AsSpan(0, _stringScratchLen).ToArray();
    }

    private byte[] ParseHexString(string content, ref int pos)
    {
        pos++; // Skip '<'
        _stringScratchLen = 0; // reuse the scratch buffer across calls (#600)
        int pendingNibble = -1;

        while (pos < content.Length && content[pos] != '>')
        {
            var c = content[pos];
            if (char.IsLetterOrDigit(c))
            {
                // Per §7.3.4.3 a hex string holds only hex digits (whitespace
                // is ignored), so a letter G-Z is skipped rather than fatal —
                // the same rule ContentStreamParser.ParseHexString already
                // under #352. This copy kept throwing FormatException, which
                // escaped page.Letters raw on hostile input: the #352 fix was
                // made to one of the two content-stream parsers and not the
                // other, the same divergence as #971 (#974).
                var nibble = HexDigitValue(c);
                if (nibble < 0)
                {
                    pos++;
                    continue;
                }
                if (pendingNibble < 0)
                {
                    pendingNibble = nibble;
                }
                else
                {
                    ScratchAdd((byte)((pendingNibble << 4) | nibble));
                    pendingNibble = -1;
                }
            }
            pos++;
        }
        pos++; // Skip '>'

        if (pendingNibble >= 0)
            ScratchAdd((byte)(pendingNibble << 4)); // Pad with 0 if odd length

        return _stringScratch.AsSpan(0, _stringScratchLen).ToArray();
    }

    private static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    /// <summary>
    /// Max array nesting depth before extraction aborts, matching
    /// <see cref="Excise.Core.Content.ContentStreamParser.MaxNestingDepth"/> —
    /// the two parse the same bytes and must bound them the same way (#971).
    /// Real content streams nest show-arrays two or three deep.
    /// </summary>
    private const int MaxArrayNestingDepth = 256;

    private int _arrayNestingDepth;

    private List<object> ParseArray(string content, ref int pos)
    {
        // Presized for the typical TJ show-array shape (alternating strings
        // and kern adjustments) instead of growing from the default 4 (#600).
        var result = new List<object>(16);
        pos++; // Skip '['

        // ParseArray -> ParseToken -> ParseArray recurses once per '['. With no
        // bound, a content stream of nothing but open brackets overflowed the
        // STACK — which .NET cannot catch, so it killed the process rather than
        // failing the extraction (#971). ~5,000 brackets (a 10 KB file) was
        // enough on a thread-pool thread, which is where the GUI's background
        // indexing and every xunit test run.
        if (++_arrayNestingDepth > MaxArrayNestingDepth)
        {
            _arrayNestingDepth--;
            throw new PdfParseException(
                $"Maximum nesting depth ({MaxArrayNestingDepth}) exceeded while extracting text from a content-stream array");
        }

        try
        {
            while (pos < content.Length)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SkipWhitespaceAndComments(content, ref pos);
                if (pos >= content.Length || content[pos] == ']')
                {
                    pos++;
                    break;
                }

                var item = ParseToken(content, ref pos);
                if (item != null)
                    result.Add(item);
            }
        }
        finally { _arrayNestingDepth--; }

        return result;
    }

    private string ParseName(string content, ref int pos)
    {
        pos++; // Skip '/'
        int segStart = pos;
        StringBuilder? sb = null; // only needed when a #XX escape occurs (rare)

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsWhiteSpace(c) || c == '/' || c == '[' || c == ']' ||
                c == '<' || c == '>' || c == '(' || c == ')' || c == '{' || c == '}')
                break;

            // Handle #XX hex escape
            if (c == '#' && pos + 2 < content.Length &&
                int.TryParse(content.AsSpan(pos + 1, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb ??= new StringBuilder();
                sb.Append(content, segStart, pos - segStart);
                sb.Append((char)code);
                pos += 3;
                segStart = pos;
                continue;
            }

            pos++;
        }

        if (sb == null)
            return string.Concat("/", content.AsSpan(segStart, pos - segStart));

        sb.Append(content, segStart, pos - segStart);
        return "/" + sb.ToString();
    }

    private object ParseNumber(string content, ref int pos)
    {
        int start = pos;
        bool hasDot = false;

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                if (c == '.') hasDot = true;
                pos++;
            }
            else
            {
                break;
            }
        }

        var span = content.AsSpan(start, pos - start);
        if (hasDot)
        {
            return double.TryParse(span, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }
        // Fast, exact integer parse for the common operand (TJ kerns, Td/Tm/cm
        // coordinates): a leading optional sign then ASCII digits. Avoids the
        // culture/NumberStyles machinery of int.TryParse (System.Number) on the
        // hot loop (#600). Bit-identical to the previous int.TryParse: a
        // sign-only, empty, embedded-sign, or out-of-int-range span reports
        // failure and falls through to int.TryParse (which returns 0), exactly
        // as before.
        if (TryParseAsciiInt(span, out var fast))
            return BoxInt(fast);
        return BoxInt(int.TryParse(span, out var i) ? i : 0);
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<char> span, out int value)
    {
        value = 0;
        if (span.Length == 0)
            return false;

        int idx = 0;
        bool negative = false;
        var first = span[0];
        if (first == '+' || first == '-')
        {
            negative = first == '-';
            idx = 1;
        }
        if (idx == span.Length)
            return false; // sign with no digits

        long acc = 0;
        for (; idx < span.Length; idx++)
        {
            uint digit = (uint)(span[idx] - '0');
            if (digit > 9)
                return false; // embedded '-', '+', '.', etc. — let int.TryParse decide
            acc = acc * 10 + digit;
            if (acc > (long)int.MaxValue + 1)
                return false; // overflow — fall back (int.TryParse yields 0)
        }

        if (negative)
            acc = -acc;
        if (acc < int.MinValue || acc > int.MaxValue)
            return false;

        value = (int)acc;
        return true;
    }

    private string ParseKeyword(string content, ref int pos)
    {
        int start = pos;

        while (pos < content.Length)
        {
            var c = content[pos];
            if (char.IsLetterOrDigit(c) || c == '\'' || c == '"' || c == '*')
                pos++;
            else
                break;
        }

        // Return the cached instance for known tokens (operators and common
        // keywords) — value-identical to the substring, but allocation-free
        // for the overwhelmingly common case (#600).
        return KnownKeywordLookup.TryGetValue(content.AsSpan(start, pos - start), out var known)
            ? known
            : content.Substring(start, pos - start);
    }

    private void SkipDictionary(string content, ref int pos)
    {
        // Advance pos EXACTLY as before (byte-identical bracket counting): the
        // parity gate relies on the fact that MCID capture never perturbs where
        // pos lands. MCID is read from the skipped [start, pos) span afterwards,
        // never interleaved with the walk (#776).
        int start = pos;
        int depth = 1;
        while (pos < content.Length && depth > 0)
        {
            // The third check point (#982). ContentStreamParser's twin is
            // ParseDictionary, which checks once per KEY; this walk has no
            // entries to count, so the check is per character — a multi-MB
            // dictionary token is one iteration of the operator loop above and
            // would otherwise be unabandonable.
            _cancellationToken.ThrowIfCancellationRequested();
            if (pos + 1 < content.Length)
            {
                if (content[pos] == '<' && content[pos + 1] == '<')
                {
                    depth++;
                    pos += 2;
                    continue;
                }
                if (content[pos] == '>' && content[pos + 1] == '>')
                {
                    depth--;
                    pos += 2;
                    continue;
                }
            }
            pos++;
        }

        _lastDictMcid = ScanForMcid(content, start, pos);
    }

    // Read the /MCID integer from an already-skipped dictionary span
    // [start, end) of the content string, or null if the dictionary carries no
    // /MCID (e.g. an /OC optional-content property dict). Pure lookahead over a
    // fixed span — advances nothing the caller sees (#776).
    private static int? ScanForMcid(string content, int start, int end)
    {
        int i = content.IndexOf("/MCID", start, System.Math.Max(0, end - start), System.StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += 5; // past "/MCID"
        while (i < end && char.IsWhiteSpace(content[i]))
            i++;
        int numStart = i;
        while (i < end && content[i] >= '0' && content[i] <= '9')
            i++;
        if (i == numStart)
            return null;
        return int.TryParse(content.AsSpan(numStart, i - numStart), out int mcid) ? mcid : null;
    }

    private static readonly HashSet<string> Operators = new()
    {
        // Text state
        "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts",
        // Text positioning
        "Td", "TD", "Tm", "T*",
        // Text showing
        "Tj", "TJ", "'", "\"",
        // Graphics state
        "q", "Q", "cm",
        // Path and other
        "m", "l", "c", "v", "y", "h", "re",
        "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*",
        "Do", "BI", "ID", "EI",
        "gs", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k",
        "d", "i", "j", "J", "M", "ri", "w",
        // Shading and Type 3 glyph metrics. Absent until #980: `sh` and
        // `d0`/`d1` were not recognised here, so `/Sh0 sh` left BOTH tokens on
        // the operand list and the NEXT operator read them as its own operands
        // — `/Sh0 sh 1 0 0 1 72 700 Tm` set the text matrix from
        // (0, 0, 1, 0, 0, 1) and moved every following letter to (0, 1).
        // ContentStreamParser has always recognised all three (#980).
        "sh", "d0", "d1",
        "BDC", "BMC", "EMC", "BX", "EX", "DP", "MP"
    };

    private static bool IsOperator(string token)
    {
        return Operators.Contains(token);
    }

    // Span-keyed lookup returning the cached string instance for known content
    // stream tokens, so ParseKeyword does not allocate a fresh string per
    // operator (#600). Covers every operator plus the non-operator keywords
    // that commonly appear as operands.
    private static readonly HashSet<string> KnownKeywords =
        new(Operators.Concat(new[] { "true", "false", "null" }));

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownKeywordLookup =
        KnownKeywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private void ExecuteOperator(string op, List<object> operands)
    {
        switch (op)
        {
            case "BT": // Begin text
                _tm_a = 1; _tm_b = 0; _tm_c = 0; _tm_d = 1; _tm_e = 0; _tm_f = 0;
                _tlm_e = 0; _tlm_f = 0;
                break;

            case "ET": // End text
                break;

            case "Tf": // Set font and size: fontName fontSize Tf
                if (operands.Count >= 2)
                {
                    _fontName = operands[0] is string name ? name.TrimStart('/') : "";
                    _fontSize = ToDouble(operands[1]);
                    _currentFont = ResolveFontFromActiveResources(_fontName);
                    LoadFontDerivedState();
                }
                break;

            case "Td": // Move to next line: tx ty Td
                if (operands.Count >= 2)
                {
                    var tx = ToDouble(operands[0]);
                    var ty = ToDouble(operands[1]);
                    // §9.4.2: Tlm' = [1 0 0 1 tx ty] × Tlm — the offset is in
                    // TEXT SPACE and must be composed through the matrix's
                    // linear part, exactly as ShowGlyph already does for §9.4.4
                    // glyph advances. The previous raw add (`e += tx`) was
                    // correct only for an unscaled, unrotated matrix; under the
                    // `/F1 1 Tf` + `10 0 0 10 Tm` idiom every line stepped
                    // 1.2pt instead of 12pt and STACKED onto the first —
                    // which is what made redaction destroy the lines below a
                    // match (#942) and marched letters off-page under flipped
                    // matrices (#899).
                    _tlm_e += tx * _tm_a + ty * _tm_c;
                    _tlm_f += tx * _tm_b + ty * _tm_d;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                }
                break;

            case "TD": // Move to next line and set leading: tx ty TD
                if (operands.Count >= 2)
                {
                    var tx = ToDouble(operands[0]);
                    var ty = ToDouble(operands[1]);
                    _textLeading = -ty;
                    // Same §9.4.2 composition as Td above.
                    _tlm_e += tx * _tm_a + ty * _tm_c;
                    _tlm_f += tx * _tm_b + ty * _tm_d;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                }
                break;

            case "Tm": // Set text matrix: a b c d e f Tm
                if (operands.Count >= 6)
                {
                    _tm_a = ToDouble(operands[0]);
                    _tm_b = ToDouble(operands[1]);
                    _tm_c = ToDouble(operands[2]);
                    _tm_d = ToDouble(operands[3]);
                    _tm_e = ToDouble(operands[4]);
                    _tm_f = ToDouble(operands[5]);
                    _tlm_e = _tm_e;
                    _tlm_f = _tm_f;
                }
                break;

            case "T*": // Move to start of next line
                // §9.4.2: T* ≡ `0 -TL Td` — compose (0, −TL) through the matrix.
                _tlm_e += -_textLeading * _tm_c;
                _tlm_f += -_textLeading * _tm_d;
                _tm_e = _tlm_e;
                _tm_f = _tlm_f;
                break;

            case "TL": // Set text leading
                if (operands.Count >= 1)
                    _textLeading = ToDouble(operands[0]);
                break;

            case "Tc": // Set character spacing
                if (operands.Count >= 1)
                    _charSpacing = ToDouble(operands[0]);
                break;

            case "Tw": // Set word spacing
                if (operands.Count >= 1)
                    _wordSpacing = ToDouble(operands[0]);
                break;

            case "Tz": // Set horizontal scaling
                if (operands.Count >= 1)
                    _horizontalScaling = ToDouble(operands[0]);
                break;

            case "Ts": // Set text rise (§9.3.7)
                // Recognised as an operator here since forever, but never
                // TRACKED: superscripts and subscripts were extracted on the
                // unrisen baseline while ContentStreamParser's operator bounds
                // carried the rise, so the two disagreed about where the same
                // glyph is. #980
                if (operands.Count >= 1)
                    _textRise = ToDouble(operands[0]);
                break;

            case "Tj": // Show text string
                if (operands.Count >= 1)
                {
                    if (operands[0] is string str)
                        ShowText(str);
                    else if (operands[0] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "TJ": // Show text with positioning
                if (operands.Count >= 1 && operands[0] is List<object> array)
                {
                    foreach (var item in array)
                    {
                        if (item is string str)
                            ShowText(str);
                        else if (item is byte[] bytes)
                            ShowText(bytes);
                        else if (item is int or double)
                        {
                            // TJ adjustment is subtracted from the coordinate of
                            // the WRITING direction (§9.4.3): horizontal → tx
                            // (scaled by Th), vertical → ty (no Th). #515
                            var adj = ToDouble(item);
                            if (_isVerticalWriting)
                            {
                                var ty = -(adj / 1000.0) * _fontSize;
                                _tm_e += ty * _tm_c;
                                _tm_f += ty * _tm_d;
                            }
                            else
                            {
                                _tm_e -= (adj / 1000.0) * _fontSize * (_horizontalScaling / 100.0);
                            }
                        }
                    }
                }
                break;

            case "'": // Move to next line and show text
                // §9.4.2: T* ≡ `0 -TL Td` — compose (0, −TL) through the matrix.
                _tlm_e += -_textLeading * _tm_c;
                _tlm_f += -_textLeading * _tm_d;
                _tm_e = _tlm_e;
                _tm_f = _tlm_f;
                if (operands.Count >= 1)
                {
                    if (operands[0] is string str)
                        ShowText(str);
                    else if (operands[0] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "\"": // Set word and char spacing, move to next line, show text
                if (operands.Count >= 3)
                {
                    _wordSpacing = ToDouble(operands[0]);
                    _charSpacing = ToDouble(operands[1]);
                    // §9.4.2: same composed line step as T*.
                    _tlm_e += -_textLeading * _tm_c;
                    _tlm_f += -_textLeading * _tm_d;
                    _tm_e = _tlm_e;
                    _tm_f = _tlm_f;
                    if (operands[2] is string str)
                        ShowText(str);
                    else if (operands[2] is byte[] bytes)
                        ShowText(bytes);
                }
                break;

            case "q": // Save graphics state
                // §8.4.1 Table 52 puts the TEXT state parameters — font + size,
                // Tc, Tw, Tz, TL, Ts, Tr — in the GRAPHICS state, so `q` saves
                // them too. This struct was SIX DOUBLES until #983: a run
                // bracketed in q/Q leaked its font size, spacing and leading
                // into everything after the `Q`, which then mis-sized every
                // following letter's cell and (via LetterFinder) the geometry
                // redaction removes on. mutool restores them; so does
                // GlyphRemover.TextStateTracker.
                _stateStack.Push(new GraphicsState
                {
                    ctm_a = _ctm_a, ctm_b = _ctm_b, ctm_c = _ctm_c, ctm_d = _ctm_d, ctm_e = _ctm_e, ctm_f = _ctm_f,
                    fontName = _fontName,
                    fontSize = _fontSize,
                    currentFont = _currentFont,
                    // The font-DERIVED maps travel with the font: restoring the
                    // name and size alone would leave the extractor decoding
                    // through the bracketed font's ToUnicode/CID maps while
                    // reporting the outer font's name.
                    fontState = CaptureFontState(),
                    textLeading = _textLeading,
                    charSpacing = _charSpacing,
                    wordSpacing = _wordSpacing,
                    horizontalScaling = _horizontalScaling,
                    textRise = _textRise
                });
                break;

            case "Q": // Restore graphics state
                if (_stateStack.Count > 0)
                {
                    var state = _stateStack.Pop();
                    _ctm_a = state.ctm_a; _ctm_b = state.ctm_b; _ctm_c = state.ctm_c;
                    _ctm_d = state.ctm_d; _ctm_e = state.ctm_e; _ctm_f = state.ctm_f;
                    _fontName = state.fontName;
                    _fontSize = state.fontSize;
                    _currentFont = state.currentFont;
                    ApplyFontState(state.fontState);
                    _textLeading = state.textLeading;
                    _charSpacing = state.charSpacing;
                    _wordSpacing = state.wordSpacing;
                    _horizontalScaling = state.horizontalScaling;
                    _textRise = state.textRise;
                }
                break;

            case "cm": // Modify current transformation matrix
                if (operands.Count >= 6)
                {
                    ConcatenateCtm(
                        ToDouble(operands[0]),
                        ToDouble(operands[1]),
                        ToDouble(operands[2]),
                        ToDouble(operands[3]),
                        ToDouble(operands[4]),
                        ToDouble(operands[5]));
                }
                break;

            case "Do":
                if (operands.Count >= 1 && operands[0] is string xObjectName)
                    ExtractFormXObjectText(xObjectName.TrimStart('/'));
                break;

            case "BDC":
                {
                    var hidden = IsHiddenOptionalContentSpan(operands);
                    _optionalContentHiddenStack.Push(hidden);
                    if (hidden)
                        _hiddenOptionalContentDepth++;
                    // #776: a BDC's /MCID comes from an inline properties dict
                    // (captured into _lastDictMcid while skipping it) or a named
                    // /Properties reference. Either sets this span's MCID; else
                    // it inherits the enclosing level's.
                    PushMcid(_lastDictMcid ?? ResolveNamedPropertyMcid(operands));
                }
                break;

            case "BMC":
                _optionalContentHiddenStack.Push(false);
                PushMcid(null); // tag-only span carries no /MCID
                break;

            case "EMC":
                if (_optionalContentHiddenStack.Count > 0)
                {
                    if (_optionalContentHiddenStack.Pop())
                        _hiddenOptionalContentDepth--;
                    PopMcid();
                }
                break;
        }
    }

    // Push the effective MCID for a newly-opened marked-content span (#776):
    // its own /MCID when it has one, else it inherits the enclosing level's so
    // nested untagged spans (e.g. a /OC toggle inside a tagged paragraph) do not
    // orphan the glyphs from their structure element.
    private void PushMcid(int? spanMcid)
    {
        int? effective = spanMcid ?? _currentMcid;
        _mcidStack.Push(effective);
        _currentMcid = effective;
    }

    private void PopMcid()
    {
        if (_mcidStack.Count > 0)
            _mcidStack.Pop();
        _currentMcid = _mcidStack.Count > 0 ? _mcidStack.Peek() : null;
    }

    // Resolve a BDC whose properties operand is a NAME referencing an entry in
    // the resource dictionary's /Properties (§14.6.2), returning that entry's
    // /MCID. Inline-dict MCIDs are handled earlier via _lastDictMcid; this
    // covers the indirect form /Tag /P1 BDC. Returns null when absent.
    private int? ResolveNamedPropertyMcid(List<object> operands)
    {
        if (operands.Count < 2 || operands[1] is not string propertyName)
            return null;

        string name = propertyName.TrimStart('/');
        foreach (var resources in _resourcesStack)
        {
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null)
                continue;
            if (_page.Document.Resolve(propertiesObj) is not PdfDictionary properties)
                continue;
            var propObj = properties.GetOptional(name);
            if (propObj == null)
                continue;
            if (_page.Document.Resolve(propObj) is PdfDictionary propDict
                && propDict.GetOptional("MCID") is { } mcidObj
                && _page.Document.Resolve(mcidObj) is PdfInteger mcidInt)
                return (int)mcidInt.Value;
        }
        return null;
    }

    private bool IsHiddenOptionalContentSpan(List<object> operands)
    {
        if (operands.Count < 2)
            return false;

        if (operands[0] is not string tag || tag != "/OC")
            return false;

        if (operands[1] is not string propertyName)
            return false;

        return IsHiddenOptionalContentProperty(propertyName.TrimStart('/'));
    }

    private bool IsHiddenOptionalContentProperty(string propertyName)
    {
        foreach (var resources in _resourcesStack)
        {
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null)
                continue;

            if (_page.Document.Resolve(propertiesObj) is not PdfDictionary properties)
                continue;

            var propertyObj = properties.GetOptional(propertyName);
            if (propertyObj == null)
                continue;

            if (IsHiddenOptionalContentObject(propertyObj))
                return true;
        }

        return false;
    }

    // Resolve default-configuration visibility of an /OC property object via the
    // shared resolver so extraction agrees with the renderer on hidden layers.
    // Handles OCG (reference-based OFF/ON/BaseState), OCMD (/P policy and /VE
    // And/Or/Not visibility expressions), and nested /OC. See issue #336.
    // The property object is passed un-resolved so reference identity is
    // preserved for /OFF and /ON array matching.
    private bool IsHiddenOptionalContentObject(PdfObject obj)
        => !Excise.Core.Document.OptionalContentVisibility.IsVisibleByDefault(_page.Document, obj);

    private void ConcatenateCtm(double a, double b, double c, double d, double e, double f)
    {
        // Multiply: CTM = new_matrix * CTM
        var na = a * _ctm_a + b * _ctm_c;
        var nb = a * _ctm_b + b * _ctm_d;
        var nc = c * _ctm_a + d * _ctm_c;
        var nd = c * _ctm_b + d * _ctm_d;
        var ne = e * _ctm_a + f * _ctm_c + _ctm_e;
        var nf = e * _ctm_b + f * _ctm_d + _ctm_f;

        _ctm_a = na; _ctm_b = nb; _ctm_c = nc;
        _ctm_d = nd; _ctm_e = ne; _ctm_f = nf;
    }

    private void ExtractFormXObjectText(string name)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        var xObject = ResolveXObjectFromActiveResources(name);
        if (xObject is not PdfStream stream || stream.GetNameOrNull("Subtype") != "Form")
            return;

        ExtractFormXObjectContent(stream);
    }

    /// <summary>
    /// Parses a resolved Form XObject stream's content, applying its
    /// <c>/Matrix</c> and pushing its <c>/Resources</c> (mirroring §8.10.1's
    /// <c>q &lt;Matrix&gt; cm … Q</c> semantics), and restoring all graphics
    /// and text state afterward. Shared by <see cref="ExtractFormXObjectText"/>
    /// (a <c>Do</c> operator invoking a form named in the active resources)
    /// and <see cref="ExtractWidgetAppearanceText"/> (a Widget annotation's
    /// <c>/AP/N</c> appearance stream, reached without going through a page
    /// content-stream <c>Do</c> at all — #669).
    /// </summary>
    private void ExtractFormXObjectContent(PdfStream stream)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        if (!_formXObjectStack.Add(stream))
            return;

        _formXObjectDepth++;
        var savedCtm = (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f);
        // The decoder replaces the lone _toUnicodeMap this tuple used to save:
        // the other six cascade fields were NOT restored, so a form XObject's
        // /Differences or embedded-cmap font leaked into the page's decoding
        // after the Do while its /ToUnicode did not — a state the font
        // dictionary never described. One object, restored or not, cannot
        // desynchronise that way (#981).
        var savedTextState = (
            _fontSize,
            _fontName,
            _currentFont,
            _decoder,
            _textLeading,
            _charSpacing,
            _wordSpacing,
            _horizontalScaling,
            _textRise,
            _is2ByteFont,
            _isVerticalWriting,
            _cidMetrics,
            _tm_a,
            _tm_b,
            _tm_c,
            _tm_d,
            _tm_e,
            _tm_f,
            _tlm_e,
            _tlm_f);
        var pushedResources = false;

        try
        {
            if (TryReadMatrix(stream.GetOptional("Matrix"), out var matrix))
                ConcatenateCtm(matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);

            var resourcesObj = stream.GetOptional("Resources");
            if (resourcesObj != null &&
                _page.Document.Resolve(resourcesObj) is PdfDictionary resources)
            {
                _resourcesStack.Push(resources);
                pushedResources = true;
            }

            ParseContentBytes(stream.DecodedData);
        }
        finally
        {
            if (pushedResources)
                _resourcesStack.Pop();
            (_ctm_a, _ctm_b, _ctm_c, _ctm_d, _ctm_e, _ctm_f) = savedCtm;
            (
                _fontSize,
                _fontName,
                _currentFont,
                _decoder,
                _textLeading,
                _charSpacing,
                _wordSpacing,
                _horizontalScaling,
                _textRise,
                _is2ByteFont,
                _isVerticalWriting,
                _cidMetrics,
                _tm_a,
                _tm_b,
                _tm_c,
                _tm_d,
                _tm_e,
                _tm_f,
                _tlm_e,
                _tlm_f) = savedTextState;
            _formXObjectDepth--;
            _formXObjectStack.Remove(stream);
        }
    }

    private PdfDictionary? ResolveFontFromActiveResources(string fontName)
    {
        foreach (var resources in _resourcesStack)
        {
            var fontsObj = resources.GetOptional("Font");
            if (fontsObj == null) continue;
            if (_page.Document.Resolve(fontsObj) is not PdfDictionary fonts)
                continue;
            var fontObj = fonts.GetOptional(fontName);
            if (fontObj == null) continue;
            return _page.Document.Resolve(fontObj) as PdfDictionary;
        }

        return _page.GetFont(fontName);
    }

    private PdfObject? ResolveXObjectFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            var xObjectsObj = resources.GetOptional("XObject");
            if (xObjectsObj == null) continue;
            if (_page.Document.Resolve(xObjectsObj) is not PdfDictionary xObjects)
                continue;
            var xObject = xObjects.GetOptional(name);
            if (xObject == null) continue;
            return _page.Document.Resolve(xObject);
        }

        return _page.GetXObject(name);
    }

    private static bool TryReadMatrix(PdfObject? matrixObj, out (double a, double b, double c, double d, double e, double f) matrix)
    {
        matrix = (1, 0, 0, 1, 0, 0);
        if (matrixObj is not PdfArray array || array.Count < 6)
            return false;

        if (!TryNumber(array[0], out var a) ||
            !TryNumber(array[1], out var b) ||
            !TryNumber(array[2], out var c) ||
            !TryNumber(array[3], out var d) ||
            !TryNumber(array[4], out var e) ||
            !TryNumber(array[5], out var f))
        {
            return false;
        }

        matrix = (a, b, c, d, e, f);
        return true;
    }

    private void ShowText(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        ShowText(bytes);
    }

    private void ShowText(byte[] bytes)
    {
        // Registered (predefined) encoding CMap (#515): the CMap's own
        // codespace ranges drive the byte segmentation — 90ms-RKSJ-H mixes
        // 1-byte and 2-byte codes, which a fixed stride would garble by
        // pairing unrelated bytes — and each code maps to its CID for width
        // lookup and CID→Unicode decoding.
        if (_registeredEncodingCMap != null)
        {
            foreach (var (code, cid, byteLength) in _registeredEncodingCMap.DecodeDetailed(bytes))
                ShowGlyph(code, cid, byteLength);
            return;
        }

        // Type 0 / composite fonts use multi-byte source codes. The descendant
        // CIDFont's encoding (or the outer ToUnicode CMap) tells us how many
        // bytes per code; in practice every modern producer uses 2 bytes for
        // Identity-H/V, so we treat the font as 2-byte if the Tf-loaded font
        // has Subtype /Type0. Simple fonts (Type1/TrueType not wrapped in a
        // Type 0 / CIDFont) stay 1-byte.
        int stride = _is2ByteFont ? 2 : 1;

        for (int i = 0; i + stride <= bytes.Length; i += stride)
        {
            int charCode = stride == 2
                ? (bytes[i] << 8) | bytes[i + 1]
                : bytes[i];

            // Outside a registered CMap, the source code doubles as the CID
            // (Identity-H/V and simple fonts alike).
            ShowGlyph(charCode, charCode, stride);
        }
    }

    /// <summary>
    /// Emits one letter for a decoded source <paramref name="charCode"/> and
    /// advances the text matrix. <paramref name="cid"/> is the CID the code
    /// maps to (equal to the code except under a registered encoding CMap);
    /// widths are CID-keyed per §9.7.4.3. <paramref name="byteLength"/> is the
    /// number of content-stream bytes the code occupied, preserved on the
    /// Letter so redaction can re-encode kept glyphs byte-exactly.
    /// </summary>
    private void ShowGlyph(int charCode, int cid, int byteLength)
    {
        var unicode = _decoder.Decode(charCode, cid, _registeredCidToUnicode);
        var charWidth = GetCharWidth(cid);

        // Calculate position in user space. §9.4.4 puts the text rise in the
        // text rendering matrix as the translation (0, Ts) INSIDE Tm, so it is
        // a text-space offset composed through the matrix's linear part — never
        // added raw to the user-space corner. #980
        var (x, y) = TransformPoint(
            _tm_e + _textRise * _tm_c,
            _tm_f + _textRise * _tm_d);

        // Glyph advance & ascent in TEXT space. Th (horizontal scaling) applies
        // only in horizontal writing (§9.2.4/§9.4.4).
        var advanceTextSpace = _isVerticalWriting
            ? charWidth * _fontSize / 1000.0
            : charWidth * _fontSize * (_horizontalScaling / 100.0) / 1000.0;
        var ascentTextSpace = _fontSize;

        // #833: map width & height VECTORS through the text-matrix × CTM linear
        // parts, then take the axis-aligned extent. The old code added the raw
        // TEXT-space scalars onto the USER-space corner, dropping the matrix
        // scale — so the ubiquitous `1 Tf … s 0 0 s Tm` producer idiom (unit font
        // size, size carried by the text matrix) yielded ~0-size boxes while the
        // pen advance (which DOES apply the matrix) kept positions correct. For
        // ordinary `s Tf … 1 0 0 1 Tm` text (tm_a=1, ctm_a=1) this is a no-op.
        var (wx, wy) = TransformVector(advanceTextSpace, 0);
        var (hx, hy) = TransformVector(0, ascentTextSpace);
        var glyphWidth = Math.Sqrt(wx * wx + wy * wy);

        PdfRectangle bbox;
        if (_isVerticalWriting)
        {
            // §9.7.4.3: the pen is the VERTICAL origin; v = (vx, vy) from /W2
            // locates the horizontal origin at pen − v and the cell spans DOWN
            // by w1y. Dimensions now carry the matrix scale (#833).
            var vm = GetVerticalMetrics(cid);
            var (vxx, _) = TransformVector(vm.Vx * _fontSize / 1000.0, 0);
            var (chx, chy) = TransformVector(0, Math.Abs(vm.W1Y) * _fontSize / 1000.0);
            var cellHeight = Math.Sqrt(chx * chx + chy * chy);
            if (cellHeight <= 0) cellHeight = Math.Abs(hy) > 0 ? Math.Abs(hy) : glyphWidth;
            var vX = x - vxx;
            var vY = y - cellHeight;
            bbox = new PdfRectangle(vX, vY, vX + glyphWidth, y);
        }
        else
        {
            bbox = AxisAlignedBox(x, y, wx, wy, hx, hy);
        }

        var letter = new Letter(
            unicode,
            bbox,
            _fontSize,
            _fontName,
            x,
            y,
            glyphWidth,
            charCode,
            byteLength // 1 for simple fonts and 1-byte-codespace Type0 (#659), 2 for Identity-H/V; per-code under a mixed-width registered CMap (#515)
        )
        {
            // O(1) counter kept in lock-step with _optionalContentHiddenStack;
            // equivalent to _optionalContentHiddenStack.Any(hidden => hidden)
            // without the per-letter enumeration (#600).
            IsInHiddenOptionalContent = _hiddenOptionalContentDepth > 0,
            IsCidFont = _isCidFont,
            // #776: the innermost enclosing /MCID span, for the a11y bridge.
            MarkedContentId = _currentMcid
        };
        _letters.Add(letter);

        // Advance text position (§9.4.4). Word spacing applies only to the
        // SINGLE-BYTE code 32 — a 2-byte <0020> in a CID font must not fire
        // it (§9.3.3).
        var spacing = _charSpacing;
        if (byteLength == 1 && charCode == 32)
            spacing += _wordSpacing;

        if (_isVerticalWriting)
        {
            // ty = w1y·Tfs + Tc + Tw — the per-CID vertical displacement from
            // /W2, else /DW2 (default −1000 → down the page). No Th: horizontal
            // scaling applies only to horizontal displacement (§9.4.4). #515
            var vm = GetVerticalMetrics(cid);
            var ty = (vm.W1Y / 1000.0) * _fontSize + spacing;
            _tm_e += ty * _tm_c;
            _tm_f += ty * _tm_d;
        }
        else
        {
            // tx = (w0·Tfs + Tc + Tw)·Th — the spacing terms sit INSIDE the
            // horizontal-scaling factor (§9.4.4), same for simple and Type0
            // fonts. Bit-identical to the old form when Th = 100. #734
            var tx = ((charWidth / 1000.0) * _fontSize + spacing) * (_horizontalScaling / 100.0);
            _tm_e += tx * _tm_a;
            _tm_f += tx * _tm_b;
        }
    }

    /// <summary>
    /// Vertical metrics for <paramref name="cid"/> — the /W2 entry, else the
    /// §9.7.4.3 defaults (w1y from /DW2, v = (w0∕2, DW2[0])). A Type0 font
    /// with no descendant metrics at all gets the spec defaults.
    /// </summary>
    private Fonts.CidVerticalMetrics GetVerticalMetrics(int cid)
        => _cidMetrics?.GetVerticalMetrics(cid)
            ?? new Fonts.CidVerticalMetrics(
                Fonts.CidFontWidths.SpecDefaultVerticalDisplacement,
                GetCharWidth(cid) / 2,
                Fonts.CidFontWidths.SpecDefaultVerticalOriginY);

    // Every derived field below (ToUnicode map, /Differences, the Identity /
    // Mac-order / embedded-CID / symbol flags, and all of LoadFontGeometry's
    // CID/CMap/width state) is a pure function of the resolved font dictionary.
    // But Tf recurs constantly — every text block re-issues it — so
    // recomputing them per call (re-parsing ToUnicode/CMap streams and the /W
    // width table, which is the LoadFontGeometry inclusive hotspot and the
    // source of the Double[] growth inside those parsers) was a large share of
    // extraction cost. Cache the computed state keyed by font-dict reference —
    // PdfDocument.Resolve returns a stable instance per object number, so the
    // same font hits — and re-ASSIGN every field on each Tf via a snapshot.
    //
    // Deliberately a snapshot/restore, NOT a "skip reload if same font"
    // short-circuit: ExtractFormXObjectContent saves/restores only a SUBSET of
    // these fields (not /Differences, the Identity/Mac-order/embedded/symbol
    // flags, _isCidFont, or the registered CMap/CID→Unicode maps), and the
    // unconditional reload at the next Tf is what HEALS that partial restore.
    // Re-assigning all fields from the cached snapshot reproduces that exactly;
    // a skip-if-same would leave the un-restored fields stale after any form
    // XObject and change output. #600.
    private readonly Dictionary<PdfDictionary, FontState> _fontStateCache =
        new(ReferenceEqualityComparer.Instance);

    private void LoadFontDerivedState()
    {
        if (_currentFont != null && _fontStateCache.TryGetValue(_currentFont, out var cached))
        {
            ApplyFontState(cached);
            return;
        }

        // Cache miss (or a null font, which is never cached). The whole
        // code→Unicode cascade now comes from one shared object (#981), keyed
        // by the font dictionary so it survives repeated Tf of the same font
        // even when the FontState cache misses.
        _decoder = GetDecoder(_currentFont);
        LoadFontGeometry();

        if (_currentFont != null)
            _fontStateCache[_currentFont] = CaptureFontState();
    }

    /// <summary>
    /// The shared decode cascade for one font dictionary, built once per
    /// TextExtractor. A null font decodes as WinAnsi, which is what happened
    /// before a /Tf resolved.
    /// </summary>
    private GlyphUnicodeDecoder GetDecoder(PdfDictionary? font)
    {
        if (font == null)
            return GlyphUnicodeDecoder.None;
        if (_decoderCache.TryGetValue(font, out var cached))
            return cached;
        var decoder = GlyphUnicodeDecoder.Build(_page.Document, font);
        _decoderCache[font] = decoder;
        return decoder;
    }

    private FontState CaptureFontState() => new(
        _decoder,
        _is2ByteFont,
        _isCidFont,
        _isVerticalWriting,
        _cidMetrics,
        _registeredEncodingCMap,
        _registeredCidToUnicode);

    private void ApplyFontState(in FontState s)
    {
        _decoder = s.Decoder;
        _is2ByteFont = s.Is2ByteFont;
        _isCidFont = s.IsCidFont;
        _isVerticalWriting = s.IsVerticalWriting;
        _cidMetrics = s.CidMetrics;
        _registeredEncodingCMap = s.RegisteredEncodingCMap;
        _registeredCidToUnicode = s.RegisteredCidToUnicode;
    }

    // Snapshot of the size-independent per-font state derived from one font
    // dictionary (#600). _fontName/_fontSize are the only genuinely per-Tf
    // fields and are set by the Tf handler, not stored here.
    private readonly record struct FontState(
        GlyphUnicodeDecoder Decoder,
        bool Is2ByteFont,
        bool IsCidFont,
        bool IsVerticalWriting,
        Fonts.CidFontWidths? CidMetrics,
        CidCMap? RegisteredEncodingCMap,
        IReadOnlyDictionary<int, string>? RegisteredCidToUnicode);

    /// <summary>
    /// After Tf loads a font, update Type 0 / CID-specific state: 2-byte stride,
    /// vertical writing mode, default CID width, and per-CID width table.
    /// </summary>
    private void LoadFontGeometry()
    {
        _is2ByteFont = false;
        _isCidFont = false;
        _isVerticalWriting = false;
        _cidMetrics = null;
        _registeredEncodingCMap = null;
        _registeredCidToUnicode = null;

        if (_currentFont == null) return;

        var subtype = _currentFont.GetNameOrNull("Subtype");
        if (subtype != "Type0") return;

        _is2ByteFont = true;
        _isCidFont = true;

        // /Encoding can be a name (Identity-H/V) or a CMap stream. Identity-V
        // and any /WMode 1 in a custom CMap means vertical writing.
        var encObj = _page.Document.Resolve(_currentFont.GetOptional("Encoding") ?? PdfNull.Instance);
        if (encObj is PdfName encName && encName.Value == "Identity-V")
            _isVerticalWriting = true;

        // Registered (predefined) CMap NAME as /Encoding (#515 slice 2), e.g.
        // /UniGB-UCS2-H or /90ms-RKSJ-H: load the embedded code→CID CMap. The
        // vertical -V variants set writing mode 1 like Identity-V does.
        if (encObj is PdfName registeredName
            && registeredName.Value is not ("Identity-H" or "Identity-V")
            && PredefinedCMapProvider.TryGetEncodingCMap(registeredName.Value) is { } registeredCMap)
        {
            _registeredEncodingCMap = registeredCMap;
            if (PredefinedCMapProvider.IsVertical(registeredName.Value))
                _isVerticalWriting = true;
        }

        // An embedded /Encoding CMap STREAM is the font's code→CID map
        // (§9.7.6.2) exactly like a registered CMap name: its codespace
        // ranges drive byte segmentation (including mixed 1/2-byte widths,
        // per-byte-range matched) and its cidchar/cidrange entries give the
        // CID for width lookup and CID→Unicode decoding. Previously only its
        // /WMode and a uniform-1-byte heuristic (#659) were honored while
        // the bytes were still decoded as fixed-stride identity codes — but
        // the RENDERER already decodes through the parsed CMap, so what was
        // extracted (and therefore what redaction could match) drifted from
        // what was drawn. #515
        if (encObj is PdfStream encStream)
        {
            try
            {
                var embedded = CidCMap.Parse(encStream.DecodedData,
                    static name => PredefinedCMapProvider.TryGetEncodingCMap(name));
                if (embedded.WMode == 1)
                    _isVerticalWriting = true;
                if (embedded.CodespaceRanges.Count > 0 || embedded.Mapping.Count > 0)
                    _registeredEncodingCMap = embedded;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Best-effort: an unreadable CMap keeps the identity defaults.
            }

            // Stride fallback for streams the CMap parser could not read
            // (#659): an EXPLICITLY UNIFORM 1-byte codespace decodes one
            // byte at a time; anything else keeps the safe 2-byte default.
            if (_registeredEncodingCMap == null)
            {
                var detail = ToUnicodeCMapParser.ParseDetailed(encStream.DecodedData);
                if (detail.CodespaceRanges.Count > 0 && detail.MaxCodeBytes == 1)
                    _is2ByteFont = false;
            }
        }

        // Resolve descendant CIDFont (always exactly one entry, per §9.7.6.1).
        var descendantsObj = _currentFont.GetOptional("DescendantFonts");
        if (descendantsObj == null) return;
        var descendantsResolved = _page.Document.Resolve(descendantsObj);
        if (descendantsResolved is not PdfArray descendants || descendants.Count == 0) return;

        var firstResolved = _page.Document.Resolve(descendants[0]);
        if (firstResolved is not PdfDictionary cidFont) return;

        // CID→Unicode via the registered Adobe-<Ordering>-UCS2 CMap selected
        // from the descendant's /CIDSystemInfo (PDF §9.10.2 method (b)); #515.
        // Fires for registered encoding CMaps AND for Identity-H/V fonts whose
        // CIDSystemInfo names a known ordering (there code == CID). A
        // /ToUnicode STREAM built _toUnicodeMap and wins outright; a
        // /ToUnicode /Identity-H|V name is fully handled by _toUnicodeIdentity;
        // a /ToUnicode that is a REGISTERED CMap name (#715) contributes its
        // ordering here instead — treating it as a code-keyed map would be
        // wrong whenever the font's encoding is not that same CMap.
        if (!_decoder.HasToUnicodeStreamMap && !_decoder.ToUnicodeIsIdentityName)
        {
            // First signal that yields a shipped map wins — an Ordering with no
            // companion CMap (notably "Identity") must not mask a later signal.
            _registeredCidToUnicode =
                TryLoadOrderingMap(GetCidSystemInfoOrdering(cidFont))
                ?? TryLoadOrderingMap(encObj is PdfName n
                    ? PredefinedCMapProvider.GetOrderingForEncodingCMap(n.Value) : null)
                ?? TryLoadOrderingMap(GetToUnicodeRegisteredOrdering(_currentFont));
        }

        // /DW, /W, /DW2, /W2 — the CID-keyed metrics tables (§9.7.4.3),
        // parsed by the shared hardened parser (#515).
        _cidMetrics = Fonts.CidFontWidths.Parse(cidFont, _page.Document.Resolve);
    }

    /// <summary>
    /// The /Ordering string of the descendant CIDFont's /CIDSystemInfo (e.g.
    /// "Japan1", "GB1"), or null when absent/unreadable. Identifies which
    /// registered character collection the font's CIDs index — the key that
    /// selects the Adobe-&lt;Ordering&gt;-UCS2 CID→Unicode CMap (§9.10.2). #515
    /// </summary>
    private static IReadOnlyDictionary<int, string>? TryLoadOrderingMap(string? ordering)
        => ordering == null ? null : PredefinedCMapProvider.TryGetCidToUnicodeMap(ordering);

    private string? GetCidSystemInfoOrdering(PdfDictionary cidFont)
    {
        if (_page.Document.Resolve(cidFont.GetOptional("CIDSystemInfo") ?? PdfNull.Instance)
            is not PdfDictionary systemInfo)
            return null;

        return _page.Document.Resolve(systemInfo.GetOptional("Ordering") ?? PdfNull.Instance)
            is PdfString ordering ? ordering.Value : null;
    }

    /// <summary>
    /// When /ToUnicode is a registered encoding-CMap NAME (a producer quirk;
    /// #715), the name still identifies the ordering (UniGB-UCS2-H → GB1),
    /// which is the only reliable information it carries — the CIDs to decode
    /// come from the font's own /Encoding, not from this CMap's code space.
    /// </summary>
    private string? GetToUnicodeRegisteredOrdering(PdfDictionary? font)
    {
        if (font == null)
            return null;
        var toUnicode = _page.Document.Resolve(font.GetOptional("ToUnicode") ?? PdfNull.Instance);
        return toUnicode is PdfName name
            ? PredefinedCMapProvider.GetOrderingForEncodingCMap(name.Value)
            : null;
    }

    private static bool TryNumber(PdfObject? obj, out double v)
    {
        switch (obj)
        {
            case PdfInteger i: v = i.Value; return true;
            case PdfReal r:    v = r.Value; return true;
            default:           v = 0; return false;
        }
    }

    private (double x, double y) TransformPoint(double tx, double ty)
    {
        // Apply text matrix
        var x1 = tx;
        var y1 = ty;

        // Apply CTM
        var x2 = x1 * _ctm_a + y1 * _ctm_c + _ctm_e;
        var y2 = x1 * _ctm_b + y1 * _ctm_d + _ctm_f;

        return (x2, y2);
    }

    /// <summary>
    /// Map a text-space DISPLACEMENT vector (vx, vy) into user space through the
    /// LINEAR part of the text matrix and then the CTM (no translation). Glyph
    /// width/height are displacements, not points, so they must go through this —
    /// not be added raw onto a user-space corner (#833). Consistent with the pen
    /// advance, which already applies the text-matrix linear part.
    /// </summary>
    private (double dx, double dy) TransformVector(double vx, double vy)
    {
        var tx = vx * _tm_a + vy * _tm_c;
        var ty = vx * _tm_b + vy * _tm_d;
        return (tx * _ctm_a + ty * _ctm_c, tx * _ctm_b + ty * _ctm_d);
    }

    /// <summary>
    /// Axis-aligned bounding box of the parallelogram spanned from origin
    /// (<paramref name="ox"/>,<paramref name="oy"/>) by the width vector
    /// (wx, wy) and height vector (hx, hy). For axis-aligned text (wy = hx = 0)
    /// this is exactly (ox, oy, ox+wx, oy+hy); for rotated/skewed runs it is the
    /// tight AABB of the rotated glyph cell.
    /// </summary>
    private static PdfRectangle AxisAlignedBox(double ox, double oy, double wx, double wy, double hx, double hy)
    {
        double x2 = ox + wx, x3 = ox + hx, x4 = ox + wx + hx;
        double y2 = oy + wy, y3 = oy + hy, y4 = oy + wy + hy;
        double left = Math.Min(Math.Min(ox, x2), Math.Min(x3, x4));
        double right = Math.Max(Math.Max(ox, x2), Math.Max(x3, x4));
        double bottom = Math.Min(Math.Min(oy, y2), Math.Min(y3, y4));
        double top = Math.Max(Math.Max(oy, y2), Math.Max(y3, y4));
        return new PdfRectangle(left, bottom, right, top);
    }

    private double GetCharWidth(int charCode)
    {
        // Type 0 / CIDFont: width comes from the /W table on the descendant font,
        // falling back to /DW when the CID is unlisted (§9.7.4.3).
        if (_is2ByteFont)
            return _cidMetrics?.GetWidth(charCode) ?? Fonts.CidFontWidths.SpecDefaultWidth;

        // Try to get width from font dictionary
        if (_currentFont != null)
        {
            // Check if font has Widths array. /Widths is an INDIRECT reference in
            // every TeX/dvips PDF, so it must be resolved — a bare `is PdfArray`
            // cast on the raw value fails there and silently falls through to the
            // 600 default, giving every glyph one flat width (#843).
            var widthsObj = _page.Document.Resolve(_currentFont.GetOptional("Widths") ?? PdfNull.Instance);
            if (widthsObj is PdfArray widths)
            {
                var firstChar = _currentFont.GetInt("FirstChar", 0);
                var lastChar = _currentFont.GetInt("LastChar", 255);

                if (charCode >= firstChar && charCode <= lastChar)
                {
                    var index = charCode - firstChar;
                    if (index < widths.Count)
                    {
                        return widths.GetNumber(index);
                    }
                }
            }

            // Check for MissingWidth in FontDescriptor
            var fontDescriptor = _currentFont.GetDictionaryOrNull("FontDescriptor");
            if (fontDescriptor != null)
            {
                var missingWidth = fontDescriptor.GetNumber("MissingWidth", 0);
                if (missingWidth > 0)
                    return missingWidth;
            }

            // For standard Type1 fonts without Widths, use built-in metrics
            var baseFont = _currentFont.GetNameOrNull("BaseFont");
            if (baseFont != null)
            {
                return GetStandardFontWidth(baseFont, charCode);
            }
        }

        // Default width for standard fonts
        return 600; // Approximate average width
    }

    /// <summary>
    /// Get character width for standard Type1 fonts (Helvetica, Times, Courier, etc.)
    /// Shared with <see cref="Excise.Core.Content.ContentStreamParser"/> rather
    /// than duplicated: a second copy is exactly how the two state machines
    /// drift apart, and a width disagreement moves the glyph cells redaction
    /// matches against (#980).
    /// </summary>
    internal static double GetStandardFontWidth(string baseFont, int charCode)
    {
        // Standard 14 fonts have fixed-width or variable-width glyphs
        // For Courier (monospace), all glyphs are 600 units wide
        if (baseFont.StartsWith("Courier"))
            return 600;

        // For Helvetica and Times, widths vary
        // These are approximate averages
        if (baseFont.StartsWith("Helvetica"))
        {
            return charCode switch
            {
                32 => 278,  // space
                65 => 667,  // A
                66 => 667,  // B
                67 => 722,  // C
                68 => 722,  // D
                69 => 667,  // E
                70 => 611,  // F
                71 => 778,  // G
                72 => 722,  // H
                73 => 278,  // I
                74 => 500,  // J
                75 => 667,  // K
                76 => 556,  // L
                77 => 833,  // M
                78 => 722,  // N
                79 => 778,  // O
                80 => 667,  // P
                81 => 778,  // Q
                82 => 722,  // R
                83 => 667,  // S
                84 => 611,  // T
                85 => 722,  // U
                86 => 667,  // V
                87 => 944,  // W
                88 => 667,  // X
                89 => 667,  // Y
                90 => 611,  // Z
                97 => 556,  // a
                98 => 556,  // b
                99 => 500,  // c
                100 => 556, // d
                101 => 556, // e
                102 => 278, // f
                103 => 556, // g
                104 => 556, // h
                105 => 222, // i
                106 => 222, // j
                107 => 500, // k
                108 => 222, // l
                109 => 833, // m
                110 => 556, // n
                111 => 556, // o
                112 => 556, // p
                113 => 556, // q
                114 => 333, // r
                115 => 500, // s
                116 => 278, // t
                117 => 556, // u
                118 => 500, // v
                119 => 722, // w
                120 => 500, // x
                121 => 500, // y
                122 => 500, // z
                _ => 556    // average
            };
        }

        // Default: average width
        return 600;
    }

    private static double ToDouble(object obj)
    {
        return obj switch
        {
            int i => i,
            double d => d,
            long l => l,
            float f => f,
            _ => 0
        };
    }

    /// <summary>
    /// What <c>q</c> saves and <c>Q</c> restores. The CTM plus the §8.4.1
    /// Table 52 TEXT state parameters and the per-font state <c>Tf</c> derives
    /// from the font dictionary (#983).
    ///
    /// The text matrix is deliberately absent: Table 52 does not list it, it is
    /// reset by <c>BT</c>, and q/Q may not appear inside a text object (§8.2).
    /// Text rendering mode (<c>Tr</c>) is absent because this extractor does not
    /// track it at all — see the gate's stated blind spot in
    /// <c>GraphicsStateTextParameterTests</c>.
    /// </summary>
    private struct GraphicsState
    {
        public double ctm_a, ctm_b, ctm_c, ctm_d, ctm_e, ctm_f;
        public string fontName;
        public double fontSize;
        public PdfDictionary? currentFont;
        public FontState fontState;
        public double textLeading;
        public double charSpacing;
        public double wordSpacing;
        public double horizontalScaling;
        public double textRise;
    }
}
