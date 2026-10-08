using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Excise.TestSupport;

/// <summary>Where a fixture's /Rotate is written.</summary>
internal enum RotateLocation
{
    Absent,
    Leaf,
    Parent,
    ParentAndLeaf,
}

/// <summary>
/// One row of <c>tests/corpora/rotation-fixtures.tsv</c> (#1982): page geometry
/// recorded from qpdf, Poppler and MuPDF, never from excise.
/// </summary>
internal sealed record RotationFixture(
    string Id,
    bool IsSynthetic,
    string? RelativePath,
    string Source,
    string? Sha256,
    int Page,
    RotateLocation RotateAt,
    int EffectiveRotation,
    double[] MediaBox,
    double[] CropBox,
    double UserUnit,
    string? Target,
    string Limitations)
{
    /// <summary>Displayed page width in points, from the recorded CropBox and rotation.</summary>
    public double VisualWidth => EffectiveRotation is 90 or 270 ? CropBox[3] - CropBox[1] : CropBox[2] - CropBox[0];

    /// <summary>Displayed page height in points.</summary>
    public double VisualHeight => EffectiveRotation is 90 or 270 ? CropBox[2] - CropBox[0] : CropBox[3] - CropBox[1];
}

/// <summary>
/// The shared rotation fixture set for #1980's interaction issues (#1982, #1983,
/// #1984, #1985, #1986). Corpus rows resolve only through <see cref="TestRepoLayout"/>;
/// synthetic rows are written byte by byte by <see cref="RotationProbes"/>, not by
/// excise's writer, so a writer defect cannot shape its own input.
/// </summary>
internal static class RotationFixtures
{
    public const string ManifestPath = "tests/corpora/rotation-fixtures.tsv";

    private static readonly Lazy<IReadOnlyList<RotationFixture>> Rows = new(Load);

    public static IReadOnlyList<RotationFixture> All => Rows.Value;

    public static RotationFixture Get(string id) =>
        All.FirstOrDefault(f => f.Id == id)
        ?? throw new ArgumentException($"No rotation fixture '{id}' in {ManifestPath}.", nameof(id));

    /// <summary>
    /// Bytes for a fixture, or null with a checkable reason. A corpus file whose
    /// SHA-256 differs from the manifest is a failure, not a skip: the recorded
    /// geometry would describe a different file.
    /// </summary>
    public static byte[]? TryLoad(RotationFixture fixture, out string absenceReason)
    {
        absenceReason = "";
        if (fixture.IsSynthetic)
            return RotationProbes.Build(fixture.Id);

        var path = TestRepoLayout.FindFile(fixture.RelativePath!);
        if (path == null)
        {
            absenceReason = TestRepoLayout.AbsenceReason(
                $"rotation fixture {fixture.Id} (fetch with scripts/download-pdfjs-corpus.sh)",
                fixture.RelativePath!);
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (actual != fixture.Sha256)
            throw new InvalidOperationException(
                $"{path} has SHA-256 {actual}; {ManifestPath} records {fixture.Sha256} for {fixture.Id}. " +
                "Re-fetch the corpus or re-record the row from qpdf/Poppler/MuPDF.");
        return bytes;
    }

    private static IReadOnlyList<RotationFixture> Load()
    {
        var manifest = TestRepoLayout.FindFile(ManifestPath)
            ?? throw new FileNotFoundException(
                TestRepoLayout.AbsenceReason("rotation fixture manifest (tracked)", ManifestPath));
        var rows = new List<RotationFixture>();
        foreach (var line in File.ReadLines(manifest))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var c = line.Split('\t');
            if (c.Length != 13)
                throw new FormatException($"{ManifestPath}: expected 13 columns, got {c.Length}: {line}");
            rows.Add(new RotationFixture(
                Id: c[0],
                IsSynthetic: c[1] == "synthetic",
                RelativePath: c[2].Length == 0 ? null : c[2],
                Source: c[3],
                Sha256: c[4].Length == 0 ? null : c[4],
                Page: int.Parse(c[5], CultureInfo.InvariantCulture),
                RotateAt: c[6] switch
                {
                    "absent" => RotateLocation.Absent,
                    "leaf" => RotateLocation.Leaf,
                    "parent" => RotateLocation.Parent,
                    "parent+leaf" => RotateLocation.ParentAndLeaf,
                    _ => throw new FormatException($"{ManifestPath}: unknown rotate_at '{c[6]}'"),
                },
                EffectiveRotation: int.Parse(c[7], CultureInfo.InvariantCulture),
                MediaBox: ParseBox(c[8]),
                CropBox: ParseBox(c[9]),
                UserUnit: double.Parse(c[10], CultureInfo.InvariantCulture),
                Target: c[11] == "-" ? null : c[11],
                Limitations: c[12]));
        }
        return rows;
    }

