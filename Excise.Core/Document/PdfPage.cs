using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Represents a page in a PDF document.
/// </summary>
public partial class PdfPage
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _pageDict;

    /// <summary>
    /// The 1-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Creates a new page wrapper.
    /// </summary>
    internal PdfPage(PdfDocument document, PdfDictionary pageDict, int pageNumber)
    {
        _document = document;
        _pageDict = pageDict;
        PageNumber = pageNumber;
    }

    /// <summary>
    /// The underlying page dictionary.
    /// </summary>
    public PdfDictionary Dictionary => _pageDict;

    /// <summary>
    /// The document this page belongs to.
    /// </summary>
    public PdfDocument Document => _document;

    /// <summary>
    /// All annotations on this page (§12.5).
    /// Covers every subtype: Text, Link, Highlight, Widget, Stamp, Ink, etc.
    /// </summary>
    public IReadOnlyList<PdfAnnotation> GetAnnotations()
    {
        var pageMap    = PdfOutlineParser.BuildPageRefMap(_document);
        var namedDests = PdfOutlineParser.BuildNamedDestinations(_document);
        return PdfAnnotationParser.Parse(_document, _pageDict, pageMap, namedDests);
    }

    /// <summary>
    /// AcroForm fields whose Widget annotation lives on this page (§12.7).
    /// Returns the document-wide AcroForm filtered to this page, plus any
    /// Widget annotations that live directly in this page's own /Annots array
    /// but were never reachable by walking /AcroForm/Fields (#670) — a Widget
    /// annotation may legally BE its own field dictionary (a "merged"
    /// field/widget, §12.7.3.1), carrying /FT and /V directly, with no entry
    /// anywhere in the /Fields tree pointing at it. Those are surfaced here so
    /// extraction and redaction can see their values; widgets already reached
    /// through /AcroForm/Fields are not duplicated. Returns an empty list only
    /// when the document has no /AcroForm dictionary AND this page's own
    /// /Annots has no merged field/widgets either.
    /// </summary>
    public IReadOnlyList<PdfField> GetFormFields()
    {
        var form = _document.GetAcroForm();
        var pageNum = PageNumber;
        var linked = form?.Fields.Where(f => f.PageNumber == pageNum).ToList()
            ?? new List<PdfField>();

        var linkedWidgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (form != null)
        {
            foreach (var f in form.Fields)
                foreach (var w in f.WidgetDictionaries)
                    linkedWidgets.Add(w);
        }

        var orphaned = PdfAcroFormParser.ExtractOrphanedPageWidgetFields(_document, _pageDict, pageNum, linkedWidgets);
        if (orphaned.Count == 0)
            return linked;

        var combined = new List<PdfField>(linked.Count + orphaned.Count);
        combined.AddRange(linked);
        combined.AddRange(orphaned);
        return combined;
    }

    /// <summary>
    /// Internal-document link annotations on this page (PDF spec §12.5.6.5).
    /// External / URI links are filtered out; what's returned is only the
    /// kind of link a clickable table-of-contents or back-of-book index
    /// produces — pointers to other pages in this document.
    /// </summary>
    public IReadOnlyList<PdfLink> GetLinks()
    {
        // Build the page-ref map and named-dest map fresh per call.
        // Callers that want them across many pages should use the static
        // PdfLinkParser.Parse with shared maps to avoid the redundant work.
        var pageMap = PdfOutlineParser.BuildPageRefMap(_document);
        var namedDests = PdfOutlineParser.BuildNamedDestinations(_document);
        return PdfLinkParser.Parse(_document, _pageDict, pageMap, namedDests);
    }

    /// <summary>
    /// Page width in points.
    /// </summary>
    public double Width => MediaBox.Width;

    /// <summary>
    /// Page height in points.
    /// </summary>
    public double Height => MediaBox.Height;

    /// <summary>
    /// Page rotation in degrees, always folded into the canonical {0, 90, 180, 270}.
    /// </summary>
    /// <remarks>
    /// The stored /Rotate may be any integer — the spec permits multiples of 90,
    /// and real-world files carry negatives (-90) and values past a full turn
    /// (450). A plain `% 360` preserves sign in C#, so this getter previously
    /// returned -90 for a /Rotate -90 page. Every consumer then had to re-fold it
    /// (SkiaRenderer, PdfViewerControl, PdfCoordinateMapper each carried their own
    /// copy), and any consumer that forgot would fall through a `switch` to the
    /// unrotated case — silently mapping coordinates as if the page were upright.
    /// Normalizing once, here, is what makes that class of bug impossible.
    /// </remarks>
    public int Rotation
    {
        get => ((GetInheritedInt("Rotate", 0) % 360) + 360) % 360;
        set
        {
            // Normalize to 0, 90, 180, or 270
            value = ((value % 360) + 360) % 360;
            if (value != 0 && value != 90 && value != 180 && value != 270)
                throw new ArgumentException("Rotation must be 0, 90, 180, or 270 degrees", nameof(value));

            if (value == 0)
                _pageDict.Remove("Rotate");
            else
                _pageDict.SetInt("Rotate", value);
        }
    }

    /// <summary>
    /// The media box (page boundaries).
    /// </summary>
    /// <remarks>
    /// A page with no /MediaBox anywhere in its inheritance chain is malformed:
    /// the spec makes it a required inheritable page attribute. excise
    /// substitutes <see cref="DefaultMediaBox"/> rather than refusing — see
    /// that field for why, and for the correction to the claim that used to
    /// live here.
    ///
    /// The history, because this block twice said the opposite of the code:
    /// the original failure was an unhandled InvalidOperationException on nine
    /// corpus files, counted as crashes by #648's DoS gate. #871 made it a
    /// typed PdfParseException, which fixed the crash. This text then asserted
    /// that refusing was also the right END state, "no reference renderer
    /// renders any of the pdfium-corpus fixtures that hit this" — read off
    /// oracle statuses that actually meant NEVER ASKED (#882). pdftocairo
    /// renders all ten. #884 replaced the refusal with the default; this
    /// remark was left behind still recommending the refusal.
    /// </remarks>
    public PdfRectangle MediaBox => GetInheritedRectangle("MediaBox") ?? DefaultMediaBox;

    /// <summary>
    /// US Letter, used when a malformed page has no /MediaBox anywhere in its
    /// inheritance chain.
    /// </summary>
    /// <remarks>
    /// /MediaBox is a required inheritable page attribute, so a page without one
    /// is malformed — but refusing it is not what other readers do, and for a
    /// redaction tool showing the page beats showing nothing. Measured:
    /// pdftocairo renders these at exactly 612x792 (checked against
    /// pdfium/bug_451265.pdf at 72dpi), and mutool renders them too. Ten corpus
    /// pages were being refused here while both of those displayed them (#884).
    ///
    /// This supersedes the reasoning in #871, which hardened the same line from
    /// an untyped InvalidOperationException to a typed PdfParseException and
    /// justified refusing by claiming no reference renderer handled these
    /// either. That claim was read off oracle statuses that meant "never asked"
    /// (#882). The typed exception was the right fix for the crash; refusing was
    /// not the right end state.
    ///
    /// The size is a guess, and a guess is the honest position: the document
    /// does not say. Anything derived from it — redaction coordinates, page
    /// geometry — is as trustworthy as the guess, which is why this is a named
    /// constant rather than an inline literal.
    /// </remarks>
    internal static readonly PdfRectangle DefaultMediaBox = new(0, 0, 612, 792);

    /// <summary>
    /// The crop box (visible area). Falls back to MediaBox if not specified.
    /// </summary>
    public PdfRectangle CropBox => GetInheritedRectangle("CropBox") ?? MediaBox;

    /// <summary>
    /// The page box that a conforming viewer renders: the intersection of a
    /// valid <see cref="CropBox"/> and <see cref="MediaBox"/>, with a valid
    /// MediaBox used when the crop is empty or disjoint.
    /// </summary>
    /// <remarks>
    /// PDF processors must not display a CropBox outside the MediaBox. Keeping
    /// this normalization on the page contract gives rendering, viewer layout,
    /// and pointer-to-content mapping one source of truth for the visible
    /// origin and extent.
    /// </remarks>
    public PdfRectangle EffectiveCropBox
    {
        get
        {
            var mediaBox = MediaBox.Normalize();
            var cropBox = CropBox.Normalize();

            if (HasPositiveArea(mediaBox))
            {
                if (!HasPositiveArea(cropBox))
                    return mediaBox;

                var intersection = new PdfRectangle(
                    Math.Max(mediaBox.Left, cropBox.Left),
                    Math.Max(mediaBox.Bottom, cropBox.Bottom),
                    Math.Min(mediaBox.Right, cropBox.Right),
                    Math.Min(mediaBox.Top, cropBox.Top));
                return HasPositiveArea(intersection) ? intersection : mediaBox;
            }

            return HasPositiveArea(cropBox) ? cropBox : DefaultMediaBox;
        }
    }

    /// <summary>
    /// The bleed box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle BleedBox => GetRectangle("BleedBox") ?? CropBox;

    /// <summary>
    /// The trim box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle TrimBox => GetRectangle("TrimBox") ?? CropBox;

    /// <summary>
    /// The art box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle ArtBox => GetRectangle("ArtBox") ?? CropBox;

    /// <summary>
    /// Width of the visible page as displayed, after applying <see cref="Rotation"/>.
    /// </summary>
    public double VisualWidth => Rotation is 90 or 270
        ? EffectiveCropBox.Height
        : EffectiveCropBox.Width;

    /// <summary>
    /// Height of the visible page as displayed, after applying <see cref="Rotation"/>.
    /// </summary>
    public double VisualHeight => Rotation is 90 or 270
        ? EffectiveCropBox.Width
        : EffectiveCropBox.Height;

    /// <summary>
    /// Map a rectangle from <em>visual</em> space — what the viewer sees after
    /// the page <see cref="Rotation"/> is applied: origin at the top-left,
    /// x increasing right, y increasing <b>down</b>, sized
    /// <see cref="VisualWidth"/>×<see cref="VisualHeight"/> — into
    /// content-stream space (PDF default: MediaBox origin at the bottom-left,
    /// y increasing up), which is what <see cref="Excise.Core.Text.Segmentation.PdfPageRedactionExtensions.RedactArea(PdfPage, PdfRectangle, Excise.Core.Text.Segmentation.GlyphRemovalStrategy)"/>
    /// and the rest of the engine operate in.
    /// </summary>
    /// <remarks>
    /// This is the single source of truth for the visual↔content mapping on
    /// rotated pages (#356). Callers that have a selection in rendered/visual
    /// coordinates must route it through here rather than applying an ad-hoc
    /// Y-flip, which is only correct at 0° rotation and silently redacts the
    /// wrong region at 90/180/270. The four corners are transformed and the
    /// axis-aligned bounding box returned (a 90° rotation keeps rectangles
    /// axis-aligned, so no area is lost or gained).
    /// </remarks>
    /// <param name="visualRect">Rectangle in visual space; its numeric x range
    /// and y range are interpreted as visual coordinates (y measured downward).</param>
    public PdfRectangle ToContentStreamCoordinates(PdfRectangle visualRect)
    {
        var visibleBox = EffectiveCropBox;
        double l = visibleBox.Left, b = visibleBox.Bottom, w = visibleBox.Width, h = visibleBox.Height;
        int r = Rotation;

        // Map one visual point (x right, y down, top-left origin) to content
        // space (x right, y up, MediaBox bottom-left origin). Derived from
        // rotating the unrotated page image clockwise by /Rotate degrees.
        (double x, double y) Map(double vx, double vy) => r switch
        {
            90  => (l + vy,         b + vx),
            180 => (l + w - vx,     b + vy),
            270 => (l + w - vy,     b + h - vx),
            _   => (l + vx,         b + h - vy),   // 0°
        };

        var p1 = Map(visualRect.Left, visualRect.Bottom);
        var p2 = Map(visualRect.Left, visualRect.Top);
        var p3 = Map(visualRect.Right, visualRect.Bottom);
        var p4 = Map(visualRect.Right, visualRect.Top);

        double minX = Math.Min(Math.Min(p1.x, p2.x), Math.Min(p3.x, p4.x));
        double maxX = Math.Max(Math.Max(p1.x, p2.x), Math.Max(p3.x, p4.x));
        double minY = Math.Min(Math.Min(p1.y, p2.y), Math.Min(p3.y, p4.y));
        double maxY = Math.Max(Math.Max(p1.y, p2.y), Math.Max(p3.y, p4.y));

        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    private static bool HasPositiveArea(PdfRectangle rectangle) =>
        rectangle.Right > rectangle.Left && rectangle.Top > rectangle.Bottom;

    /// <summary>
    /// This page's presentation transition effect (/Trans, ISO 32000-2:2020 §12.4.4),
    /// used by presentation-mode viewers when navigating to this page. /Trans is a
    /// direct (non-inheritable) page entry. Null if the page has no /Trans.
    /// excise parses and round-trips this dictionary; it does not implement
    /// presentation-mode playback (issue #331 — UI integration deferred).
    /// </summary>
    public PdfPageTransition? Transition => PdfPageTransitionParser.Parse(_document, _pageDict);

    /// <summary>
    /// The page's display duration in seconds (/Dur, ISO 32000-2:2020 §12.4.4) —
    /// how long a full-screen presentation-mode viewer should show this page
    /// before automatically advancing. /Dur is a direct (non-inheritable) page
    /// entry. Null if the page has no /Dur.
    /// </summary>
    public double? Duration => _pageDict.TryGetValue("Dur", out var durObj) && durObj.TryGetNumber(out var dur)
        ? dur
        : null;

    /// <summary>
    /// This page's additional actions (/AA, ISO 32000-2:2020 §12.6.3, Table 203).
    /// Keys are trigger names: "O" (page opened / becomes visible), "C" (page closed /
    /// no longer visible). Empty if the page has no /AA. Never executed by excise —
    /// parsed for round-trip and inspection only.
    /// </summary>
    public IReadOnlyDictionary<string, PdfAction> AdditionalActions =>
        PdfActionParser.ParseAdditionalActions(_document, _pageDict.GetOptional("AA"));

    /// <summary>
    /// This page's embedded thumbnail image stream (/Thumb, ISO 32000-2:2020 §12.3.4),
    /// if present. The stream is a small preview image (typically DeviceGray or
    /// DeviceRGB, possibly DCT- or Flate-encoded) that a viewer may show in a
    /// thumbnail panel instead of rendering the full page. excise parses and
    /// preserves this stream on save but does not decode/render it — a thumbnail
    /// strip should fall back to the renderer when this is null (issue #331).
    /// </summary>
    public PdfStream? ThumbnailStream => _pageDict.ResolveStream(_document, "Thumb");

    /// <summary>
    /// Get the page resources dictionary.
    /// </summary>
    public PdfDictionary? Resources => GetInheritedDictionary("Resources");

    /// <summary>
    /// Gets a graphics context for drawing on this page.
    /// </summary>
    public PdfGraphics GetGraphics()
    {
        // Return a fresh graphics context each call. Caching a single
        // instance bit us when callers used `using var g = …` — once
        // disposed, subsequent calls handed back the same dead instance.
        return new PdfGraphics(this);
    }

    /// <summary>
    /// Get a font from the page resources.
    /// </summary>
    public PdfDictionary? GetFont(string fontName)
    {
        // /Font is often an indirect reference (most browsers/Word/WeasyPrint
        // emit `/Font N 0 R`), and Resources is sometimes one too. Resolve
        // both, otherwise the font lookup silently misses and the renderer
        // falls back to a default fallback typeface.
        var fontsObj = Resources?.GetOptional("Font");
        if (fontsObj == null) return null;
        var fonts = _document.Resolve(fontsObj) as PdfDictionary;
        if (fonts == null) return null;

        var fontObj = fonts.GetOptional(fontName);
        if (fontObj == null)
            return null;

        return _document.Resolve(fontObj) as PdfDictionary;
    }

    /// <summary>
    /// Get all fonts used on this page.
    /// </summary>
    public IEnumerable<(string Name, PdfDictionary Font)> GetFonts()
    {
        var fonts = Resources?.ResolveDictionary(_document, "Font");
        if (fonts == null)
            yield break;

        foreach (var kvp in fonts)
        {
            var fontDict = _document.Resolve(kvp.Value) as PdfDictionary;
            if (fontDict != null)
            {
                yield return (kvp.Key.Value, fontDict);
            }
        }
    }

    /// <summary>
    /// Adds a font to the page resources if not already present.
    /// Returns the font resource name (e.g., "F1", "F2").
    /// </summary>
    public string AddFont(PdfFont font)
    {
        // Get or create Resources dictionary
        var resources = EnsureResources();

        // Get or create Font dictionary within Resources
        if (!resources.TryGetValue("Font", out var fontDictObj) || fontDictObj is not PdfDictionary fontDict)
        {
            fontDict = new PdfDictionary();
            resources["Font"] = fontDict;
        }

        // Check if this font is already registered by base font name
        foreach (var kvp in fontDict)
        {
            var existingFont = _document.Resolve(kvp.Value) as PdfDictionary;
            if (existingFont != null)
            {
                var existingBaseFont = existingFont.GetNameOrNull("BaseFont");
                if (existingBaseFont == font.BaseFont)
                {
                    return kvp.Key.Value; // Return existing name
                }
            }
        }

        // Find next available font name
        var fontName = font.Name;
        int counter = 1;
        while (fontDict.ContainsKey(fontName))
        {
            fontName = $"F{counter++}";
        }

        // Add the font dictionary (embedded fonts register their own indirect
        // stream objects in the document and return a Type0 dictionary).
        var builtFont = font.BuildFontDictionary(_document);
        fontDict[fontName] = font.PreferIndirectFontDictionary
            ? _document.AddIndirectObject(builtFont)
            : builtFont;

        return fontName;
    }

    /// <summary>
    /// Ensures the page has a Resources dictionary, creating one if needed.
    /// </summary>
    private PdfDictionary EnsureResources()
    {
        var resources = Resources;
        if (resources != null)
            return resources;

        // Create a new Resources dictionary
        resources = new PdfDictionary();
        _pageDict["Resources"] = resources;
        return resources;
    }

    /// <summary>
    /// Get an XObject (form or image) from the page resources.
    /// </summary>
    public PdfObject? GetXObject(string name)
    {
        var xobjects = Resources?.ResolveDictionary(_document, "XObject");
        if (xobjects == null)
            return null;

        var obj = xobjects.GetOptional(name);
        return obj != null ? _document.Resolve(obj) : null;
    }

    /// <summary>
    /// Get a graphics state from the page resources.
    /// </summary>
    public PdfDictionary? GetExtGState(string name)
    {
        var extGState = Resources?.ResolveDictionary(_document, "ExtGState");
        if (extGState == null)
            return null;

        var obj = extGState.GetOptional(name);
        return obj != null ? _document.Resolve(obj) as PdfDictionary : null;
    }

    /// <summary>
    /// Get a shading dictionary from the page resources.
    /// </summary>
    public PdfDictionary? GetShading(string name)
    {
        var shadings = Resources?.ResolveDictionary(_document, "Shading");
        if (shadings == null)
            return null;

        var obj = shadings.GetOptional(name);
        return obj != null ? _document.Resolve(obj) as PdfDictionary : null;
    }

    /// <summary>
    /// Get a color space object from the page resources.
    /// Returns the raw PdfObject (name or array) for parsing into PdfColorSpace.
    /// </summary>
    public PdfObject? GetColorSpaceObject(string name)
    {
        var colorSpaces = Resources?.ResolveDictionary(_document, "ColorSpace");
        if (colorSpaces == null)
            return null;

        var obj = colorSpaces.GetOptional(name);
        return obj != null ? _document.Resolve(obj) : null;
    }

    #region Inherited Properties

    /// <summary>
    /// Get an inherited integer value (walks up page tree).
    /// </summary>
    private int GetInheritedInt(string key, int defaultValue)
    {
        var current = _pageDict;
        var visited = NewAncestorVisitedSet();
        while (current != null)
        {
            if (current.ContainsKey(key))
                return current.GetInt(key, defaultValue);

            current = NextPageTreeAncestor(current, visited);
        }
        return defaultValue;
    }

    /// <summary>
    /// Step to the next ancestor in the page tree, refusing to revisit a node.
    /// </summary>
    /// <remarks>
    /// A /Parent chain is attacker-controlled data and is not guaranteed to be
    /// acyclic. Without this guard a node whose /Parent points at itself makes
    /// every inherited-attribute lookup spin forever — pdfium's
    /// bug_517126568.pdf is 577 bytes, draws one blue rectangle, and cost
    /// excise over 120 seconds of CPU while pdftocairo and Ghostscript both
    /// render it immediately (#881).
    ///
    /// That is a denial-of-service primitive for a tool whose entire input is
    /// documents someone else produced, and the file does not even look
    /// malformed. Descent through /Kids already had cycle detection; the
    /// ascent through /Parent did not.
    /// </remarks>
    private PdfDictionary? NextPageTreeAncestor(PdfDictionary current, HashSet<PdfDictionary> visited)
    {
        var parentRef = current.GetReferenceOrNull("Parent");
        if (parentRef == null)
            return null;

        if (_document.GetObject(parentRef) is not PdfDictionary parent)
            return null;

        // Already seen: the chain loops. Stop as though the tree ended, which
        // yields the same answer an acyclic tree would for an absent key.
        return visited.Add(parent) ? parent : null;
    }

    private HashSet<PdfDictionary> NewAncestorVisitedSet()
        => new(ReferenceEqualityComparer.Instance) { _pageDict };

    /// <summary>
    /// Get an inherited dictionary value (walks up page tree).
    /// </summary>
    private PdfDictionary? GetInheritedDictionary(string key)
    {
        var current = _pageDict;
        var visited = NewAncestorVisitedSet();
        while (current != null)
        {
            var obj = current.GetOptional(key);
            if (obj != null)
            {
                var resolved = _document.Resolve(obj);
                if (resolved is PdfDictionary dict)
                    return dict;
            }

            current = NextPageTreeAncestor(current, visited);
        }
        return null;
    }

    /// <summary>
    /// Get an inherited rectangle (walks up page tree).
    /// </summary>
    private PdfRectangle? GetInheritedRectangle(string key)
    {
        var current = _pageDict;
        var visited = NewAncestorVisitedSet();
        while (current != null)
        {
            var rect = GetRectangleFromDict(current, key);
            if (rect.HasValue)
                return rect;

            current = NextPageTreeAncestor(current, visited);
        }
        return null;
    }

    /// <summary>
    /// Get a rectangle from this page's dictionary (non-inherited).
    /// </summary>
    private PdfRectangle? GetRectangle(string key)
    {
        return GetRectangleFromDict(_pageDict, key);
    }

    /// <summary>
    /// Get a rectangle from a dictionary.
    /// </summary>
    private PdfRectangle? GetRectangleFromDict(PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null)
            return null;

        var resolved = _document.Resolve(obj);
        if (resolved is not PdfArray arr || arr.Count != 4)
            return null;

        return new PdfRectangle(
            arr.GetNumber(0),
            arr.GetNumber(1),
            arr.GetNumber(2),
            arr.GetNumber(3)
        );
    }

    #endregion

    /// <inheritdoc />
    public override string ToString() => $"Page {PageNumber} ({Width}x{Height} pts)";
}

