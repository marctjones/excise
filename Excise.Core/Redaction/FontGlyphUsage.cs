using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1920: which character codes every font in a document still draws, read
/// through the one content-stream walk with a usage sink. The redaction that
/// removes a term compares a snapshot taken before it with one taken after,
/// and only codes no stream draws any more may leave the font programs and
/// their /ToUnicode, /W and /Widths.
/// </summary>
/// <remarks>
/// <para><b>Errs toward "still drawn".</b> A code counted that is not really
/// drawn keeps its glyph (the leak #1920 is about, for that one code); a code
/// missed that IS drawn would blank a glyph a reader paints, which changes the
/// page. So every stream that may paint is walked whatever its visibility:
/// page content, form XObjects (through <c>Do</c>), tiling patterns, soft-mask
/// groups, Type3 glyph procedures, every appearance stream of every annotation
/// (all of <c>/N</c>, <c>/R</c>, <c>/D</c> and their states, hidden or not) and
/// of every AcroForm widget, whether a page lists it or not. A form field's
/// value is text a viewer regenerating the appearance draws with the font its
/// <c>/DA</c> names, so its characters are recorded too
/// (<see cref="FieldText"/>). A stream that cannot be decoded or walked marks
/// every font its resources could name as <see cref="Uncertain"/>: those are
/// never edited.</para>
/// <para>Fonts are keyed by object number (an eviction re-parses a font into a
/// new instance; the number survives), or by the dictionary itself when it is a
/// direct object.</para>
/// </remarks>
internal sealed class FontGlyphUsage
{
    /// <summary>What one font draws.</summary>
    internal sealed class FontUse(PdfDictionary font)
    {
        /// <summary>The font dictionary, as last seen.</summary>
        public PdfDictionary Font { get; set; } = font;

        /// <summary>Character codes shown with this font.</summary>
        public HashSet<int> Codes { get; } = new();

        /// <summary>CIDs those codes selected (the code itself for a simple font).</summary>
        public HashSet<int> Cids { get; } = new();

        /// <summary>The walker's Unicode for each code shown.</summary>
        public Dictionary<int, string> Unicode { get; } = new();

        /// <summary>The CID each code shown selected.</summary>
        public Dictionary<int, int> CodeToCid { get; } = new();
    }

    private const int MaxFormDepth = 64;

    private readonly PdfDocument _document;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<PdfStream> _formPath = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfStream> _nestedSeen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _type3Seen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfStream> _appearancesSeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<(PdfStream Stream, PdfDictionary? Scope, PdfDictionary? Extra)> _nested = new();
    private PdfPage? _page;

    /// <summary>Usage by font key (object number, or the direct dictionary).</summary>
    public Dictionary<object, FontUse> Fonts { get; } = new();

    /// <summary>Fonts some unreadable stream could have drawn with.</summary>
    public HashSet<object> Uncertain { get; } = new();

    /// <summary>
    /// Code points of form-field values (and FreeText contents) whose default
    /// appearance names the font: a viewer that regenerates the appearance
    /// draws them with it.
    /// </summary>
    public Dictionary<object, HashSet<int>> FieldText { get; } = new();

