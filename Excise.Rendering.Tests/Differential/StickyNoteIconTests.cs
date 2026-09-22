using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1071 — a /Text annotation picks its icon with <c>/Name</c> (§12.5.6.4
/// Table 172). Every name drew the SAME two-bar glyph, so a Help marker and an
/// Insert marker were indistinguishable.
///
/// <para><b>Superseded by #1794.</b> excise's OWN viewer no longer draws a
/// small per-/Name icon at all: <c>RenderStickyNoteDefault</c> now draws a
/// real post-it-sized card with the note's /Contents wrapped and visible on
/// it, and /Name no longer selects anything (every icon name renders
/// identically — the card and its text). So
/// <c>EveryPairOfIcons_IsVisuallyDistinct</c>, this file's namesake gate, no
/// longer has a claim to make; it is removed rather than left green on a
/// property that stopped being true on the day #1071 was filed. The
/// remaining tests below still hold: a note still draws SOMETHING visible
/// (now the card, not a glyph), an unrecognised/absent name still doesn't
/// vanish (it never depended on /Name), and a degenerate /Rect still falls
/// back to a fixed-size box. Issue #1795 tracks restoring per-icon meaning
/// via a real <c>/AP</c> other readers can show.</para>
///
/// <para><b>Why the remaining gates compare excise to excise, which the
/// house rule normally forbids.</b> The no-self-oracle rule exists for
/// properties where there is an external truth to check against — does the
/// text survive redaction, does the page render correctly. Here there is
/// none for excise's OWN chosen post-it visual: no spec and no reference
/// renderer has an opinion on it. What IS checkable without an oracle is
/// that excise's own output is internally consistent — a note draws
/// something, an unknown name doesn't erase it, a degenerate rect still gets
/// a box.</para>
/// </summary>
public class StickyNoteIconTests : IDisposable
{
    private const int Dpi = 288;          // 4x nominal: the marker is only ~17pt
    private const int PageSize = 80;

    /// <summary>Every /Name §12.5.6.4 Table 172 defines.</summary>
    public static readonly string[] IconNames =
    {
        "Comment", "Key", "Note", "Help", "NewParagraph", "Paragraph", "Insert",
    };

    private readonly List<string> _temp = new();

    public static TheoryData<string> AllNames()
    {
        var d = new TheoryData<string>();
        foreach (var n in IconNames) d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void EveryIconName_DrawsACard(string name)
    {
        using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(name)));

        InkPixels(bmp).Should().BeGreaterThan(200,
            $"/Name /{name} must still draw the post-it card (#1794) — a /Text annotation " +
            "draws nothing else, so an invisible one is an annotation the reviewer never " +
            "sees while its /Contents still ships to the recipient");
    }

    /// <summary>
    /// §12.5.6.4: /Note is the default icon name. #1794 stopped /Name
    /// selecting anything drawn (every name renders the same post-it card),
    /// which trivially satisfies "an unfamiliar name must not vanish the
    /// annotation" — but the property is still worth pinning explicitly so a
    /// future re-introduction of per-name art doesn't quietly regress it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("SomeVendorExtensionIcon")]
    public void UnknownOrAbsentName_StillDrawsTheCard(string? name)
    {
        using var actual = RenderWithExcise(WriteTemp(StickyNotePdf(name)));
        using var note = RenderWithExcise(WriteTemp(StickyNotePdf("Note")));

        MaskDifference(InkMask(actual), InkMask(note)).Should().BeLessThan(20,
            "/Name no longer selects anything drawn (#1794) — an unrecognised or absent " +
            "value must render identically to any other, never blank");
    }

    /// <summary>
    /// #1794: two notes with DIFFERENT /Contents, same /Name (irrelevant now)
    /// and same /Rect must render DIFFERENTLY — the text on the card is what
    /// now carries the annotation's visible meaning, where /Name used to.
    /// This is the direct successor to #1071's "the icons differ" property,
    /// now asked of /Contents instead of /Name.
    /// </summary>
    [Fact]
    public void DifferentContents_RenderDifferently()
    {
        using var a = RenderWithExcise(WriteTemp(StickyNotePdf("Note", contents: "Alpha review")));
        using var b = RenderWithExcise(WriteTemp(StickyNotePdf("Note", contents: "Zulu escalation")));

        MaskDifference(InkMask(a), InkMask(b)).Should().BeGreaterThan(20,
            "different /Contents text on an otherwise-identical card must be visually " +
            "distinguishable — a card that reads the same regardless of its text would " +
            "carry no more information than the old undifferentiated icon did (#1071)");
    }

    /// <summary>
    /// #1794: a REAL card-sized /Rect (bigger than the old fixed icon) draws
    /// at that actual size rather than being clamped down to the ~17pt icon
    /// box — the whole point of the post-it rework. A rect genuinely smaller
    /// than the icon (the next test) still clamps UP to it.
    /// </summary>
    [Fact]
    public void RealCardSizedRect_RendersAtItsActualSize_NotClampedToTheOldIconSize()
    {
        using var small = RenderWithExcise(WriteTemp(StickyNotePdf("Note", rect: "[20 20 44 44]")));
        using var big = RenderWithExcise(
            WriteTemp(StickyNotePdf("Note", rect: "[20 20 220 170]", pageSize: 260)));

        InkPixels(big).Should().BeGreaterThan(InkPixels(small) * 4,
            "a ~200x150pt card must ink far more of the page than a ~24x24pt one — if the " +
            "renderer still clamped every /Text to the old icon size, the two would be the " +
            "same size and roughly the same ink count");
    }

    /// <summary>
    /// The one thing here an external renderer CAN settle: that a card
    /// belongs at a degenerate /Rect at all. Producers write /Rect
    /// [50 110 50 110] and mean it (§12.5.6.4 — the icon is a fixed size
    /// regardless of the rect for THEIR reading of it), so excise's own
    /// renderer normalises up to the same fixed floor before its zero-area
    /// guard, same as before #1794.
    /// </summary>
    [Fact]
    public void DegenerateRect_StillDrawsACard_AsMutoolDoes()
    {
        var path = WriteTemp(StickyNotePdf("Note", rect: "[40 40 40 40]"));

        InkPixels(RenderWithExcise(path)).Should().BeGreaterThan(200,
            "a zero-area /Rect is normal for /Text, not malformed");

        if (MutoolReferenceRenderer.IsAvailable)
        {
            using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            reference.Should().NotBeNull();
            InkPixels(reference!).Should().BeGreaterThan(50,
                "mutool also places a marker at a degenerate /Rect — that agreement is " +
                "what makes normalising the rect correct rather than an invention");
        }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] StickyNotePdf(
        string? iconName, string rect = "[20 20 44 44]", string contents = "note", int? pageSize = null)
    {
        var size = pageSize ?? PageSize;
        var annot = $"<< /Type /Annot /Subtype /Text /F 4 /Rect {rect} " +
                    $"/Contents ({contents}) /C [1 0.85 0.2]" +
                    (iconName == null ? "" : $" /Name /{iconName}") + " >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {size} {size}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
        });
    }

    private static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    /// <summary>
    /// The INK of the glyph only — dark pixels. The note body is /C yellow and
    /// identical for every name, so counting all non-white pixels would make
    /// every pair look alike and this gate would have passed on the bug.
    /// </summary>
    private static bool[,] InkMask(SKBitmap bmp)
    {
        var mask = new bool[bmp.Width, bmp.Height];
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                mask[x, y] = c.Red < 100 && c.Green < 100 && c.Blue < 100;
            }
        return mask;
    }

    private static int MaskDifference(bool[,] a, bool[,] b)
    {
        int w = Math.Min(a.GetLength(0), b.GetLength(0));
        int h = Math.Min(a.GetLength(1), b.GetLength(1));
        int diff = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a[x, y] != b[x, y]) diff++;
        return diff;
    }

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-icon-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
