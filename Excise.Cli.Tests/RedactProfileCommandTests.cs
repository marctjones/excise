using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Cli.Commands;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Cli.Tests;

/// <summary>
/// #1586 — the CLI's <c>--profile</c>, at the handler level.
/// </summary>
/// <remarks>
/// <para><b>Why these are not just Core tests.</b> Twice while wiring #1586 a
/// front end nearly undid the profile it had asked for: the handler rebuilt the
/// carrier policy from <c>CarrierScrubPolicy.Default</c>, which downgrades
/// Maximum's RemoveWhole to Strip on every carrier — most of what that profile
/// is. The engine was correct both times. Only a test at the FRONT END can see
/// that class of defect, which is #896's lesson restated: a guarantee
/// re-established by every front end holds until someone writes a new
/// one.</para>
/// </remarks>
public class RedactProfileCommandTests
{
    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"excise_profile_{Guid.NewGuid():N}{extension}");

    /// <summary>
    /// The shared <c>outline-title</c> carrier trap: a one-page PDF whose
    /// bookmark title holds the token, and which draws it on the page too.
    /// Reused rather than rebuilt — one fixture, four consumers.
    /// </summary>
    private static (byte[] Bytes, string Token) BookmarkTrap()
    {
        var trap = Excise.TestSupport.CarrierTrapFixtures.Get("outline-title");
        return (trap.Build(true), trap.Token);
    }

    [Fact]
    public void StandardIsTheDefault_AndSaysSo()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        var (bytes, token) = BookmarkTrap();
        File.WriteAllBytes(inputPath, bytes);
        try
        {
            var result = RedactCommandHandler.Execute(new RedactCommandRequest(
                inputPath, outputPath, token),
                cancellationToken: TestContext.Current.CancellationToken);

            result.AccessibilityRemoved.Should().BeFalse(
                "Standard keeps the accessibility and navigation carriers");
            using var redacted = PdfDocument.Open(outputPath);
            redacted.Catalog.GetOptional("Outlines").Should().NotBeNull(
                "bookmarks are navigation and Standard keeps them");
        }
        finally { File.Delete(inputPath); File.Delete(outputPath); }
    }

    [Fact]
    public void Maximum_StripsBookmarks_AndReportsTheAccessibilityLoss()
    {
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        var (bytes, token) = BookmarkTrap();
        File.WriteAllBytes(inputPath, bytes);
        try
        {
            var result = RedactCommandHandler.Execute(new RedactCommandRequest(
                inputPath, outputPath, token,
                Profile: RedactionProfile.Maximum),
                cancellationToken: TestContext.Current.CancellationToken);

            result.AccessibilityRemoved.Should().BeTrue(
                "the CLI must be able to PRINT the warning, so the flag has to reach it");
            result.Removals.Should().Contain(r => r.Feature.Contains("outline"));

            using var redacted = PdfDocument.Open(outputPath);
            redacted.Catalog.GetOptional("Outlines").Should().BeNull();
        }
        finally { File.Delete(inputPath); File.Delete(outputPath); }
    }

    [Fact]
    public void Maximum_KeepsItsRemoveWholeCarrierPolicy_WhenNoCarrierPolicyIsGiven()
    {
        // The regression this exists for: `CarrierPolicy = request.CarrierPolicy
        // ?? CarrierScrubPolicy.Default` silently reverted Maximum to Strip.
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        var (bytes, token) = BookmarkTrap();
        File.WriteAllBytes(inputPath, bytes);
        try
        {
            RedactCommandHandler.Execute(new RedactCommandRequest(
                inputPath, outputPath, token,
                Profile: RedactionProfile.Maximum),
                cancellationToken: TestContext.Current.CancellationToken);

            var saved = File.ReadAllBytes(outputPath);
            Excise.TestSupport.SavedPdfLeakScanner.FindTerm(saved, token)
                .Should().BeEmpty("no carrier keeps the term under Maximum");
            Excise.TestSupport.SavedPdfLeakScanner.AllCarriersText(saved)
                .Should().NotContain("Chapter on",
                    "RemoveWhole drops the whole title, not just the term inside it — a " +
                    "residue like \"Chapter on \" is exactly the #1169 reveal risk");
        }
        finally { File.Delete(inputPath); File.Delete(outputPath); }
    }

    /// <summary>
    /// The CLI's own carrier notes must not accuse a carrier the profile
    /// already REMOVED.
    /// </summary>
    /// <remarks>
    /// <para><c>CliRedactedCopySafetyAdapter.AuditTermRedaction</c> runs
    /// <c>RedactedCopySafetyPolicy.Evaluate</c> over the finished document to
    /// collect what the surgical term policy could not examine (#916/#905). Now
    /// that Standard REMOVES all JavaScript, a note saying it could not be
    /// examined would be false and would push <c>NOT SCRUBBED</c> lines at a
    /// user whose document has no script left — the report equivalent of
    /// crying wolf, which is how a real refusal stops being read.</para>
    /// <para>Measured rather than reasoned: this drives the handler on a live
    /// JavaScript trap and reads the notes back.</para>
    /// </remarks>
    [Fact]
    public void Standard_RemovesTheScript_AndDoesNotThenComplainAboutIt()
    {
        var trap = Excise.TestSupport.CarrierTrapFixtures.Get("field-javascript");
        var inputPath = TempPath(".pdf");
        var outputPath = TempPath(".pdf");
        File.WriteAllBytes(inputPath, trap.Build(true));
        try
        {
            var result = RedactCommandHandler.Execute(new RedactCommandRequest(
                inputPath, outputPath, trap.Token),
                cancellationToken: TestContext.Current.CancellationToken);

            result.Removals.Should().Contain(r => r.Feature.Contains("JavaScript"),
                "Standard removes it, and the CLI prints that");
            result.CarrierNotes.Should().NotContain(
                n => n.Contains("NOT SCRUBBED") && n.Contains("JavaScript"),
                "a carrier that is GONE cannot be unexamined — the note would be false, " +
                "and false refusals are how real ones stop being read");
            Excise.TestSupport.SavedPdfLeakScanner.FindTerm(
                File.ReadAllBytes(outputPath), trap.Token).Should().BeEmpty(
                "and the independent check: the token is nowhere in the saved bytes");
        }
        finally { File.Delete(inputPath); File.Delete(outputPath); }
    }
}
