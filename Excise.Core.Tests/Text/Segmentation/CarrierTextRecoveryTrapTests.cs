using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;
using Kind = Excise.Core.Text.Segmentation.CarrierTextRecovery.CarrierFindingKind;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// The unredact CERTAIN channel against one synthetic trap per carrier
/// (<see cref="CarrierTrapFixtures"/>): form values and appearances, XFA,
/// attachments (including a nested PDF), actions, metadata, orphan objects and
/// a superseded revision. Each must be read back under its own carrier label;
/// a benign document with tool metadata and a harmless incremental update must
/// yield nothing at all.
/// </summary>
/// <remarks>
/// These pin excise reading its own fixtures. Whether the text is REALLY where
/// excise says is checked against qpdf and mutool in
/// <c>CarrierTrapIndependentCorroborationTests</c> (Excise.Rendering.Tests).
/// </remarks>
public class CarrierTextRecoveryTrapTests
{
    public static TheoryData<string> TextTraps() =>
        new(CarrierTrapFixtures.All.Where(t => t.Oracle != CarrierTrapFixtures.Oracle.PresenceOnly).Select(t => t.Id));

    public static TheoryData<string> PresenceTraps() =>
        new(CarrierTrapFixtures.All.Where(t => t.Oracle == CarrierTrapFixtures.Oracle.PresenceOnly).Select(t => t.Id));

    private static CarrierTextRecovery.CarrierText[] Scan(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return CarrierTextRecovery.Scan(doc, TestContext.Current.CancellationToken).ToArray();
    }

    [Theory]
    [MemberData(nameof(TextTraps))]
    public void TextTrap_IsRecoveredUnderItsCarrier(string id)
    {
        var trap = CarrierTrapFixtures.Get(id);
        var findings = Scan(trap.Build(false));

        findings.Should().Contain(
            f => f.Kind == Kind.Text
                 && f.Carrier.Contains(trap.ExpectedCarrier, StringComparison.Ordinal)
                 && f.Text.Contains(trap.Token, StringComparison.Ordinal),
            $"{id}: the token is physically present in '{trap.ExpectedCarrier}' and must be read back. " +
            $"Found: {string.Join(" | ", findings.Select(f => $"[{f.Kind} {f.Carrier}] {f.Text}"))}");
    }

    [Theory]
    [MemberData(nameof(PresenceTraps))]
    public void PresenceTrap_IsReportedAsPresentNotAsText(string id)
    {
        var trap = CarrierTrapFixtures.Get(id);
        var findings = Scan(trap.Build(false));

        findings.Should().Contain(
            f => f.Kind == Kind.Presence && f.Carrier.Contains(trap.ExpectedCarrier, StringComparison.Ordinal),
            $"{id}: undecoded content must be reported, not dropped");
        findings.Where(f => f.Kind == Kind.Text).Should().NotContain(
            f => f.Text.Contains(trap.Token, StringComparison.Ordinal),
            "presence is not a recovery; the scan does not claim text it did not decode");
    }

    [Fact]
    public void CleanDocument_WithToolMetadataAndHarmlessUpdate_YieldsNothing()
    {
        var findings = Scan(CarrierTrapFixtures.Clean());

        findings.Should().BeEmpty(
            "tool-written /Info and XMP, and an update that only touched /ModDate, are not leaks; " +
            $"found: {string.Join(" | ", findings.Select(f => $"[{f.Kind} {f.Carrier}] {f.Text}"))}");
    }

    [Fact]
    public void Clean_PreconditionHoldsAnEarlierRevision()
    {
        // The false-positive test is only meaningful if the revision scanner
        // actually had something to compare. Pre-registered: 2 %%EOF markers.
        var text = System.Text.Encoding.Latin1.GetString(CarrierTrapFixtures.Clean());
        text.Split("%%EOF").Length.Should().Be(3);
    }

    [Theory]
    [InlineData("orphan-dictionary", 6)]
    [InlineData("orphan-content-stream", 6)]
    [InlineData("acroform-v", 6)]
    [InlineData("widget-appearance", 7)]
    [InlineData("attachment-desc", 6)]
    [InlineData("xfa-datasets", 6)]
    public void Findings_NameTheObjectThatHoldsTheCarrier(string id, int objectNumber)
    {
        var trap = CarrierTrapFixtures.Get(id);
        var hit = Scan(trap.Build(false)).First(f => f.Text.Contains(trap.Token, StringComparison.Ordinal));
        hit.ObjectNumber.Should().Be(objectNumber, $"{id}: an auditor must be able to go to the object");
    }