/// <summary>
/// A recoverable problem encountered while assembling page content streams for
/// best-effort viewing. Strict editing/redaction paths still reject these cases.
/// </summary>
internal readonly record struct ContentStreamReadWarning(
    string Code,
    string Message,
    int ObjectNumber,
    int GenerationNumber,
    string? Detail = null)
{
    public const string ImageOnlyFilterInContentStreamCode = "IMAGE_ONLY_FILTER_IN_CONTENT_STREAM";
    public const string UndecodedFilteredContentStreamCode = "UNDECODED_FILTERED_CONTENT_STREAM";

    internal static ContentStreamReadWarning ImageOnlyFilter(
        int objectNumber,
        int generationNumber,
        string filter)
        => new(
            ImageOnlyFilterInContentStreamCode,
            $"Page content stream {FormatObjectId(objectNumber, generationNumber)} uses /{filter}; PDF restricts JBIG2Decode to image XObjects, so the stream was skipped for best-effort rendering.",
            objectNumber,
            generationNumber,
            filter);

    internal static ContentStreamReadWarning UndecodedFilter(
        int objectNumber,
        int generationNumber,
        IReadOnlyList<string> filters)
    {
        var detail = filters.Count == 0 ? null : string.Join(",", filters);
        return new(
            UndecodedFilteredContentStreamCode,
            $"Page content stream {FormatObjectId(objectNumber, generationNumber)} was filtered but not decoded, so it was skipped for best-effort rendering.",
            objectNumber,
            generationNumber,
            detail);
    }

    public override string ToString()
        => string.IsNullOrWhiteSpace(Detail) ? $"{Code}: {Message}" : $"{Code}: {Message} ({Detail})";

    private static string FormatObjectId(int objectNumber, int generationNumber)
        => objectNumber > 0 ? $"{objectNumber} {generationNumber} R" : "(direct)";
}

