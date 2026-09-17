using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Kind = Excise.Core.Text.Segmentation.CarrierTextRecovery.CarrierFindingKind;
using Oracle = Excise.TestSupport.CarrierTrapFixtures.Oracle;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// No self-oracle for the unredact certain channel: every carrier excise says
/// holds a trap's token is checked with tools that are not excise.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>qpdf</b> — <c>--json --decode-level=all --json-stream-data=inline</c>
///     dumps every object, strings and decoded stream data. The token must be in
///     it, and the fixture must pass <c>qpdf --check</c> (a trap nothing else can
///     parse measures nothing).</item>
///   <item><b>mutool</b> — <c>show -b</c> of the object excise names (following one
///     level of references) must contain the token: the location claim, not just
///     the presence claim.</item>
///   <item>A nested PDF is extracted with <c>qpdf --show-attachment</c> and read
///     with mutool. A prior revision is the file prefix ending at the first
///     <c>%%EOF</c>, read with mutool — and the CURRENT file, read by both tools,
///     must not show the token, which is what makes that carrier a leak.</item>
/// </list>
/// </remarks>
public sealed class CarrierTrapIndependentCorroborationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"carrier-traps-{Guid.NewGuid():N}");

    public CarrierTrapIndependentCorroborationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Traps() => new(CarrierTrapFixtures.All.Select(t => t.Id));

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [MemberData(nameof(Traps))]
    public void ExciseFinding_IsCorroboratedByQpdfAndMutool(string id)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "qpdf and mutool are both required to corroborate carrier findings (brew install qpdf mupdf-tools)");

        var trap = CarrierTrapFixtures.Get(id);
        var bytes = trap.Build(false);
        var path = Write(id + ".pdf", bytes);

        var check = QpdfReferenceTool.Check(path);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"{id}: the trap must be a valid PDF to qpdf: {check.Value.Output}");

        CarrierTextRecovery.CarrierText[] findings;
        using (var doc = PdfDocument.Open(bytes))
            findings = CarrierTextRecovery.Scan(doc, TestContext.Current.CancellationToken).ToArray();

        switch (trap.Oracle)
        {
            case Oracle.QpdfDump:
            {
                var dump = QpdfDump(path);
                dump.Should().Contain(trap.Token, $"{id}: qpdf must independently see the token in the file");

                var hit = findings.First(f => f.Kind == Kind.Text && f.Text.Contains(trap.Token, StringComparison.Ordinal)
                                              && f.Carrier.Contains(trap.ExpectedCarrier, StringComparison.Ordinal));
                if (hit.ObjectNumber > 0)
                    MutoolShowWithReferences(path, hit.ObjectNumber).Should().Contain(trap.Token,
                        $"{id}: excise says object {hit.ObjectNumber} holds the token; mutool must agree");
                break;
            }
            case Oracle.NestedPdfText:
            {
                var inner = RunTool("qpdf", $"--show-attachment={trap.AttachmentName}", path);
                inner.Should().NotBeNull($"{id}: qpdf must extract the attachment");
                var innerPath = Write(id + "-inner.pdf", inner!);
                (MutoolTextExtractor.ExtractPage(innerPath, 1) ?? "").Should().Contain(trap.Token,
                    $"{id}: mutool must read the token in the attached PDF");
                findings.Should().Contain(f => f.Text.Contains(trap.Token, StringComparison.Ordinal));
                break;
            }
            case Oracle.PriorRevisionText:
            {
                var text = Encoding.Latin1.GetString(bytes);
                var firstEof = text.IndexOf("%%EOF", StringComparison.Ordinal) + "%%EOF\n".Length;
                var prefixPath = Write(id + "-rev1.pdf", bytes[..firstEof]);
                (MutoolTextExtractor.ExtractPage(prefixPath, 1) ?? "").Should().Contain(trap.Token,
                    $"{id}: mutool must read the token in revision 1");
                (MutoolTextExtractor.ExtractPage(path, 1) ?? "").Should().NotContain(trap.Token,
                    $"{id}: the current revision must not show it — otherwise this is not a leak");
                QpdfDump(path).Should().NotContain(trap.Token,
                    $"{id}: qpdf reads the current revision only; the old object is invisible to an object dump");
                findings.Should().Contain(f => f.Carrier.StartsWith("prior revision", StringComparison.Ordinal)
                                               && f.Text.Contains(trap.Token, StringComparison.Ordinal));
                break;
            }
            case Oracle.PresenceOnly:
            {
                var hit = findings.First(f => f.Kind == Kind.Presence
                                              && f.Carrier.Contains(trap.ExpectedCarrier, StringComparison.Ordinal));
                if (hit.Carrier.StartsWith("attachment", StringComparison.Ordinal))
                    Encoding.UTF8.GetString(RunTool("qpdf", "--list-attachments", path) ?? Array.Empty<byte>())
                        .Should().Contain("blob.bin", $"{id}: qpdf must list the attachment excise reports as present");
                else
                    QpdfDump(path).Should().Contain("/Thumb", $"{id}: qpdf must see the thumbnail excise reports");
                break;
            }
        }
    }

    [Fact]
    public void CleanControl_IsValidAndHasTheRevisionTheScanMustIgnore()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is required (brew install qpdf)");
        var path = Write("clean.pdf", CarrierTrapFixtures.Clean());
        QpdfReferenceTool.Check(path)!.Value.Success.Should().BeTrue();
        // qpdf reports the current /Info — the revision the scan compared against exists.
        QpdfDump(path).Should().Contain("D:20260917010000Z");
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        CarrierTextRecovery.Scan(doc, TestContext.Current.CancellationToken).Should().BeEmpty();
    }

    // ── Tool helpers ────────────────────────────────────────────────────

    /// <summary>Every string and decoded stream body in qpdf's JSON v2 object dump.</summary>
    internal static string QpdfDump(string path)
    {
        var json = RunTool("qpdf", "--json", "--json-key=qpdf", "--decode-level=all", "--json-stream-data=inline", path)
                   ?? throw new InvalidOperationException("qpdf --json failed on " + path);
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        Collect(doc.RootElement, null, sb);
        return sb.ToString();
    }

    private static void Collect(JsonElement e, string? propertyName, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Collect(p.Value, p.Name, sb);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, null, sb);
                break;
            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                if (propertyName == "data")
                {
                    try
                    {
                        var raw = Convert.FromBase64String(s);
                        sb.Append(Encoding.Latin1.GetString(raw)).Append('\n');
                    }
                    catch (FormatException) { sb.Append(s).Append('\n'); }
                }
                else if (s.StartsWith("u:", StringComparison.Ordinal))
                {
                    sb.Append(s, 2, s.Length - 2).Append('\n');
                }
                else if (s.StartsWith("b:", StringComparison.Ordinal))
                {
                    try { sb.Append(Encoding.Latin1.GetString(Convert.FromHexString(s[2..]))).Append('\n'); }
                    catch (FormatException) { sb.Append(s).Append('\n'); }
                }
                else
                {
                    sb.Append(s).Append('\n');
                }
                break;
        }
    }

    /// <summary><c>mutool show -b</c> of an object and of the objects it references directly.</summary>
    private static string MutoolShowWithReferences(string path, int objectNumber)
    {
        var first = Encoding.Latin1.GetString(RunTool("mutool", "show", "-b", path, objectNumber.ToString()) ?? Array.Empty<byte>());
        var sb = new StringBuilder(first);
        foreach (Match m in Regex.Matches(first, @"(\d+) 0 R"))
            sb.Append('\n').Append(Encoding.Latin1.GetString(
                RunTool("mutool", "show", "-b", path, m.Groups[1].Value) ?? Array.Empty<byte>()));
        return sb.ToString();
    }

    private static byte[]? RunTool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        copy.GetAwaiter().GetResult();
        _ = err.GetAwaiter().GetResult();
        // qpdf exits 3 for warnings; the output is still the file's content.
        return p.ExitCode is 0 or 3 ? ms.ToArray() : null;
    }
}
