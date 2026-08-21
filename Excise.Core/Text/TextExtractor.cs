using System.Linq;
using System.Text;
using System.Threading;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text;

/// <summary>
/// Extracts text and letter information from PDF pages.
///
/// <para>This class owns no tokenizer and no state machine. It is a SINK over
/// <see cref="ContentStreamWalker"/> — the single content-stream walk — and its
/// whole job is the part that is genuinely extraction: turn each glyph the
/// walker reports into a <see cref="Letter"/>, carry the marked-content and
/// optional-content tagging that only extraction cares about, descend into form
/// XObjects, and repair visual-order RTL runs afterwards. #992/#996.</para>
/// </summary>
public class TextExtractor
{
    private readonly PdfPage _page;
    private readonly byte[] _contentStream;

    // The walk. Created per extraction and kept afterwards so the annotation
    // and form-field passes — which run AFTER the page's content stream and
    // reach appearance streams that no `Do` points at — descend through the
    // same state machine rather than a second one.
    private ContentStreamWalker? _walker;

    private readonly List<Letter> _letters = new();

    // Form XObject recursion: bounded depth plus cycle detection, so a form
    // that invokes itself (directly or through a ring) terminates. This is the
    // POLICY half of `Do`; the state half — /Matrix, /Resources, and restoring
    // everything afterwards per §8.10.1 — is ContentStreamWalker.RunNested.
    private readonly HashSet<PdfStream> _formXObjectStack = new();
    private int _formXObjectDepth;
    private const int MaxFormXObjectDepth = 64;

    // Marked-content nesting depth of /OC spans that are hidden. Maintained in
    // lock-step with _optionalContentHiddenStack (BDC/BMC push, EMC pop) so the
    // per-glyph "am I inside any hidden span?" check is O(1) instead of a
    // per-letter LINQ Any() over the stack (#600).
    private readonly Stack<bool> _optionalContentHiddenStack = new();
    private int _hiddenOptionalContentDepth;

    // Marked-content ID (/MCID) tracking for the accessibility MCID→letter
    // bridge (#776). Pushed/popped in lock-step with _optionalContentHiddenStack
    // (every BDC/BMC pushes, every EMC pops) so the nesting matches exactly.
    // Each entry is the EFFECTIVE MCID at that nesting level: a span carrying its
    // own /MCID sets it; a span without one inherits the enclosing level's value.
    // _currentMcid mirrors the top of the stack for O(1) per-glyph tagging.
    private readonly Stack<int?> _mcidStack = new();
    private int? _currentMcid;