    [Fact]
    public void PageLevelCarriers_NameThePage()
    {
        var trap = CarrierTrapFixtures.Get("annotation-subj");
        Scan(trap.Build(false)).Single(f => f.Text.Contains(trap.Token, StringComparison.Ordinal))
            .PageNumber.Should().Be(1);
    }

    [Fact]
    public void NestedPdf_WithinDepthLimit_IsRecovered()
    {
        var findings = Scan(CarrierTrapFixtures.NestedChain(CarrierTextRecovery.MaxAttachmentDepth, "DEEPNESTTOKEN"));
        findings.Should().Contain(f => f.Kind == Kind.Text && f.Text.Contains("DEEPNESTTOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedPdf_BeyondDepthLimit_IsReportedNotSilentlyDropped()
    {
        var findings = Scan(CarrierTrapFixtures.NestedChain(CarrierTextRecovery.MaxAttachmentDepth + 1, "TOODEEPTOKEN"));
        findings.Should().NotContain(f => f.Text.Contains("TOODEEPTOKEN", StringComparison.Ordinal));
        findings.Should().Contain(f => f.Kind == Kind.Presence && f.Text.Contains("depth limit", StringComparison.Ordinal));
    }

    [Fact]
    public void LongCarrierText_IsTruncatedAndMarked()
    {
        var longTitle = "LONGTITLETOKEN" + new string('x', CarrierTextRecovery.MaxTextChars * 2);
        var hit = Scan(CarrierTrapFixtures.WithInfo($"<< /Title ({longTitle}) >>")).Single();
        hit.Text.Should().StartWith("LONGTITLETOKEN");
        hit.Text.Length.Should().BeLessThan(CarrierTextRecovery.MaxTextChars + 64);
        hit.Text.Should().Contain("truncated");
    }

    [Fact]
    public void ToolWrittenInfoKeys_AreNotReported_ButAuthorKeysAre()
    {
        var findings = Scan(CarrierTrapFixtures.WithInfo(
            "<< /Producer (P) /Creator (C) /CreationDate (D:2026) /ModDate (D:2026) /Author (AUTHORTOKEN) >>"));
        findings.Should().ContainSingle().Which.Carrier.Should().Be("/Info /Author");
    }

    [Fact]
    public void Scan_HonoursCancellation()
    {
        using var doc = PdfDocument.Open(CarrierTrapFixtures.Get("acroform-v").Build(false));
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var act = () => CarrierTextRecovery.Scan(doc, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void PriorRevision_VisibleInCurrentRevision_IsNotReportedAsLost()
    {
        // The differential reports only what the current revision no longer
        // shows: with the token still on the page, revision 1 adds nothing.
        var trap = CarrierTrapFixtures.Get("prior-revision");
        Scan(trap.Build(true)).Should().NotContain(
            f => f.Carrier.StartsWith("prior revision", StringComparison.Ordinal)
                 && f.Text.Contains(trap.Token, StringComparison.Ordinal));
    }

    /// <summary>
    /// The scan walks every object, every revision and every attachment. It is
    /// an audit for <c>excise unredact</c> and must never run on open, render,
    /// or redaction: the only production caller is the unredact handler.
    /// </summary>
    [Fact]
    public void CarrierScan_HasNoProductionCallerOutsideUnredact()
    {
        var root = TestRepoLayout.LocalCheckoutRoot;
        Assert.SkipWhen(root is null, "repository root not found from the test binary");
        var callers = Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Full: p, Rel: Path.GetRelativePath(root!, p).Replace('\\', '/')))
            .Where(p => !p.Rel.Split('/').Any(seg => seg is "bin" or "obj" or ".claude" or "tools"
                                                     || seg.EndsWith(".Tests", StringComparison.Ordinal))
                        && !Path.GetFileName(p.Full).StartsWith("CarrierTextRecovery", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p.Full).Contains("CarrierTextRecovery.Scan", StringComparison.Ordinal))
            .Select(p => p.Rel)
            .ToList();

        callers.Should().Equal(new[] { "Excise.Cli/Commands/UnredactCommandHandler.cs" },
            "the carrier scan is an unredact audit, not something open/render/redact may pay for");
    }
}
