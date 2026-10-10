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
    private readonly List<PdfStream> _undecodableForms = new();

    // Content streams the page draws that are NOT form XObjects: a tiling
    // pattern cell (scn/SCN), a soft-mask group (gs /SMask /G), a Type3 glyph
    // procedure. Their glyphs never reach a page letter, so redaction can
    // neither match nor remove text they draw. Collected during the walk,
    // probed for text after it, and reported by redaction rather than left
    // in silence (CLAUDE.md rules 5 and 6).
    private readonly List<(PdfStream Stream, string Kind, PdfDictionary? Scope, PdfDictionary? ExtraResources)> _nestedCandidates = new();
    private readonly HashSet<PdfStream> _nestedSeen = new();
    private readonly HashSet<PdfDictionary> _type3FontsSeen = new();
    private PdfDictionary? _lastShowFont;
    private readonly List<NestedTextCarrier> _nestedTextCarriers = new();

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

    // §14.6: "each sequence shall be entirely contained within a single content
    // stream". The stack depth at which the stream being walked began: its EMC
    // cannot close a span its invoker opened, and the spans it leaves open close
    // with it (#1894). The spans below stay in force, since a Do inside a hidden
    // span paints hidden.
    private int _markedContentFloor;

    // The same hidden flags with ONE stack across every Do, the way MuPDF and
    // Ghostscript paint: there a form's stray EMC closes its invoker's hidden span
    // and its unclosed hidden BDC hides the page text after it, while Poppler
    // follows the floor above. A letter is hidden only when both readings agree,
    // so a visible-only redaction never skips text some viewer paints (#1894).
    private readonly Stack<bool> _unscopedHiddenStack = new();
    private int _unscopedHiddenDepth;

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
    /// Form XObjects (appearance streams included) the last extraction reached
    /// but could not decode, so none of their text is in its letters (#1863).
    /// </summary>
    internal IReadOnlyList<PdfStream> UndecodableForms => _undecodableForms;

    /// <summary>
    /// Tiling-pattern cells, soft-mask groups and Type3 glyph procedures the
    /// last extraction found the page drawing that themselves draw text (or
    /// could not be decoded). None of that text is in its letters.
    /// </summary>
    internal IReadOnlyList<NestedTextCarrier> NestedTextCarriers => _nestedTextCarriers;

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
        _undecodableForms.Clear();
        _nestedCandidates.Clear();
        _nestedSeen.Clear();
        _type3FontsSeen.Clear();
        _lastShowFont = null;
        _nestedTextCarriers.Clear();
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
        ProbeNestedTextCarriers(cancellationToken);
        return _letters.AsReadOnly();
    }

    /// <summary>
    /// Walk the page's AcroForm fields and emit their text: what each widget's
    /// appearance DRAWS, and, where that does not already read as the field's
    /// value carriers, synthetic letters for those carriers.
    ///
    /// <para><b>The appearance (#2039).</b> A widget's <c>/AP /N</c> is the ink
    /// every reader paints (ISO 32000-2 12.5.5) when <c>/NeedAppearances</c> is
    /// not asking it to redraw. Its glyphs are walked through the one
    /// content-stream walk, from the default graphics and text state, with the
    /// form-to-page transform of Algorithm 8.1 (<see cref="AppearanceMapping"/>),
    /// so each letter sits where its glyph is drawn. The text can differ from
    /// <c>/V</c>: a choice field's <c>/V</c> is its SAVE value (§12.7.4.4; XFA
    /// 3.3 p758-760) while the widget shows the display text ("CAN" against
    /// "Canada"), and a producer may format a value it draws. Reading only
    /// <c>/V</c> left the shown text unfindable, so a redaction of it removed
    /// nothing and reported success. The letters carry the "AcroForm:" font
    /// prefix so a match routes to <see cref="Segmentation.InteractiveRedactionScrubber"/>,
    /// which rewrites the appearance and the value carriers; no page operand
    /// backs them (<see cref="Letter.OperandByteOffset"/> -1).</para>
    ///
    /// <para><b>The carriers.</b> <c>/V</c> (or <c>/DV</c>), and a list box's
    /// option list (#661), are text the file holds and a reader honouring
    /// <c>/NeedAppearances</c> draws. Each is emitted as before, as synthetic
    /// letters placed by estimate inside <c>/Rect</c>, UNLESS a search of the
    /// appearance letters already finds it (a value drawn as written is read
    /// once, not twice) or, for a choice field, the appearance draws the
    /// display text of the option whose save value it is. Anything else (no
    /// appearance, an appearance that draws no text or a stale value, a font
    /// with no usable Unicode mapping, a comb whose cells read apart) keeps the
    /// synthetic letters, so nothing findable before is unfindable now.</para>
    /// </summary>
    private void EmitFormFieldLetters()
    {
        IReadOnlyList<PdfField> fields;
        // #2040: every field with a widget on this page, not only those whose
        // first widget is here.
        try { fields = _page.GetFormFieldsWithWidgetsOnPage(); }
        catch (Exception __ex) when (__ex is not OutOfMemoryException) { return; }

        var pageWidgets = PageWidgetSet();
        foreach (var field in fields)
        {
            // The rect of the field's widget on THIS page: Field.Rect is the
            // first widget's, on whichever page that is (#2040).
            if (FieldRectOnThisPage(field) is not { } rect) continue;
            var fontName = $"AcroForm:{field.FieldType}";

            var drawn = ReadFieldAppearanceLetters(field, pageWidgets, fontName, out var hidden);
            _letters.AddRange(drawn);

            // Signature (#669: a "Digitally signed by…" block) and Button
            // (#1760: a pushbutton's caption; a checkbox's /V is a state name)
            // have no text carrier: what the appearance draws is all there is.
            // A hidden widget's appearance text is read as before #2039: its
            // text, laid out by estimate in /Rect.
            if (field.FieldType is PdfFieldType.Signature or PdfFieldType.Button)
            {
                foreach (var widget in hidden)
                {
                    var text = ExtractWidgetAppearanceText(widget);
                    if (!string.IsNullOrEmpty(text))
                        EmitMultiLineLettersInRect(text, rect, fontName);
                }
                continue;
            }

            // List box (Choice, non-combo): the widget renders its option list,
            // not only the selection (#661). Options the appearance does not
            // draw (scrolled out of view, or no appearance) are still in the
            // file. A combo box shows only its value: the /V path below.
            if (field.FieldType == PdfFieldType.Choice && !field.IsComboBox &&
                field.Options is { Count: > 0 } options)
            {
                var missing = options.Where(o => !AppearanceReads(drawn, o)).ToList();
                if (missing.Count > 0)
                    EmitMultiLineLettersInRect(string.Join("\n", missing), rect, fontName);
                continue;
            }

            var value = field.Value ?? field.DefaultValue;
            if (string.IsNullOrEmpty(value)) continue;
            if (AppearanceReads(drawn, value) || AppearanceReadsChoiceDisplay(field, drawn, value)) continue;

            // Plain multiline text fields (/Ff bit 12) can hold far more than
            // fits on one line — the same reasoning as the Choice-listbox
            // branch above, just for /FT /Tx instead of /FT /Ch (#672). The
            // single-line EmitLettersInRect silently truncates to whatever
            // fits the rect's width, which is wrong for one long line.
            if (field.IsMultiline)
                EmitMultiLineLettersInRect(value, rect, fontName);
            else
                EmitLettersInRect(value, rect, fontName);
        }
    }

    // The walk for widget appearances: one per extraction, so its font caches
    // serve every widget, and never the page's own walker, whose graphics and
    // text state are whatever the page content left (an appearance starts from
    // the defaults, §12.5.5 and §8.4.1). RunNested restores this walker's
    // default state after each appearance.
    private ContentStreamWalker? _appearanceWalker;

    // While an appearance is walked: the "AcroForm:" font name its letters
    // carry (see AddLetter). Null on every other walk.
    private string? _appearanceFontName;

    // While an appearance is walked: the widget whose appearance it is (see
    // AddLetter, #2041). Null on every other walk.
    private PdfDictionary? _appearanceWidget;

    /// <summary>The widget annotations in this page's <c>/Annots</c>.</summary>
    private HashSet<PdfDictionary> PageWidgetSet()
    {
        var set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (_page.Document.Resolve(_page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
            return set;
        foreach (var item in annots)
        {
            if (_page.Document.Resolve(item) is PdfDictionary annot && annot.GetNameOrNull("Subtype") == "Widget")
                set.Add(annot);
        }
        return set;
    }

    /// <summary>
    /// The letters a field's widgets on this page draw, at their drawn
    /// positions. Each widget is read on its own page
    /// (<see cref="PdfField.WidgetPageNumbers"/>: the page whose <c>/Annots</c>
    /// lists it, #2040); a widget on another page contributes nothing here.
    /// </summary>
    /// <remarks>
    /// A widget flagged Hidden or NoView (§12.5.3 Table 167) paints nothing on
    /// screen, so its appearance is not page text; it is listed in
    /// <paramref name="hidden"/> and keeps the reading it had before #2039. Its
    /// appearance is a carrier the redaction profile removes and reports
    /// (<c>RemoveHiddenAnnotationAppearances</c>, #1581). A widget of a
    /// multi-widget field that no page's <c>/Annots</c> lists is not drawn
    /// either: it is listed there too, on the page its <c>/P</c> names, or with
    /// no <c>/P</c>, on the field's first page.
    /// </remarks>
    private List<Letter> ReadFieldAppearanceLetters(
        PdfField field, HashSet<PdfDictionary> pageWidgets, string fontName, out List<PdfDictionary> hidden)
    {
        var letters = new List<Letter>();
        hidden = new List<PdfDictionary>();
        var widgets = field.WidgetDictionaries;
        for (var i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            if (!WidgetIsOnThisPage(field, i))
                continue;   // read on its own page (#2040)
            if (widgets.Count > 1 && !pageWidgets.Contains(widget))
            {
                // Not painted on any page: the pre-#2039 reading, as for a
                // hidden widget.
                hidden.Add(widget);
                continue;
            }
            if (_page.Document.Resolve(widget.GetOptional("F") ?? PdfNull.Instance) is PdfObject f
                && f.TryGetNumber(out var flags) && ((int)flags & (AnnotationFlagHidden | AnnotationFlagNoView)) != 0)
            {
                hidden.Add(widget);
                continue;
            }
            letters.AddRange(ReadWidgetAppearanceLetters(widget, fontName));
        }
        return letters;
    }

    /// <summary>#2040: whether a field's widget <paramref name="index"/> is on this page; one on no page belongs to the field's first page.</summary>
    private bool WidgetIsOnThisPage(PdfField field, int index)
        => (field.WidgetPageNumbers[index] ?? field.PageNumber) == _page.PageNumber;

    /// <summary>
    /// #2040: the <c>/Rect</c> of the field's first widget on this page, where
    /// its value carriers are laid out when no appearance reads them; the
    /// field's first widget's rect when none of its widgets here parses one.
    /// </summary>
    private PdfRectangle? FieldRectOnThisPage(PdfField field)
    {
        var widgets = field.WidgetDictionaries;
        for (var i = 0; i < widgets.Count; i++)
        {
            if (WidgetIsOnThisPage(field, i)
                && _page.Document.Resolve(widgets[i].GetOptional("Rect") ?? PdfNull.Instance) is PdfArray rectArray
                && AppearanceMapping.TryGetNumbers(_page.Document, rectArray, 4, out var r))
                return new PdfRectangle(r[0], r[1], r[2], r[3]);
        }
        return field.Rect;
    }

    private const int AnnotationFlagHidden = 1 << 1;
    private const int AnnotationFlagNoView = 1 << 5;

    /// <summary>
    /// Walk one widget's normal appearance (by <c>/AS</c> for a state
    /// dictionary) and return its letters in page space: Algorithm 8.1's
    /// <c>Matrix × A</c> is the walk's starting CTM, so the walker itself puts
    /// every glyph, nested forms included, where the page draws it.
    /// </summary>
    private List<Letter> ReadWidgetAppearanceLetters(PdfDictionary widget, string fontName)
    {
        var result = new List<Letter>();
        if (ResolveNormalAppearance(widget) is not { } appearance)
            return result;
        if (_page.Document.Resolve(widget.GetOptional("Rect") ?? PdfNull.Instance) is not PdfArray rectArray
            || !AppearanceMapping.TryGetNumbers(_page.Document, rectArray, 4, out var r)
            || !AppearanceMapping.TryFormToPage(_page.Document, appearance,
                new PdfRectangle(r[0], r[1], r[2], r[3]), out var formToPage))
            return result;

        var start = _letters.Count;
        // Marked content of the page does not enclose an annotation: the walk
        // starts outside every span the page left open, and leaves none.
        var savedHidden = _hiddenOptionalContentDepth;
        var savedUnscoped = _unscopedHiddenDepth;
        var savedUnscopedCount = _unscopedHiddenStack.Count;
        var savedMcid = _currentMcid;
        _hiddenOptionalContentDepth = 0;
        _unscopedHiddenDepth = 0;
        _appearanceFontName = fontName;
        _appearanceWidget = widget;
        try
        {
            RunFormXObject(_appearanceWalker ??= CreateWalker(Array.Empty<byte>()), appearance,
                fromIdentityCtm: true, matrixOverride: formToPage);
        }
        finally
        {
            _appearanceFontName = null;
            _appearanceWidget = null;
            while (_unscopedHiddenStack.Count > savedUnscopedCount)
                _unscopedHiddenStack.Pop();
            _hiddenOptionalContentDepth = savedHidden;
            _unscopedHiddenDepth = savedUnscoped;
            _currentMcid = savedMcid;
        }

        result.AddRange(_letters.Skip(start));
        _letters.RemoveRange(start, _letters.Count - start);
        // An appearance is a content stream like any other: RTL runs in it
        // are in visual order (#632).
        BidiReorderer.ReorderVisualRtlRuns(result);
        return result;
    }

    /// <summary>A widget's <c>/AP /N</c> form, by <c>/AS</c> for a state dictionary (first entry when <c>/AS</c> is absent or names none).</summary>
    private PdfStream? ResolveNormalAppearance(PdfDictionary widget)
    {
        if (widget.GetOptional("AP") is not { } apObj || _page.Document.Resolve(apObj) is not PdfDictionary ap)
            return null;
        if (ap.GetOptional("N") is not { } nObj)
            return null;
        var resolved = _page.Document.Resolve(nObj);
        if (resolved is not PdfStream && resolved is PdfDictionary states)
        {
            var asName = widget.GetNameOrNull("AS");
            var chosen = (asName != null ? states.GetOptional(asName) : null) ?? states.Values.FirstOrDefault();
            resolved = chosen != null ? _page.Document.Resolve(chosen) : null;
        }
        return resolved is PdfStream stream && stream.GetNameOrNull("Subtype") == "Form" ? stream : null;
    }

    /// <summary>
    /// True when a search of <paramref name="drawn"/> finds <paramref name="text"/>
    /// as typed: the whole of it, or else every word of it. The search is the
    /// one search and redaction use, so "reads" means "a user searching for it
    /// finds it here"; a comb whose cells read as separate words does not.
    /// </summary>
    private static bool AppearanceReads(List<Letter> drawn, string text)
    {
        if (drawn.Count == 0) return false;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;

        var glyphs = new StringBuilder();
        foreach (var letter in drawn)
            foreach (var ch in letter.Value)
                if (!char.IsWhiteSpace(ch)) glyphs.Append(ch);
        // In order: "Smith, John" drawn does not read as "John Smith", which a
        // search for the value would then miss.
        var all = glyphs.ToString();
        var from = 0;
        foreach (var word in words)
        {
            var at = all.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + word.Length;
        }

        if (Find(string.Join(' ', words)))
            return true;
        return words.Distinct(StringComparer.Ordinal).All(Find);

        bool Find(string term) =>
            Segmentation.PdfDocumentRedactionExtensions.FindTextMatches(drawn, term, caseSensitive: true).Count > 0;
    }

    /// <summary>
    /// A choice field's <c>/V</c> is a save value; the widget shows the display
    /// text of the <c>[save display]</c> option pair (§12.7.4.4) whose save
    /// value it is. True when the appearance reads that display text.
    /// </summary>
    private bool AppearanceReadsChoiceDisplay(PdfField field, List<Letter> drawn, string value)
    {
        if (field.FieldType != PdfFieldType.Choice || drawn.Count == 0) return false;
        if (_page.Document.Resolve(field.RawDictionary.GetOptional("Opt") ?? PdfNull.Instance) is not PdfArray opt)
            return false;
        foreach (var item in opt)
        {
            if (_page.Document.Resolve(item) is PdfArray { Count: >= 2 } pair
                && _page.Document.Resolve(pair[0]) is PdfString save && save.Value == value
                && _page.Document.Resolve(pair[1]) is PdfString display
                && AppearanceReads(drawn, display.Value))
                return true;
        }
        return false;
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
    /// Used for non-widget annotations (#1428) and hidden widgets. A shown
    /// widget's appearance is read at its drawn glyphs instead
    /// (<see cref="ReadWidgetAppearanceLetters"/>, #2039).
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
            if (annot.Subtype == Document.PdfAnnotationSubtype.FreeText && !string.IsNullOrEmpty(annot.Contents))
                EmitMultiLineLettersInRect(annot.Contents, annot.Rect, "Annotation:FreeText");

            // #1428: an annotation's /AP /N appearance stream is genuine drawn
            // ink — §12.5.5 renders it verbatim, in precedence over any
            // synthesized appearance — and until this fix it was invisible to
            // extraction, to the term-driven redaction scrub, and to
            // RedactText's own oracle-vs-self verification, on EVERY subtype
            // except FreeText's /Contents path above. Measured leak: a Stamp
            // annotation whose /AP drew "STAMPSECRET" survived
            // RedactText("STAMPSECRET") -- mutool AND pdftotext both still
            // read it in the saved output. Reuses the exact machinery the
            // AcroForm widget path already proved (ExtractWidgetAppearanceText
            // needs only /AP + optional /AS, nothing Widget-specific), so this
            // is the same appearance-stream reader with a wider set of callers,
            // not a new one. Deliberately excludes Widget (its /AP is already
            // extracted via the AcroForm field-value path, EmitFormFieldLetters
            // -- extracting it again here would double-emit).
            if (annot.Subtype == Document.PdfAnnotationSubtype.Widget) continue;

            var apText = ExtractWidgetAppearanceText(annot.RawDictionary);
            if (!string.IsNullOrEmpty(apText))
                EmitMultiLineLettersInRect(apText, annot.Rect, $"Annotation:{annot.Subtype}");
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
                _letters.Add(new Letter(GlyphUnicodeDecoder.ShareSingleChar(ch.ToString()), bbox, fontSize, fontName, x, y, advance, ch));
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
                GlyphUnicodeDecoder.ShareSingleChar(ch.ToString()),
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
                // #2008: measured in the frame of the line each glyph is written
                // along. An upright glyph's frame is its user-space box and
                // baseline, unchanged; a line a matrix turns would otherwise
                // break between every glyph, its baseline running along y.
                var (prevBox, prevBaseline) = TextSelectionEngine.LineFrame(prevLetter);
                var (box, baseline) = TextSelectionEngine.LineFrame(letter);

                // Check for line break
                var yDiff = Math.Abs(baseline - prevBaseline);
                if (TextSelectionEngine.DirectionChanges(prevLetter, letter) || yDiff > lineGapThreshold)
                {
                    startNewWord = true;
                }
                else
                {
                    // Check for horizontal gap
                    var gap = box.Left - prevBox.Right;
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
        public void OnTextShowBegin() => owner.NoteType3Font(walker);
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

    /// <summary>
    /// #1098 — extract letters from an APPEARANCE stream (or any Form XObject
    /// content) that no page `Do` points at, using the appearance's OWN
    /// <paramref name="resources"/> so its fonts resolve. Positions are in the
    /// appearance's own coordinate space, which is exactly the space its glyph
    /// operators live in — so the redaction that follows needs no coordinate
    /// mapping (the whole reason #1098's fix is smaller than it looks). Reuses
    /// the same walker + LetterSink as the page walk; does NOT emit the synthetic
    /// AcroForm/annotation letters (those are the page's, not this stream's).
    /// </summary>
    public IReadOnlyList<Letter> ExtractLettersFrom(
        byte[] content, Primitives.PdfDictionary? resources, CancellationToken cancellationToken = default)
    {
        _letters.Clear();
        var walker = new ContentStreamWalker(content, _page);
        if (resources != null) walker.PushResources(resources);
        _walker = walker;
        var sink = new LetterSink(this, walker);
        walker.Walk(ref sink, cancellationToken);
        BidiReorderer.ReorderVisualRtlRuns(_letters);
        return _letters.AsReadOnly();
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
        // #2039: a widget appearance's glyph is the field's text, not page
        // content: it carries the "AcroForm:" font name redaction routes by, no
        // page operand offset, and no page structure MCID.
        var appearanceFont = _appearanceFontName;
        _letters.Add(new Letter(
            // #1485: letters are retained for the document's lifetime, and a
            // per-page /ToUnicode map gives each page its own copy of every
            // one-char value. Share them; the value is unchanged.
            GlyphUnicodeDecoder.ShareSingleChar(glyph.Unicode),
            glyph.Cell,
            glyph.FontSize,
            appearanceFont ?? glyph.FontName,
            glyph.X,
            glyph.Y,
            glyph.Width,
            glyph.CharCode,
            glyph.ByteLength)
        {
            // O(1) counter kept in lock-step with _optionalContentHiddenStack;
            // equivalent to _optionalContentHiddenStack.Any(hidden => hidden)
            // without the per-letter enumeration (#600).
            IsInHiddenOptionalContent = _hiddenOptionalContentDepth > 0 && _unscopedHiddenDepth > 0,
            // §9.3.6, #1607. Mode 3 and 7 paint nothing, so this letter is
            // extractable text that never appeared on the page.
            TextRenderMode = glyph.TextRenderMode,
            IsCidFont = glyph.IsCidFont,
            IsVerticalWriting = glyph.IsVerticalWriting,
            // #2008: the direction a turned line runs along, for line grouping.
            BaselineAngle = glyph.BaselineAngle,
            // #776: the innermost enclosing /MCID span, for the a11y bridge.
            MarkedContentId = appearanceFont == null ? _currentMcid : null,
            // #2041: which widget drew it, so redaction selects that field by
            // identity, wherever the glyph lands (outside /Rect, beyond /BBox).
            SourceWidget = appearanceFont == null ? null : _appearanceWidget,
            // #1091/#1092: where this glyph's code lives, for the operand rewrite.
            OperandByteOffset = appearanceFont == null ? glyph.OperandByteOffset : -1,
            TjElementIndex = appearanceFont == null ? glyph.TjElementIndex : -1,
            // #1091 advance compensation: the FULL §9.4.4 advance in TJ-number
            // units, so -sum over a removed run restores the exact pen movement
            // and following text does not shift. w0 alone misses the spacing
            // term (Tc + Tw), which a TJ number expresses as spacing·1000/Tfs
            // (Th cancels between the glyph advance and the TJ compensation).
            DisplacementThousandths = glyph.DisplacementThousandths
                + (glyph.FontSize > 1e-6 ? glyph.Spacing * 1000.0 / glyph.FontSize : 0)
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
                PushMarkedContentSpan(
                    IsHiddenOptionalContentSpan(operands, walker), ResolveSpanMcid(operands, walker));
                break;

            case "BMC":
                PushMarkedContentSpan(hidden: false, spanMcid: null); // tag-only span carries no /MCID
                break;

            case "scn":
            case "SCN":
                // §8.7.3.1: a pattern colour names its pattern as the LAST operand.
                if (operands.Count >= 1 && operands[^1] is PdfName patternName &&
                    walker.ResolveResource("Pattern", patternName.Value) is PdfStream pattern &&
                    pattern.GetInt("PatternType", 0) == 1)
                    NoteNestedCarrier(pattern, "tiling pattern", null, walker);
                break;

            case "gs":
                if (operands.Count >= 1 && operands[0] is PdfName gsName &&
                    walker.ResolveResource("ExtGState", gsName.Value) is PdfDictionary gs &&
                    _page.Document.Resolve(gs.GetOptional("SMask") ?? PdfNull.Instance) is PdfDictionary smask &&
                    _page.Document.Resolve(smask.GetOptional("G") ?? PdfNull.Instance) is PdfStream group)
                    NoteNestedCarrier(group, "soft-mask group", null, walker);
                break;

            case "EMC":
                if (_unscopedHiddenStack.Count > 0 && _unscopedHiddenStack.Pop())
                    _unscopedHiddenDepth--;
                if (_optionalContentHiddenStack.Count > _markedContentFloor)
                    PopMarkedContentSpan();
                break;
        }
    }

    private void PushMarkedContentSpan(bool hidden, int? spanMcid)
    {
        _optionalContentHiddenStack.Push(hidden);
        _unscopedHiddenStack.Push(hidden);
        if (hidden)
        {
            _hiddenOptionalContentDepth++;
            _unscopedHiddenDepth++;
        }
        PushMcid(spanMcid);
    }

    private void PopMarkedContentSpan()
    {
        if (_optionalContentHiddenStack.Pop())
            _hiddenOptionalContentDepth--;
        PopMcid();
    }

    /// <summary>A Type3 font about to show glyphs: each glyph procedure is a nested carrier.</summary>
    private void NoteType3Font(ContentStreamWalker walker)
    {
        var font = walker.CurrentFont;
        if (font == null || ReferenceEquals(font, _lastShowFont)) return;
        _lastShowFont = font;
        if (font.GetNameOrNull("Subtype") != "Type3" || !_type3FontsSeen.Add(font)) return;
        if (_page.Document.Resolve(font.GetOptional("CharProcs") ?? PdfNull.Instance) is not PdfDictionary procs)
            return;
        // §9.6.5: a glyph procedure's resources are the font's /Resources.
        var fontResources = _page.Document.Resolve(font.GetOptional("Resources") ?? PdfNull.Instance) as PdfDictionary;
        foreach (var value in procs.Values)
            if (_page.Document.Resolve(value) is PdfStream proc)
                NoteNestedCarrier(proc, "Type3 glyph procedure", fontResources, walker);
    }

    private void NoteNestedCarrier(PdfStream stream, string kind, PdfDictionary? extraResources, ContentStreamWalker walker)
    {
        if (_nestedSeen.Add(stream))
            _nestedCandidates.Add((stream, kind, walker.ActiveResources.FirstOrDefault(), extraResources));
    }

    /// <summary>
    /// Walk each nested carrier the page drew through the same walker and
    /// sink, keep only what it says (its letters never join the page's), and
    /// record those that draw text. A carrier found inside another is probed
    /// in turn; the form depth bound and cycle set apply as for <c>Do</c>.
    /// Geometry is not used: where a tiling cell repeats, or where a glyph
    /// procedure lands in glyph space, is not computed here.
    /// </summary>
    private void ProbeNestedTextCarriers(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _nestedCandidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (stream, kind, scope, extraResources) = _nestedCandidates[i];
            if (stream.IsFiltered && !stream.TryEnsureDecoded())
            {
                _nestedTextCarriers.Add(new NestedTextCarrier(stream, kind, extraResources ?? scope, null,
                    $"its /Filter {string.Join(" ", stream.Filters.Select(f => "/" + f))} could not be decoded" +
                    (stream.DecodeFailureReason is { } why ? $" ({why})" : "")));
                continue;
            }

            var walker = CreateWalker(Array.Empty<byte>());
            if (scope != null) walker.PushResources(scope);
            if (extraResources != null) walker.PushResources(extraResources);

            var start = _letters.Count;
            // Like an annotation appearance, a probed carrier starts outside
            // every marked-content span the page left open, and leaves none.
            var savedHidden = _hiddenOptionalContentDepth;
            var savedUnscoped = _unscopedHiddenDepth;
            var savedUnscopedCount = _unscopedHiddenStack.Count;
            var savedMcid = _currentMcid;
            _hiddenOptionalContentDepth = 0;
            _unscopedHiddenDepth = 0;
            string? unread = null;
            try
            {
                RunFormXObject(walker, stream, fromIdentityCtm: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // A malformed carrier the page walk never entered must not
                // fail the page's extraction; it is reported unexamined.
                unread = $"it could not be read ({ex.Message})";
            }
            finally
            {
                while (_unscopedHiddenStack.Count > savedUnscopedCount)
                    _unscopedHiddenStack.Pop();
                _hiddenOptionalContentDepth = savedHidden;
                _unscopedHiddenDepth = savedUnscoped;
                _currentMcid = savedMcid;
            }

            var drawn = _letters.GetRange(start, _letters.Count - start);
            _letters.RemoveRange(start, _letters.Count - start);
            BidiReorderer.ReorderVisualRtlRuns(drawn);
            var text = string.Concat(drawn.Select(l => l.Value));
            if (unread != null)
                _nestedTextCarriers.Add(new NestedTextCarrier(stream, kind, extraResources ?? scope, null, unread));
            else if (!string.IsNullOrWhiteSpace(text))
                _nestedTextCarriers.Add(new NestedTextCarrier(stream, kind, extraResources ?? scope, text));
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
    /// <param name="matrixOverride">The form-to-page transform to use in place
    /// of the stream's own <c>/Matrix</c>: an annotation appearance's
    /// Algorithm 8.1 <c>Matrix × A</c>, which already contains it.</param>
    private void RunFormXObject(ContentStreamWalker walker, PdfStream stream, bool fromIdentityCtm,
        double[]? matrixOverride = null)
    {
        if (_formXObjectDepth >= MaxFormXObjectDepth)
            return;

        // #1863: a form whose /Filter cannot be decoded is skipped and
        // recorded, never walked: redaction reports it instead of aborting.
        if (stream.IsFiltered && !stream.TryEnsureDecoded())
        {
            _undecodableForms.Add(stream);
            return;
        }

        if (!_formXObjectStack.Add(stream))
            return;

        _formXObjectDepth++;
        var invokerFloor = _markedContentFloor;
        _markedContentFloor = _optionalContentHiddenStack.Count;
        try
        {
            PdfDictionary? resources = null;
            var resourcesObj = stream.GetOptional("Resources");
            if (resourcesObj != null)
                resources = _page.Document.Resolve(resourcesObj) as PdfDictionary;

            var matrix = matrixOverride
                ?? (TryReadMatrix(stream.GetOptional("Matrix"), out var m)
                    ? new[] { m.a, m.b, m.c, m.d, m.e, m.f }
                    : null);

            var sink = new LetterSink(this, walker);
            walker.RunNested(stream.DecodedData, resources, matrix, fromIdentityCtm, ref sink);
        }
        finally
        {
            while (_optionalContentHiddenStack.Count > _markedContentFloor)
                PopMarkedContentSpan();
            _markedContentFloor = invokerFloor;
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

}

/// <summary>
/// A content stream a page draws that is not a form XObject (a tiling pattern
/// cell, a soft-mask group, a Type3 glyph procedure) and that itself draws
/// text. Extraction does not make its glyphs page letters, so the page's glyph
/// pass can neither match nor remove them: a term redaction rewrites the
/// carrier's own stream, and what it cannot rewrite is reported.
/// </summary>
/// <param name="Stream">The carrier's own content stream.</param>
/// <param name="Kind">"tiling pattern", "soft-mask group" or "Type3 glyph procedure".</param>
/// <param name="Resources">What its names resolve through when it has no
/// <c>/Resources</c> of its own: a Type3 font's, else those in scope where it was drawn.</param>
/// <param name="Text">What it draws, in logical order; null when it could not be read.</param>
/// <param name="Unread">Why it could not be read, when <paramref name="Text"/> is null.</param>
internal sealed record NestedTextCarrier(
    PdfStream Stream, string Kind, PdfDictionary? Resources, string? Text, string? Unread = null);