    public TextExtractor(PdfPage page)
    {
        _page = page;
        _contentStream = page.GetContentStreamBytes();
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
        _letters.Clear();
        ParseContentStream(cancellationToken);
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
        // fromIdentityCtm: this parse happens after the page's own content
        // stream has already been walked (ExtractLetters calls
        // EmitFormFieldLetters after ParseContentStream), so whatever CTM was
        // left behind is unrelated to the annotation and would just pollute
        // the (already-discarded) positions computed below. The walker restores
        // it afterwards along with everything else.
        RunFormXObject(_walker ??= CreateWalker(_contentStream), appearanceStream,
            fromIdentityCtm: true);

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

    // ------------------------------------------------------------------
    // The walk. There is one, and it is ContentStreamWalker's (#992). What
    // follows is only the sink: what extraction does with each glyph, and the
    // two things extraction tracks that operator bounds do not — marked-content
    // tagging and optional-content visibility.
    // ------------------------------------------------------------------

    /// <summary>
    /// The walker's consumer. A STRUCT, dispatched through the walker's generic
    /// constraint, so the per-glyph callback on this — the extraction hot path
    /// #600 tuned — allocates nothing and devirtualizes. It holds two references
    /// and forwards; all accumulated state lives on the TextExtractor.
    /// </summary>
    private readonly struct LetterSink(TextExtractor owner, ContentStreamWalker walker)
        : IContentStreamSink
    {
        public void OnOperator(string name, List<PdfObject> operands) =>
            owner.ExecuteSinkOperator(name, operands, walker);

        // The pixels are the renderer's business; extraction needs only that
        // they were skipped rather than tokenised, which the walker guarantees.
        public void OnInlineImage(PdfDictionary imageParams, byte[] imageData) { }

        // A Letter is per GLYPH, so the per-operator and per-string boundaries
        // carry no information here. ContentStreamParser is the sink that needs
        // them, to close an operator's bounding box.
        public void OnTextShowBegin() { }
        public void OnStringBegin() { }
        public void OnStringEnd(int byteCount) { }
        public void OnTextShowEnd() { }
        public void OnTjAdjustment(double adjustment) { }

        public void OnGlyph(in WalkedGlyph glyph) => owner.AddLetter(in glyph);
    }

    private ContentStreamWalker CreateWalker(byte[] content)
    {
        var walker = new ContentStreamWalker(content, _page);
        if (_page.Resources != null)
            walker.PushResources(_page.Resources);
        return walker;
    }

    private void ParseContentStream(CancellationToken cancellationToken)
    {
        var walker = CreateWalker(_contentStream);
        _walker = walker;
        var sink = new LetterSink(this, walker);
        walker.Walk(ref sink, cancellationToken);
    }

    /// <summary>
    /// One letter per glyph, from the numbers the walker computed. Nothing here
    /// re-derives geometry: the cell, the pen origin and the advance are the
    /// SAME values ContentStreamParser aggregates into operator bounds, which is
    /// what makes it structurally impossible for the two to disagree about where
    /// a glyph is (#833/#942/#980).
    /// </summary>
    private void AddLetter(in WalkedGlyph glyph)
    {
        _letters.Add(new Letter(
            glyph.Unicode,
            glyph.Cell,
            glyph.FontSize,
            glyph.FontName,
            glyph.X,
            glyph.Y,
            glyph.Width,
            glyph.CharCode,
            glyph.ByteLength)
        {
            // O(1) counter kept in lock-step with _optionalContentHiddenStack;
            // equivalent to _optionalContentHiddenStack.Any(hidden => hidden)
            // without the per-letter enumeration (#600).
            IsInHiddenOptionalContent = _hiddenOptionalContentDepth > 0,
            IsCidFont = glyph.IsCidFont,
            // #776: the innermost enclosing /MCID span, for the a11y bridge.
            MarkedContentId = _currentMcid
        });
    }

    /// <summary>
    /// The operators extraction acts on beyond the glyph walk: <c>Do</c>, and
    /// the marked-content brackets. Everything else — the whole graphics and
    /// text state machine — the walker has already executed.
    /// </summary>
    private void ExecuteSinkOperator(string name, List<PdfObject> operands, ContentStreamWalker walker)
    {
        switch (name)
        {
            case "Do":
                if (operands.Count >= 1 && operands[0] is PdfName xObjectName)
                    ExecuteDo(xObjectName.Value, walker);
                break;

            case "BDC":
                {
                    var hidden = IsHiddenOptionalContentSpan(operands, walker);
                    _optionalContentHiddenStack.Push(hidden);
                    if (hidden)
                        _hiddenOptionalContentDepth++;
                    PushMcid(ResolveSpanMcid(operands, walker));
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

    private void ExecuteDo(string name, ContentStreamWalker walker)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        if (walker.ResolveXObject(name) is not PdfStream stream ||
            stream.GetNameOrNull("Subtype") != "Form")
            return;

        RunFormXObject(walker, stream, fromIdentityCtm: false);
    }

    /// <summary>
    /// Walk a Form XObject's content through the same walker, bounded and
    /// cycle-checked. §8.10.1's <c>q &lt;Matrix&gt; cm … Q</c> semantics —
    /// concatenating /Matrix, bringing /Resources into scope, and restoring all
    /// graphics and text state afterwards — belong to the walker; the depth
    /// bound and the cycle set are policy and belong here.
    /// </summary>
    private void RunFormXObject(ContentStreamWalker walker, PdfStream stream, bool fromIdentityCtm)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        if (!_formXObjectStack.Add(stream))
            return;

        _formXObjectDepth++;
        try
        {
            PdfDictionary? resources = null;
            var resourcesObj = stream.GetOptional("Resources");
            if (resourcesObj != null)
                resources = _page.Document.Resolve(resourcesObj) as PdfDictionary;

            var matrix = TryReadMatrix(stream.GetOptional("Matrix"), out var m)
                ? new[] { m.a, m.b, m.c, m.d, m.e, m.f }
                : null;

            var sink = new LetterSink(this, walker);
            walker.RunNested(stream.DecodedData, resources, matrix, fromIdentityCtm, ref sink);
        }
        finally
        {
            _formXObjectDepth--;
            _formXObjectStack.Remove(stream);
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

    /// <summary>
    /// A BDC span's /MCID, from an inline properties dictionary
    /// (<c>/Span &lt;&lt;/MCID 3&gt;&gt; BDC</c>) or from a named /Properties
    /// entry (<c>/Span /P1 BDC</c>, §14.6.2). Read from the PARSED dictionary
    /// operand — until #996 extraction skipped dictionaries entirely and had to
    /// scan the raw bytes of the skipped span for "/MCID" instead.
    /// </summary>
    private int? ResolveSpanMcid(List<PdfObject> operands, ContentStreamWalker walker)
    {
        if (operands.Count < 2)
            return null;

        if (operands[1] is PdfDictionary inlineProperties)
            return ReadMcid(inlineProperties);

        if (operands[1] is not PdfName propertyName)
            return null;

        foreach (var resources in walker.ActiveResources)
        {
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null)
                continue;
            if (_page.Document.Resolve(propertiesObj) is not PdfDictionary properties)
                continue;
            var propObj = properties.GetOptional(propertyName.Value);
            if (propObj == null)
                continue;
            if (_page.Document.Resolve(propObj) is PdfDictionary propDict)
                return ReadMcid(propDict);
        }

        return null;
    }

    private int? ReadMcid(PdfDictionary dict) =>
        dict.GetOptional("MCID") is { } mcidObj &&
        _page.Document.Resolve(mcidObj) is PdfInteger mcid
            ? (int)mcid.Value
            : null;

    /// <summary>
    /// True when this BDC opens an <c>/OC</c> span whose optional-content group
    /// is OFF in the default configuration. Resolved through the shared
    /// <see cref="Document.OptionalContentVisibility"/> so extraction agrees
    /// with the renderer about hidden layers — OCG (reference-based
    /// OFF/ON/BaseState), OCMD (/P policy and /VE And/Or/Not expressions) and
    /// nested /OC alike. See issue #336. The property object is passed
    /// UN-resolved so reference identity survives for /OFF and /ON matching.
    /// </summary>
    private bool IsHiddenOptionalContentSpan(List<PdfObject> operands, ContentStreamWalker walker)
    {
        if (operands.Count < 2)
            return false;

        if (operands[0] is not PdfName tag || tag.Value != "OC")
            return false;

        if (operands[1] is not PdfName propertyName)
            return false;

        foreach (var resources in walker.ActiveResources)
        {
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null)
                continue;

            if (_page.Document.Resolve(propertiesObj) is not PdfDictionary properties)
                continue;

            var propertyObj = properties.GetOptional(propertyName.Value);
            if (propertyObj == null)
                continue;

            if (!Document.OptionalContentVisibility.IsVisibleByDefault(_page.Document, propertyObj))
                return true;
        }

        return false;
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

    private static bool TryNumber(PdfObject? obj, out double v)
    {
        switch (obj)
        {
            case PdfInteger i: v = i.Value; return true;
            case PdfReal r:    v = r.Value; return true;
            default:           v = 0; return false;
        }
    }

    /// <summary>
    /// Get character width for standard Type1 fonts (Helvetica, Times, Courier, etc.)
    /// Lives here but is called by <see cref="ContentStreamWalker"/>: it is the
    /// last piece of font metrics that is neither in the font dictionary nor in
    /// an embedded program, and a second copy is exactly how state machines
    /// drift apart (#980).
    /// </summary>
    internal static double GetStandardFontWidth(string baseFont, int charCode)
    {
        // #1100: the real AFM metrics, for every one of the 14 -- not just
        // Courier and Helvetica. This method used to fall through to a flat
        // 600 for everything else, so all four Times faces advanced 0.6em per
        // glyph regardless of the glyph, and Helvetica punctuation took the
        // 556 "average" arm. See StandardFontMetrics for what that cost: the
        // letter model drifted 74pt over a 22-character Times line, which is
        // the geometry redaction matches and draws its black box against.
        if (Fonts.StandardFontMetrics.TryGetWidth(baseFont, charCode, out var width))
            return width;

        // Outside 32-126, or a font that is not one of the 14. Unchanged: the
        // codes above 126 need the font's /Encoding to resolve a glyph name,
        // and inventing a WinAnsi assumption here would trade an obvious wrong
        // answer for a confident one.
        if (baseFont.StartsWith("Courier", StringComparison.Ordinal))
            return 600;

        return baseFont.StartsWith("Helvetica", StringComparison.Ordinal) ? 556 : 600;
    }
}
