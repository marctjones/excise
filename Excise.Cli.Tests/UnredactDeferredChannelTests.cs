using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1690 — <c>excise unredact</c> focuses on TEXT recovery, and the image
/// channels are DEFERRED: present, tested, opt-in, not graded.
///
/// <para><b>Every case here is TWO-SIDED</b>, because each half alone passes on
/// a fixture that never carried the subject. "Off → skipped" passes on a
/// document with no image in it; "on → found" passes whether or not the flag
/// does anything. Only the pair says the flag is the thing that changed.</para>
///
/// <para>⚠️ The failure this file exists to prevent is specific and already
/// happened: the engine was changed to declare the deferred channels skipped
/// with a message naming <c>--include-deferred</c>, and the CLI had no such
/// option. Two channels were unreachable while the report told the user how to
/// reach them. A test that only asserted the skip would have passed.</para>
/// </summary>
public class UnredactDeferredChannelTests
{
    // ── the flag exists and is what turns the channel on ────────────────────

    [Fact]
    public void ByDefault_TheImageChannelIsSkipped_WithAReasonNamingTheIssueAndTheFlag()
    {
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var recovery = Run(path, includeDeferred: false).Report!.Recovery!;

            recovery.ChannelsRun.Should().NotContain(RecoveryScanner.Channels.CoveredImage);
            recovery.ChannelsSkipped.Should().ContainKey(RecoveryScanner.Channels.CoveredImage);

            var reason = recovery.ChannelsSkipped[RecoveryScanner.Channels.CoveredImage];
            reason.Should().Contain("#1690", "a skip with no attribution cannot be looked up");
            reason.Should().Contain(RecoveryChannelTiers.OptInFlag,
                "the reason must name the flag that brings the channel back, and that flag must exist");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithTheFlag_TheImageChannelRunsAndReportsWhatSurvivesUnderTheBox()
    {
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var recovery = Run(path, includeDeferred: true).Report!.Recovery!;

            recovery.ChannelsRun.Should().Contain(RecoveryScanner.Channels.CoveredImage);
            recovery.ChannelsSkipped.Should().NotContainKey(RecoveryScanner.Channels.CoveredImage);
            AllFindings(recovery).Should().Contain(f => f.Channel == RecoveryScanner.Channels.CoveredImage,
                "deferred is not deleted — the channel still works, it just has to be asked for");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheVectorHalfIsEmittedEitherWay_TheSplitFiltersTheOUTPUT_NotTheWALK()
    {
        // One walk finds image and vector alike; only what is EMITTED is
        // filtered. Splitting CoveredContentRecovery in two would have left a
        // second scanner to keep in step, and the vector half — which is Tier 1
        // — would have been the thing that silently stopped running.
        var path = WriteFixture(VectorUnderBox());
        try
        {
            foreach (var deferred in new[] { false, true })
            {
                var recovery = Run(path, includeDeferred: deferred).Report!.Recovery!;
                recovery.ChannelsRun.Should().Contain(RecoveryScanner.Channels.CoveredVector,
                    $"the vector half is Tier 1 and unconditional (includeDeferred: {deferred})");
                AllFindings(recovery).Should().Contain(
                    f => f.Channel == RecoveryScanner.Channels.CoveredVector,
                    $"includeDeferred: {deferred}");
            }
        }
        finally { File.Delete(path); }
    }

    // ── the report says what it does not cover ──────────────────────────────

    [Fact]
    public void ADefaultRun_DeclaresTheBlindSpot_InTheNarrowWording()
    {
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var report = Run(path, includeDeferred: false).Report!;

            report.Limitations.Should().NotBeNull();
            var text = string.Join("\n", report.Limitations!);
            text.Should().Contain("#1690");
            text.Should().Contain("PIXELS",
                "the hole is a mark over pixels with no text layer beneath it");

            // ⚠️ NOT "blind to scanned documents". A scanned page whose
            // invisible OCR text layer survives under the box is recovered by
            // the Tier 1 hidden-text channel; claiming otherwise would overstate
            // the hole, which is the same error as understating it.
            text.Should().NotContain("blind to scanned",
                "the broad claim is false — text-render-mode-3 still recovers a surviving OCR layer");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithEveryDeferredChannelRun_ThereIsNoBlindSpotToDeclare()
    {
        // The other side of the case above, and the one a fixed sentence could
        // not have got right: a report that ran everything must not print a
        // limitation it does not have.
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var report = Run(path, includeDeferred: true, useOcr: true).Report;

            // --ocr needs tesseract; when it is absent the handler fails with
            // exit 2 and there is no report to inspect. The assertion below is
            // about a report, so say so rather than passing vacuously.
            Assert.SkipWhen(report == null, "--ocr needs tesseract on PATH");
            report!.Limitations.Should().BeNull(
                "with every deferred channel run there is nothing deferred to declare");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithOcrAlone_TheLimitationNamesTheIMAGEChannels_AndNotOcr()
    {
        // The case that killed the const string. It said "the image and OCR
        // channels did not run", which is FALSE here: OCR ran. A report
        // claiming a blind spot it does not have is the same species of error
        // as one claiming coverage it does not have.
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var outcome = Run(path, includeDeferred: false, useOcr: true);
            Assert.SkipWhen(outcome.Report == null, "--ocr needs tesseract on PATH");

            var text = string.Join("\n", outcome.Report!.Limitations!);
            text.Should().Contain(RecoveryScanner.Channels.CoveredImage);
            text.Should().Contain(RecoveryScanner.Channels.ImageLayer);
            text.Should().NotContain(RecoveryScanner.Channels.OcrDifferential,
                "the OCR differential DID run — naming it as absent would be a lie about coverage");
        }
        finally { File.Delete(path); }
    }

    // ── the headline is the TEXT score ──────────────────────────────────────

    [Fact]
    public void ADeferredFindingIsLISTEDButDoesNotMoveTheHeadline()
    {
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var off = Run(path, includeDeferred: false).Report!;
            var on = Run(path, includeDeferred: true).Report!;

            // Listed: the finding count rises, because the report genuinely has
            // one more row in it and the lists below must add up.
            on.Quantification.Findings.Should().BeGreaterThan(off.Quantification.Findings,
                "the deferred finding is reported, not suppressed");

            // Not graded: the text-recovery score is unmoved. An image reported
            // present-only recovered no text, and letting the opt-in flag
            // inflate the score would make `--include-deferred` look like a
            // better result on the same document.
            on.Quantification.Recovered.Should().Be(off.Quantification.Recovered);
            on.Quantification.FullyRecoverable.Should().Be(off.Quantification.FullyRecoverable);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EveryModelFindingCarriesItsTier()
    {
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var recovery = Run(path, includeDeferred: true).Report!.Recovery!;
            var findings = AllFindings(recovery).ToList();

            findings.Should().NotBeEmpty();
            findings.Should().OnlyContain(f => f.Tier == "text" || f.Tier == "deferred");
            findings.Where(f => f.Channel == RecoveryScanner.Channels.CoveredImage)
                .Should().OnlyContain(f => f.Tier == "deferred");
        }
        finally { File.Delete(path); }
    }

    // ── coverage is declared, never implied ─────────────────────────────────

    [Fact]
    public void ResidueMode_DeclaresEVERYOtherChannelSkipped_NotAHandPickedSix()
    {
        // This list used to be hand-written and named six of fourteen, so a
        // residue-only report declared six skips and silently omitted the rest
        // — a report over one channel reading like one over seven. It is
        // derived now, so the assertion is over the whole vocabulary.
        var dictionary = Path.Combine(Path.GetTempPath(), $"excise-dict-{Guid.NewGuid():N}.txt");
        File.WriteAllText(dictionary, "MANAFORT\n");
        var path = WriteFixture(ImageUnderBox());
        try
        {
            var input = new UnredactCommandInput(
                path, "residue", dictionary, Tolerance: 0.5, MaxCandidates: 200,
                UseOcr: false, NoCorroboration: true);
            var recovery = UnredactCommandHandler
                .Execute(input, TestContext.Current.CancellationToken).Report!.Recovery!;

            foreach (var channel in RecoveryScanner.Channels.All.Where(
                         c => c != RecoveryScanner.Channels.Residue))
            {
                recovery.ChannelsSkipped.Should().ContainKey(channel,
                    "a channel that did not run must SAY it did not run");
            }
        }
        finally { File.Delete(path); File.Delete(dictionary); }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static UnredactCommandOutcome Run(string path, bool includeDeferred, bool useOcr = false) =>
        UnredactCommandHandler.Execute(
            new UnredactCommandInput(
                path, "certain", DictionaryPath: null, Tolerance: 0.5, MaxCandidates: 200,
                UseOcr: useOcr, NoCorroboration: false, IncludeDeferred: includeDeferred),
            TestContext.Current.CancellationToken);

    private static IEnumerable<UnredactModelFinding> AllFindings(UnredactRecoveryModel recovery) =>
        recovery.Linked.Concat(recovery.Unlinked).Concat(recovery.DocumentLevel);

    /// <summary>A 2x2 grey image, fully covered by an opaque black box.</summary>
    private static (string Content, string? ExtraObject, string? Resources) ImageUnderBox() =>
        ("q 120 0 0 120 72 600 cm /Im0 Do Q\n" +
         "q 0 0 0 rg 72 600 120 120 re f Q\n",
         "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray " +
         "/BitsPerComponent 8 /Length 4 >>\nstream\n\x00\x40\x80\xFF\nendstream",
         "/XObject << /Im0 6 0 R >>");

    /// <summary>A filled vector path under an opaque black box — the Tier 1 half.</summary>
    private static (string Content, string? ExtraObject, string? Resources) VectorUnderBox() =>
        ("q 0 0 1 rg 80 610 100 100 re f Q\n" +
         "q 0 0 0 rg 72 600 120 120 re f Q\n",
         null, null);

    private static string WriteFixture((string Content, string? ExtraObject, string? Resources) fixture)
    {
        var content = fixture.Content;
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 5 0 R >> {fixture.Resources} >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (fixture.ExtraObject != null) objs.Add(fixture.ExtraObject);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objs.Count];
        for (var i = 0; i < objs.Count; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        var path = Path.Combine(Path.GetTempPath(), $"excise-unredact-tier-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
        return path;
    }
}