    private static double[] ParseBox(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
}

/// <summary>
/// Hand-assembled single-page probe PDFs. Two Helvetica lines, each containing
/// <see cref="Target"/>-adjacent words, so a search for <see cref="RepeatedWord"/>
/// has two results and a drag over <see cref="Target"/> has one exact answer.
/// </summary>
internal static class RotationProbes
{
    public const string Target = "ALPHA";
    public const string RepeatedWord = "PROBE";
    public const string Line1 = "ALPHA PROBE ONE";
    public const string Line2 = "BRAVO PROBE TWO";

    public static byte[] Build(string id) => id switch
    {
        "probe-r0" => Write(parentRotate: null, leafRotate: null, cropBox: null),
        "probe-r90" => Write(null, 90, null),
        "probe-r180" => Write(null, 180, null),
        "probe-r270" => Write(null, 270, null),
        "probe-inherit90" => Write(90, null, null),
        "probe-inherit270-leaf0" => Write(270, 0, null),
        "probe-crop0" => Write(null, null, "90 250 522 742"),
        "probe-crop90" => Write(null, 90, "90 250 522 742"),
        _ => throw new ArgumentException($"No synthetic rotation probe '{id}'.", nameof(id)),
    };

    private static byte[] Write(int? parentRotate, int? leafRotate, string? cropBox)
    {
        var content =
            "BT /F1 18 Tf 1 0 0 1 120 600 Tm (" + Line1 + ") Tj ET\n" +
            "BT /F1 18 Tf 1 0 0 1 120 560 Tm (" + Line2 + ") Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1" +
                (parentRotate is { } pr ? $" /Rotate {pr}" : "") + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                (cropBox != null ? $" /CropBox [{cropBox}]" : "") +
                (leafRotate is { } lr ? $" /Rotate {lr}" : "") +
                " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

internal enum RotationView
{
    SinglePage,
    Continuous,
}

/// <summary>
/// One bounded interaction scenario (#1982): a fixture, the quarter turns applied
/// through the UI rotate command, the view, a zoom (0 = the view's own default fit)
/// and a device pixel ratio.
/// </summary>
internal sealed record RotationScenario(
    string Id,
    string FixtureId,
    int UiQuarterTurns,
    RotationView View,
    double Zoom,
    double Dpr)
{
    public RotationFixture Fixture => RotationFixtures.Get(FixtureId);

    /// <summary>Displayed rotation after the UI turns.</summary>
    public int FinalRotation => (Fixture.EffectiveRotation + 90 * UiQuarterTurns) % 360;

    public override string ToString() => Id;
}

/// <summary>
/// The shared scenario matrix for #1983-#1986. Not a Cartesian product: every
/// final rotation is reached both from a pre-rotated file and through the UI, each
/// view sees every rotation at least once, inherited rotation and the cropped
/// origin each appear in both views, and zoom/DPR vary across rows rather than
/// multiplying them. 18 rows.
/// </summary>
internal static class RotationScenarioTable
{
    public static readonly IReadOnlyList<RotationScenario> All = new[]
    {
        // Single page: pre-rotated files.
        new RotationScenario("s-r0", "probe-r0", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-r90", "probe-r90", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-r180-z150", "probe-r180", 0, RotationView.SinglePage, 1.5, 1),
        new RotationScenario("s-r270", "probe-r270", 0, RotationView.SinglePage, 1.0, 2),
        new RotationScenario("s-inherit90", "probe-inherit90", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-crop0", "probe-crop0", 0, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-crop90-z75", "probe-crop90", 0, RotationView.SinglePage, 0.75, 1),
        new RotationScenario("s-14497", "pdfjs-issue14497", 0, RotationView.SinglePage, 0, 1),
        // Single page: rotated through the UI command.
        new RotationScenario("s-r0-ui90", "probe-r0", 1, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-r0-ui180", "probe-r0", 2, RotationView.SinglePage, 1.0, 2),
        new RotationScenario("s-r0-ui270-z150", "probe-r0", 3, RotationView.SinglePage, 1.5, 1),
        new RotationScenario("s-inherit90-ui180", "probe-inherit90", 1, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-crop0-ui90", "probe-crop0", 1, RotationView.SinglePage, 1.0, 1),
        new RotationScenario("s-14497-ui180", "pdfjs-issue14497", 1, RotationView.SinglePage, 0, 1),
        // Continuous view.
        new RotationScenario("c-r90", "probe-r90", 0, RotationView.Continuous, 1.0, 1),
        new RotationScenario("c-r0-ui270", "probe-r0", 3, RotationView.Continuous, 1.0, 2),
        new RotationScenario("c-crop90-ui180-z150", "probe-crop90", 1, RotationView.Continuous, 1.5, 1),
        new RotationScenario("c-inherit90-ui180", "probe-inherit90", 1, RotationView.Continuous, 1.0, 2),
    };

    public static RotationScenario Get(string id) => All.Single(s => s.Id == id);

    /// <summary>Scenario ids, for <c>[MemberData]</c>.</summary>
    public static IEnumerable<object[]> Ids(Func<RotationScenario, bool>? where = null) =>
        All.Where(where ?? (_ => true)).Select(s => new object[] { s.Id });
}

/// <summary>
/// A region in DISPLAYED page space: points, top-left origin, after /Rotate and
/// relative to the visible (crop) box. Both MuPDF stext and the overlay canvases
/// use this space, so comparing them needs no rotation formula from the test.
/// </summary>
internal readonly record struct VisualRegion(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    public static VisualRegion Union(IEnumerable<VisualRegion> regions)
    {
        var list = regions.ToList();
        return new VisualRegion(list.Min(r => r.Left), list.Min(r => r.Top), list.Max(r => r.Right), list.Max(r => r.Bottom));
    }

    public VisualRegion Inflate(double d) => new(Left - d, Top - d, Right + d, Bottom + d);

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Area of the intersection divided by this region's area.</summary>
    public double FractionCoveredBy(VisualRegion other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        if (w <= 0 || h <= 0 || Width <= 0 || Height <= 0) return 0;
        return w * h / (Width * Height);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Left:F1},{Top:F1} → {Right:F1},{Bottom:F1}]");
}

/// <summary>
/// Glyph regions from MuPDF's structured text (<c>mutool draw -F stext</c>),
/// read RAW: stext already reports the displayed page (rotated, crop-box
/// relative, top-left origin), so nothing here converts coordinates and nothing
/// here asks excise anything. That is what makes it an oracle for overlays.
/// </summary>
internal static class MutoolStextGeometry
{
    private static readonly Lazy<string?> Executable = new(() => FindOnPath("mutool"));

    public static bool IsAvailable => Executable.Value != null;

    internal sealed record StextChar(string C, VisualRegion Box);

    internal sealed record StextPage(double Width, double Height, IReadOnlyList<IReadOnlyList<StextChar>> Lines)
    {
        /// <summary>Every occurrence of <paramref name="term"/> inside one line, as the union of its char quads.</summary>
        public IReadOnlyList<VisualRegion> Find(string term)
        {
            var hits = new List<VisualRegion>();
            foreach (var line in Lines)
            {
                var text = string.Concat(line.Select(c => c.C));
                for (int at = text.IndexOf(term, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(term, at + 1, StringComparison.Ordinal))
                {
                    // One stext char is one string element only for single-UTF-16 chars;
                    // the probe and fixture targets are ASCII.
                    hits.Add(VisualRegion.Union(line.Skip(at).Take(term.Length).Select(c => c.Box)));
                }
            }
            return hits;
        }
    }

    public static StextPage Read(byte[] pdf, int pageNumber)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-rot-stext-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { return Read(path, pageNumber); }
        finally { File.Delete(path); }
    }

    public static StextPage Read(string pdfPath, int pageNumber)
    {
        var xml = Run(Executable.Value ?? throw new InvalidOperationException("mutool is not on PATH"),
            "draw", "-F", "stext", "-o", "-", pdfPath, pageNumber.ToString(CultureInfo.InvariantCulture));
        // stext writes control characters as &#x14; for unmapped glyphs, which XML 1.0
        // forbids; read them anyway rather than lose the page.
        using var reader = XmlReader.Create(new StringReader(xml[xml.IndexOf("<?xml", StringComparison.Ordinal)..]),
            new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore });
        var doc = XDocument.Load(reader);
        var page = doc.Descendants("page").First();
        var lines = page.Descendants("line").Select(l => (IReadOnlyList<StextChar>)l.Descendants("char")
            .Select(ch =>
            {
                var q = ch.Attribute("quad")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var xs = new[] { q[0], q[2], q[4], q[6] };
                var ys = new[] { q[1], q[3], q[5], q[7] };
                return new StextChar(ch.Attribute("c")?.Value ?? "",
                    new VisualRegion(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
            }).ToList()).ToList();
        return new StextPage(
            double.Parse(page.Attribute("width")!.Value, CultureInfo.InvariantCulture),
            double.Parse(page.Attribute("height")!.Value, CultureInfo.InvariantCulture),
            lines);
    }

    internal static string Run(string exe, params string[] args) => RunTool(exe, args);

    internal static string RunTool(string exe, string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{exe} did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} {string.Join(' ', args)} exceeded 60 s");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    internal static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                     .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }))
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        return null;
    }
}

/// <summary>
/// Poppler's view of page geometry: <c>pdfinfo -box</c> for rotation and boxes,
/// <c>pdftotext -bbox</c> for word regions. A second, independent reader beside
/// MuPDF; where the two differ, the difference is classified by the caller.
/// </summary>
internal static class PopplerGeometry
{
    private static readonly Lazy<string?> PdfInfo = new(() => MutoolStextGeometry.FindOnPath("pdfinfo"));
    private static readonly Lazy<string?> PdfToText = new(() => MutoolStextGeometry.FindOnPath("pdftotext"));

    public static bool IsAvailable => PdfInfo.Value != null && PdfToText.Value != null;

    internal sealed record PageInfo(int Rotation, double[] MediaBox, double[] CropBox);

    public static PageInfo Info(string pdfPath, int pageNumber)
    {
        var p = pageNumber.ToString(CultureInfo.InvariantCulture);
        var output = MutoolStextGeometry.RunTool(PdfInfo.Value!, new[] { "-box", "-f", p, "-l", p, pdfPath });
        int rotation = 0;
        double[]? media = null, crop = null;
        // -f/-l restrict the per-page lines ("Page    1 rot:   90") to this page.
        foreach (var raw in output.Split('\n'))
        {
            var rest = raw.Trim();
            if (!rest.StartsWith("Page ", StringComparison.Ordinal)) continue;
            if (rest.Contains(" rot:", StringComparison.Ordinal))
                rotation = int.Parse(rest[(rest.IndexOf("rot:", StringComparison.Ordinal) + 4)..].Trim(), CultureInfo.InvariantCulture);
            else if (rest.Contains("MediaBox:", StringComparison.Ordinal))
                media = Numbers(rest[(rest.IndexOf("MediaBox:", StringComparison.Ordinal) + 9)..]);
            else if (rest.Contains("CropBox:", StringComparison.Ordinal))
                crop = Numbers(rest[(rest.IndexOf("CropBox:", StringComparison.Ordinal) + 8)..]);
        }
        return new PageInfo(rotation,
            media ?? throw new FormatException($"pdfinfo printed no MediaBox:\n{output}"),
            crop ?? media);
    }

    /// <summary>
    /// Word regions from <c>pdftotext -bbox</c>, plus the page size Poppler
    /// prints. NOTE (classified, #1982): on a /Rotate 90 page Poppler prints the
    /// UNROTATED page size while its word boxes are in the displayed space.
    /// </summary>
    public static (double Width, double Height, IReadOnlyList<(string Word, VisualRegion Box)> Words) Words(
        string pdfPath, int pageNumber, bool cropBox)
    {
        var p = pageNumber.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-bbox", "-f", p, "-l", p };
        if (cropBox) args.Add("-cropbox");
        args.Add(pdfPath);
        args.Add("-");
        var html = MutoolStextGeometry.RunTool(PdfToText.Value!, args.ToArray());
        var doc = XDocument.Parse(html[html.IndexOf("<html", StringComparison.Ordinal)..]);
        XNamespace ns = doc.Root!.Name.Namespace;
        var page = doc.Descendants(ns + "page").First();
        double D(XElement e, string a) => double.Parse(e.Attribute(a)!.Value, CultureInfo.InvariantCulture);
        var words = page.Descendants(ns + "word")
            .Select(w => (w.Value, new VisualRegion(D(w, "xMin"), D(w, "yMin"), D(w, "xMax"), D(w, "yMax"))))
            .ToList();
        return (D(page, "width"), D(page, "height"), words);
    }

    private static double[] Numbers(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
}