    private FontGlyphUsage(PdfDocument document, CancellationToken cancellationToken)
    {
        _document = document;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The key a font is tracked under.</summary>
    internal static object KeyOf(PdfDictionary font) => font.ObjectNumber is { } n ? n : font;

    /// <summary>
    /// The key for <paramref name="font"/>, reached as <paramref name="raw"/> in
    /// a resource dictionary: the object number of the reference when it is one
    /// (an object in an object stream carries no number of its own), else
    /// <see cref="KeyOf(PdfDictionary)"/>.
    /// </summary>
    internal static object KeyOf(PdfDictionary font, PdfObject? raw) =>
        font.ObjectNumber is { } n ? n : raw is PdfReference r ? r.ObjectNum : font;

    private readonly Dictionary<PdfDictionary, object> _keys = new(ReferenceEqualityComparer.Instance);

    private object KeyFor(PdfDictionary font, ContentStreamWalker walker, string name)
    {
        if (_keys.TryGetValue(font, out var key)) return key;
        PdfObject? raw = null;
        foreach (var resources in walker.ActiveResources.Append(_page?.Resources))
        {
            if (resources == null) continue;
            if (_document.Resolve(resources.GetOptional("Font") ?? PdfNull.Instance) is not PdfDictionary fonts) continue;
            if (fonts.GetOptional(name) is { } entry && ReferenceEquals(_document.Resolve(entry), font)) { raw = entry; break; }
        }
        return _keys[font] = KeyOf(font, raw);
    }

    /// <summary>Walk the whole document.</summary>
    public static FontGlyphUsage Collect(PdfDocument document, CancellationToken cancellationToken = default)
    {
        var usage = new FontGlyphUsage(document, cancellationToken);
        usage.Run();
        return usage;
    }

    private void Run()
    {
        var acroForm = _document.Resolve(_document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        var defaultResources = acroForm == null ? null
            : _document.Resolve(acroForm.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary;

        for (var pageNumber = 1; pageNumber <= _document.PageCount; pageNumber++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var page = _document.GetPage(pageNumber);
            _page = page;
            WalkPage(page);

            if (_document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
                foreach (var item in annots)
                    if (_document.Resolve(item) is PdfDictionary annot)
                        WalkAnnotation(annot, defaultResources);
            DrainNested();
        }

        // Widgets no page lists still hold appearances a viewer may show.
        if (acroForm != null && _document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) is PdfArray fields)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var field in fields)
                WalkField(field, acroForm, defaultResources, inheritedDa: null, seen, depth: 0);
        }
    }

    private void WalkPage(PdfPage page)
    {
        var resources = page.Resources;
        byte[] content;
        try
        {
            if (!page.TryGetContentStreamBytes(out content, out _))
                MarkUncertain(resources);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            MarkUncertain(resources);
            return;
        }

        var walker = new ContentStreamWalker(content, page);
        if (resources != null) walker.PushResources(resources);
        var sink = new UsageSink(this, walker);
        try
        {
            walker.Walk(ref sink, _cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            MarkUncertain(resources);
        }
    }

    private void WalkAnnotation(PdfDictionary annot, PdfDictionary? defaultResources)
    {
        if (_document.Resolve(annot.GetOptional("AP") ?? PdfNull.Instance) is PdfDictionary ap)
        {
            foreach (var state in new[] { "N", "R", "D" })
            {
                switch (_document.Resolve(ap.GetOptional(state) ?? PdfNull.Instance))
                {
                    case PdfStream stream:
                        WalkAppearance(stream, defaultResources);
                        break;
                    case PdfDictionary states:
                        foreach (var value in states.Values)
                            if (_document.Resolve(value) is PdfStream s)
                                WalkAppearance(s, defaultResources);
                        break;
                }
            }
        }

        // A FreeText annotation's /Contents is regenerated with its /DA font.
        if (annot.GetNameOrNull("Subtype") == "FreeText")
            RecordFieldText(annot.GetOptional("DA"), defaultResources, null, annot.GetOptional("Contents"));
    }

    private void WalkAppearance(PdfStream stream, PdfDictionary? defaultResources)
    {
        if (!_appearancesSeen.Add(stream)) return;
        var page = _page ?? (_document.PageCount > 0 ? _document.GetPage(1) : null);
        if (page == null) return;
        var walker = new ContentStreamWalker(Array.Empty<byte>(), page);
        // §12.7.3.3: an appearance's names fall back to the form's /DR.
        if (defaultResources != null) walker.PushResources(defaultResources);
        RunForm(walker, stream, fromIdentityCtm: true);
        DrainNested();
    }

    private void WalkField(PdfObject fieldObj, PdfDictionary acroForm, PdfDictionary? defaultResources,
        PdfObject? inheritedDa, HashSet<PdfDictionary> seen, int depth)
    {
        if (depth > 64 || _document.Resolve(fieldObj) is not PdfDictionary field || !seen.Add(field)) return;
        var da = field.GetOptional("DA") ?? inheritedDa ?? acroForm.GetOptional("DA");
        var fieldResources = _document.Resolve(field.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary
            ?? defaultResources;

        WalkAnnotationAppearanceOnly(field, defaultResources);

        RecordFieldText(da, defaultResources, fieldResources, field.GetOptional("V"));
        RecordFieldText(da, defaultResources, fieldResources, field.GetOptional("DV"));
        RecordFieldText(da, defaultResources, fieldResources, field.GetOptional("Opt"));
        if (_document.Resolve(field.GetOptional("MK") ?? PdfNull.Instance) is PdfDictionary mk)
            foreach (var key in new[] { "CA", "RC", "AC" })
                RecordFieldText(da, defaultResources, fieldResources, mk.GetOptional(key));

        if (_document.Resolve(field.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
            foreach (var kid in kids)
                WalkField(kid, acroForm, defaultResources, da, seen, depth + 1);
    }

    private void WalkAnnotationAppearanceOnly(PdfDictionary widget, PdfDictionary? defaultResources)
    {
        if (_document.Resolve(widget.GetOptional("AP") ?? PdfNull.Instance) is not PdfDictionary) return;
        WalkAnnotation(widget, defaultResources);
    }

    private static readonly Regex DaFont = new(@"/([^\s/\[\]<>(){}%]+)\s+[-+0-9.]+\s+Tf", RegexOptions.CultureInvariant);

    private void RecordFieldText(PdfObject? daObj, PdfDictionary? defaultResources, PdfDictionary? fieldResources, PdfObject? valueObj)
    {
        if (valueObj == null || _document.Resolve(daObj ?? PdfNull.Instance) is not PdfString da) return;
        var text = new List<string>();
        CollectStrings(_document.Resolve(valueObj), text, 0);
        if (text.Count == 0) return;

        foreach (Match m in DaFont.Matches(da.Value))
        {
            var name = m.Groups[1].Value;
            foreach (var resources in new[] { fieldResources, defaultResources })
            {
                if (resources == null) continue;
                if (_document.Resolve(resources.GetOptional("Font") ?? PdfNull.Instance) is not PdfDictionary fonts) continue;
                if (_document.Resolve(fonts.GetOptional(name) ?? PdfNull.Instance) is not PdfDictionary font) continue;
                var key = KeyOf(font, fonts.GetOptional(name));
                if (!FieldText.TryGetValue(key, out var set)) FieldText[key] = set = new HashSet<int>();
                foreach (var s in text)
                    for (var i = 0; i < s.Length; i++)
                    {
                        var cp = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                            ? char.ConvertToUtf32(s[i], s[++i]) : s[i];
                        set.Add(cp);
                    }
            }
        }
    }

    private void CollectStrings(PdfObject obj, List<string> into, int depth)
    {
        if (depth > 8) return;
        switch (obj)
        {
            case PdfString s:
                into.Add(s.Value);
                break;
            case PdfArray a:
                foreach (var item in a) CollectStrings(_document.Resolve(item), into, depth + 1);
                break;
            case PdfStream stream when depth == 0:
                // A rich-text or long value held in a stream (§12.7.4.3).
                if (!stream.IsFiltered || stream.TryEnsureDecoded())
                    into.Add(System.Text.Encoding.Latin1.GetString(stream.DecodedData));
                break;
        }
    }

    private void Record(ContentStreamWalker walker, in WalkedGlyph glyph)
    {
        if (walker.CurrentFont is not { } font) return;
        var key = KeyFor(font, walker, glyph.FontName);
        if (!Fonts.TryGetValue(key, out var use)) Fonts[key] = use = new FontUse(font);
        use.Font = font;
        use.Codes.Add(glyph.CharCode);
        use.Cids.Add(glyph.Cid);
        use.Unicode.TryAdd(glyph.CharCode, glyph.Unicode);
        use.CodeToCid.TryAdd(glyph.CharCode, glyph.Cid);
    }

    private void OnOperator(string name, List<PdfObject> operands, ContentStreamWalker walker)
    {
        switch (name)
        {
            case "Do":
                if (operands.Count >= 1 && operands[0] is PdfName xName &&
                    walker.ResolveXObject(xName.Value) is PdfStream form && form.GetNameOrNull("Subtype") == "Form")
                    RunForm(walker, form, fromIdentityCtm: false);
                break;
            case "scn":
            case "SCN":
                if (operands.Count >= 1 && operands[^1] is PdfName patternName &&
                    walker.ResolveResource("Pattern", patternName.Value) is PdfStream pattern &&
                    pattern.GetInt("PatternType", 0) == 1)
                    Note(pattern, walker, null);
                break;
            case "gs":
                if (operands.Count >= 1 && operands[0] is PdfName gsName &&
                    walker.ResolveResource("ExtGState", gsName.Value) is PdfDictionary gs &&
                    _document.Resolve(gs.GetOptional("SMask") ?? PdfNull.Instance) is PdfDictionary smask &&
                    _document.Resolve(smask.GetOptional("G") ?? PdfNull.Instance) is PdfStream group)
                    Note(group, walker, null);
                break;
        }
    }

    private void OnTextShow(ContentStreamWalker walker)
    {
        var font = walker.CurrentFont;
        if (font == null || font.GetNameOrNull("Subtype") != "Type3" || !_type3Seen.Add(font)) return;
        if (_document.Resolve(font.GetOptional("CharProcs") ?? PdfNull.Instance) is not PdfDictionary procs) return;
        var fontResources = _document.Resolve(font.GetOptional("Resources") ?? PdfNull.Instance) as PdfDictionary;
        foreach (var value in procs.Values)
            if (_document.Resolve(value) is PdfStream proc)
                Note(proc, walker, fontResources);
    }

    private void Note(PdfStream stream, ContentStreamWalker walker, PdfDictionary? extra)
    {
        if (_nestedSeen.Add(stream))
            _nested.Add((stream, walker.ActiveResources.FirstOrDefault(), extra));
    }

    private void DrainNested()
    {
        var page = _page ?? (_document.PageCount > 0 ? _document.GetPage(1) : null);
        if (page == null) return;
        for (var i = 0; i < _nested.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (stream, scope, extra) = _nested[i];
            var walker = new ContentStreamWalker(Array.Empty<byte>(), page);
            if (scope != null) walker.PushResources(scope);
            if (extra != null) walker.PushResources(extra);
            RunForm(walker, stream, fromIdentityCtm: true);
        }
        _nested.Clear();
    }

    private void RunForm(ContentStreamWalker walker, PdfStream stream, bool fromIdentityCtm)
    {
        var resources = _document.Resolve(stream.GetOptional("Resources") ?? PdfNull.Instance) as PdfDictionary;
        if (_formPath.Count >= MaxFormDepth || !_formPath.Add(stream))
            return;
        try
        {
            if (stream.IsFiltered && !stream.TryEnsureDecoded())
            {
                MarkUncertain(resources);
                foreach (var scope in walker.ActiveResources) MarkUncertain(scope);
                return;
            }
            var sink = new UsageSink(this, walker);
            walker.RunNested(stream.DecodedData, resources, null, fromIdentityCtm, ref sink);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            MarkUncertain(resources);
            foreach (var scope in walker.ActiveResources) MarkUncertain(scope);
        }
        finally
        {
            _formPath.Remove(stream);
        }
    }

    private void MarkUncertain(PdfDictionary? resources)
    {
        if (resources == null) return;
        if (_document.Resolve(resources.GetOptional("Font") ?? PdfNull.Instance) is not PdfDictionary fonts) return;
        foreach (var value in fonts.Values)
            if (_document.Resolve(value) is PdfDictionary font)
                Uncertain.Add(KeyOf(font, value));
    }

    private readonly struct UsageSink(FontGlyphUsage owner, ContentStreamWalker walker) : IContentStreamSink
    {
        public void OnOperator(string name, List<PdfObject> operands) => owner.OnOperator(name, operands, walker);
        public void OnInlineImage(PdfDictionary imageParams, byte[] imageData) { }
        public void OnTextShowBegin() => owner.OnTextShow(walker);
        public void OnStringBegin() { }
        public void OnGlyph(in WalkedGlyph glyph) => owner.Record(walker, in glyph);
        public void OnStringEnd(int byteCount) { }
        public void OnTextShowEnd() { }
        public void OnTjAdjustment(double adjustment) { }
    }
}