/// <summary>
/// A rectangle in PDF coordinates (bottom-left origin).
/// </summary>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>
    /// Width of the rectangle.
    /// </summary>
    public double Width => Math.Abs(Right - Left);

    /// <summary>
    /// Height of the rectangle.
    /// </summary>
    public double Height => Math.Abs(Top - Bottom);

    /// <summary>
    /// Create a rectangle from an array.
    /// </summary>
    public static PdfRectangle FromArray(PdfArray arr)
    {
        if (arr.Count != 4)
            throw new ArgumentException("Rectangle array must have 4 elements");

        return new PdfRectangle(
            arr.GetNumber(0),
            arr.GetNumber(1),
            arr.GetNumber(2),
            arr.GetNumber(3)
        );
    }

    /// <summary>
    /// Normalize the rectangle (ensure Left &lt; Right and Bottom &lt; Top).
    /// </summary>
    public PdfRectangle Normalize()
    {
        return new PdfRectangle(
            Math.Min(Left, Right),
            Math.Min(Bottom, Top),
            Math.Max(Left, Right),
            Math.Max(Bottom, Top)
        );
    }

    /// <summary>
    /// Check if this rectangle intersects with another rectangle.
    /// </summary>
    public bool IntersectsWith(PdfRectangle other)
    {
        var a = Normalize();
        var b = other.Normalize();
        return a.Left < b.Right && a.Right > b.Left &&
               a.Bottom < b.Top && a.Top > b.Bottom;
    }

    /// <summary>
    /// Check if a point is contained within this rectangle.
    /// </summary>
    public bool Contains(double x, double y)
    {
        var norm = Normalize();
        return x >= norm.Left && x <= norm.Right &&
               y >= norm.Bottom && y <= norm.Top;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Left:F2}, {Bottom:F2}, {Right:F2}, {Top:F2}]";
}

/// <summary>
/// A point in PDF coordinates (bottom-left origin).
/// </summary>
public readonly record struct PdfPoint(double X, double Y)
{
    /// <inheritdoc />
    public override string ToString() => $"({X:F2}, {Y:F2})";
}
