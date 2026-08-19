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
/// Insert marker were indistinguishable. The icon is the only thing a /Text
/// annotation draws, so that was the whole of its visible meaning.
///
/// <para><b>Why this gate compares excise to excise, which the house rule
/// normally forbids.</b> The no-self-oracle rule exists for properties where
/// there is an external truth to check against — does the text survive
/// redaction, does the page render correctly. Here there is none: §12.5.6.4
/// names the icons and says NOTHING about how they are drawn, and the three
/// reference renderers each invent their own (mutool black strokes, pdftocairo
/// a grey-green fill, Ghostscript grey plus black). Pixel-matching any one of
/// them would be asserting a house style we did not choose.</para>
///
/// <para>So the property asserted is the one that actually carries the
/// meaning: <b>the icons differ from each other</b>, and an unknown name falls
/// back to the spec default. That is checkable without an oracle because it is
/// a claim about excise's own output being internally distinguishable — not a
/// claim about correctness that excise is refereeing for itself.</para>
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
    public void EveryIconName_DrawsAMarker(string name)
    {
        using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(name)));

        InkPixels(bmp).Should().BeGreaterThan(200,
            $"/Name /{name} must draw a visible marker — a /Text annotation draws " +
            "nothing else, so an invisible one is an annotation the reviewer never sees " +
            "while its /Contents still ships to the recipient");
    }

    /// <summary>
    /// The defect itself, and the reason this file is not one big smoke test:
    /// before #1071 all 21 of these pairs were IDENTICAL.
    /// </summary>
    [Fact]
    public void EveryPairOfIcons_IsVisuallyDistinct()
    {
        var glyphs = new Dictionary<string, bool[,]>();
        foreach (var name in IconNames)
        {
            using var bmp = RenderWithExcise(WriteTemp(StickyNotePdf(name)));
            glyphs[name] = InkMask(bmp);
        }

        var tooSimilar = new List<string>();
        for (int i = 0; i < IconNames.Length; i++)
            for (int j = i + 1; j < IconNames.Length; j++)
            {
                int diff = MaskDifference(glyphs[IconNames[i]], glyphs[IconNames[j]]);
                if (diff < 150) tooSimilar.Add($"{IconNames[i]} vs {IconNames[j]} ({diff}px)");
            }

        tooSimilar.Should().BeEmpty(
            "each /Name must be recognisable AS that name; two icons a reader cannot tell " +
            "apart carry no more information than the single glyph they replaced");
    }

    /// <summary>
    /// §12.5.6.4: /Note is the default. Both an absent /Name and a name from a
    /// later extension must land there rather than drawing nothing — an
    /// annotation that vanishes because its icon name was unfamiliar is the
    /// worst of the available failures.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("SomeVendorExtensionIcon")]
    public void UnknownOrAbsentName_FallsBackToTheNoteIcon(string? name)
    {
        using var actual = RenderWithExcise(WriteTemp(StickyNotePdf(name)));
        using var note = RenderWithExcise(WriteTemp(StickyNotePdf("Note")));

        MaskDifference(InkMask(actual), InkMask(note)).Should().BeLessThan(20,
            "an unrecognised or absent /Name is /Note per Table 172 — silently drawing " +
            "nothing would hide the annotation entirely");
    }

    /// <summary>
    /// The one thing here an external renderer CAN settle: that a marker belongs
    /// at a degenerate /Rect at all. Producers write /Rect [50 110 50 110] and
    /// mean it (§12.5.6.4 — the icon is a fixed size regardless of the rect), so
    /// the renderer normalises to ~17pt before its zero-area guard.
    /// </summary>
    [Fact]
    public void DegenerateRect_StillDrawsAMarker_AsMutoolDoes()
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

    private static byte[] StickyNotePdf(string? iconName, string rect = "[20 20 44 44]")
    {
        var annot = $"<< /Type /Annot /Subtype /Text /F 4 /Rect {rect} " +
                    "/Contents (note) /C [1 0.85 0.2]" +
                    (iconName == null ? "" : $" /Name /{iconName}") + " >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
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
