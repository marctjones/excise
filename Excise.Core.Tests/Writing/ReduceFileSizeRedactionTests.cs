using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Writing;
using Excise.TestSupport;
using Xunit;
using static Excise.Core.Tests.Writing.OptimizerFixtures;

namespace Excise.Core.Tests.Writing;

/// <summary>
/// #1550 — Reduce File Size must never undo a redaction. Named to sit inside
/// the <c>FullyQualifiedName~Redaction</c> gate.
/// </summary>
/// <remarks>
/// Every verdict comes from <see cref="SavedPdfLeakScanner"/> (which inflates
/// every stream with the BCL, not excise's filters) or from mutool — never
/// from excise reading its own output.
/// </remarks>
public sealed class ReduceFileSizeRedactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-1550-redaction-{Guid.NewGuid():N}");

    public ReduceFileSizeRedactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData(PdfOptimizationPreset.Lossless)]
    [InlineData(PdfOptimizationPreset.High)]
    [InlineData(PdfOptimizationPreset.Standard)]
    [InlineData(PdfOptimizationPreset.Screen)]
    public void RedactedDocument_StaysRedactedThroughEveryPreset(PdfOptimizationPreset preset)
    {
        byte[] redacted;
        using (var doc = PdfDocument.Open(UncompressedTextPageWithThumbnail()))
        {
            doc.SetTitle($"Statement of {Term}");
            doc.RedactText(Term, drawBlackRect: true).VerifiedRemovals.Should().Be(12);
            redacted = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(redacted, Term).Should().BeEmpty("precondition: the redaction itself is clean");

        var output = Path.Combine(_dir, $"reduced-{preset}.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(
            redacted, output, PdfOptimizationOptions.ForPreset(preset),
            cancellationToken: TestContext.Current.CancellationToken);
        var reduced = File.ReadAllBytes(output);

        SavedPdfLeakScanner.FindTerm(reduced, Term).Should().BeEmpty(
            "Reduce File Size must not bring back anything the redaction removed");

        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not on PATH — the saved-bytes scan above still ran");
        var text = MutoolTextOracle.ExtractAllPages(reduced);
        text.Should().NotContain(Term);
        text.Should().Contain("the applicant", "the unredacted text must survive the optimizer");
    }

    /// <summary>
    /// The optimizer works on what a SAVE writes, not on the source file's
    /// object table. An unreachable object in the source that still carries
    /// the term — a prior revision's leftover — must not come back.
    /// </summary>
    /// <remarks>
    /// The first assertion is the instrument check: the scanner does see the
    /// orphan in the source bytes, so the clean result is not a blind scanner.
    /// </remarks>
    [Fact]
    public void UnreachableSourceObjectCarryingATerm_DoesNotReappearInTheReducedCopy()
    {
        var source = TextPageWithOrphanCarryingTerm();
        SavedPdfLeakScanner.FindTerm(source, $"orphaned copy of {Term}").Should().NotBeEmpty(
            "instrument check: the orphan is really in the source file");

        byte[] saved;
        using (var doc = PdfDocument.Open(source))
            saved = doc.SaveToBytes();

        var output = Path.Combine(_dir, "orphan.pdf");
        PdfDocumentOptimizer.SaveOptimizedCopy(
            saved, output, PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Lossless),
            cancellationToken: TestContext.Current.CancellationToken);

        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), "orphaned copy").Should().BeEmpty();
    }
}
