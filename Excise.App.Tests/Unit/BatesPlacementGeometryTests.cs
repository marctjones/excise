using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Editing;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1636 Bates numbering, placement on the page the reader SEES. BatesNumberingWorkflowTests pins the
/// four corners on an upright page; this holds all six positions on rotated pages, a media box whose
/// origin is not (0,0), and a crop box smaller than the media box. The oracle is mutool: its
/// structured-text page box and line boxes are in the displayed orientation, so a stamp drawn in
/// unrotated user space (the classic fault) lands on the wrong edge or off the page.
/// </summary>
public sealed class BatesPlacementGeometryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-bates-geometry-{Guid.NewGuid():N}");

    public BatesPlacementGeometryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    public static IEnumerable<object[]> Cases()
    {
        // mediabox, cropbox (or null), rotate
        var geometries = new (string Media, string? Crop, int Rotate)[]
        {
            ("0 0 612 792", null, 0),
            ("0 0 612 792", null, 90),
            ("0 0 612 792", null, 180),
            ("0 0 612 792", null, 270),
            ("100 200 712 992", null, 0),
            ("100 200 712 992", null, 90),
            ("0 0 612 792", "50 60 500 700", 0),
            ("0 0 612 792", "50 60 500 700", 270),
        };
        foreach (var (media, crop, rotate) in geometries)
            foreach (var position in Enum.GetValues<BatesPosition>())
                yield return new object[] { media, crop ?? "", rotate, position };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheStampLandsWhereTheReaderExpectsIt(string media, string crop, int rotate, BatesPosition position)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = Path.Combine(_tempDir, "in.pdf");
        File.WriteAllBytes(source, Fixture(media, crop.Length == 0 ? null : crop, rotate));
        var output = Path.Combine(_tempDir, "out.pdf");
        using (var doc = PdfDocument.Open(source))
        {
            new BatesNumberingService().ApplyBatesNumbers(doc, new BatesOptions { Prefix = "BATESGEO", Position = position });
            doc.Save(output);
        }

        var (pageWidth, pageHeight, box) = FindLine(output, "BATESGEO000001");
        var context = $"{position} on {media} crop[{crop}] rotate {rotate}: stamp {box}, page {pageWidth}x{pageHeight}";

        // On the visible page, with room for the glyphs.
        box.X0.Should().BeGreaterThanOrEqualTo(-0.5, context);
        box.Y0.Should().BeGreaterThanOrEqualTo(-0.5, context);
        box.X1.Should().BeLessThanOrEqualTo(pageWidth + 0.5, context);
        box.Y1.Should().BeLessThanOrEqualTo(pageHeight + 0.5, context);

        var centerX = (box.X0 + box.X1) / 2;
        var centerY = (box.Y0 + box.Y1) / 2;
        var wantTop = position is BatesPosition.TopLeft or BatesPosition.TopCenter or BatesPosition.TopRight;
        (centerY < pageHeight / 2).Should().Be(wantTop, "vertical edge: " + context);
        switch (position)
        {
            case BatesPosition.TopLeft or BatesPosition.BottomLeft:
                centerX.Should().BeLessThan(pageWidth / 3, "left third: " + context); break;
            case BatesPosition.TopRight or BatesPosition.BottomRight:
                centerX.Should().BeGreaterThan(pageWidth * 2 / 3, "right third: " + context); break;
            default:
                centerX.Should().BeInRange(pageWidth / 3, pageWidth * 2 / 3, "middle third: " + context); break;
        }
        // A margin, not the middle of the page: within 80pt of its edge.
        var edgeGap = wantTop ? box.Y0 : pageHeight - box.Y1;
        edgeGap.Should().BeLessThan(80, "close to its edge: " + context);
    }

    private readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public override string ToString() => $"[{X0:F1},{Y0:F1}-{X1:F1},{Y1:F1}]";
    }

    private static (double Width, double Height, Box Line) FindLine(string pdf, string text)
    {
        var psi = new ProcessStartInfo("mutool", $"draw -q -F stext -o - \"{pdf}\" 1")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        // Drain BOTH pipes concurrently and bound the wait (#1068, #1516): a synchronous
        // ReadToEnd() on a stuck child blocks forever and xUnit's timeout cannot abort it.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("mutool did not exit within 30s; killed it.");
        }
        var xml = stdout.GetAwaiter().GetResult();
        stderr.GetAwaiter().GetResult();

        var page = XDocument.Parse(xml).Descendants("page").Single();
        var width = double.Parse(page.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(page.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var line in page.Descendants("line"))
        {
            var lineText = string.Concat(line.Descendants("char").Select(c => c.Attribute("c")!.Value));
            if (!lineText.Contains(text, StringComparison.Ordinal)) continue;
            var b = line.Attribute("bbox")!.Value.Split(' ')
                .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return (width, height, new Box(b[0], b[1], b[2], b[3]));
        }
        throw new InvalidOperationException($"mutool found no text line containing '{text}' on page 1 of the stamped copy; lines: " +
            string.Join(" | ", page.Descendants("line").Select(l => string.Concat(l.Descendants("char").Select(c => c.Attribute("c")!.Value)))));
    }

    private static byte[] Fixture(string media, string? crop, int rotate)
    {
        var body = "BT /F1 12 Tf 150 400 Td (body text) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [{media}] " +
                (crop != null ? $"/CropBox [{crop}] " : "") +
                (rotate != 0 ? $"/Rotate {rotate} " : "") +
                "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
