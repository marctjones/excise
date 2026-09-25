using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1830 item 1: a font selected by an ExtGState <c>/Font</c> entry (§8.4.5
/// Table 58), never by <c>Tf</c>, must still be the font a rebuilt run's
/// surviving glyphs draw in. The rebuild read a private text-state copy with no
/// <c>gs</c> case, so it drew them under the last <c>Tf</c>, or an invented
/// <c>/F1 12</c> when there was none.
///
/// <para>The runs are <c>'</c> operators because the operand split refuses
/// them and the run is rebuilt; a well-formed <c>Tj</c>/<c>TJ</c> is split in
/// place and never reaches the rebuild. <c>/F1</c> is Helvetica and the
/// ExtGState font is Courier, so mutool, an independent renderer, sees a wrong
/// font as different glyph advances inside each surviving word.</para>
/// </summary>
public sealed class ExtGStateFontRedactionTests : IDisposable
{
    private const string Body =
        "/GS0 gs\n" +
        "BT 20 TL 72 720 Td (KEEP SECRET TAIL) ' (ALPHA HIDDEN OMEGA) ' ET\n";

    private static readonly string[] Survivors = { "KEEP", "TAIL", "ALPHA", "OMEGA" };

    private readonly List<string> _temp = new();

    /// <summary>
    /// With a <c>Tf</c> line before the <c>gs</c> (the stale-font case), and
    /// with no <c>Tf</c> anywhere (the invented-font case).
    /// </summary>
    [Theory]
    [InlineData("BT /F1 10 Tf 72 760 Td (HEADER) Tj ET\n")]
    [InlineData("")]
    public void RebuiltRun_DrawsInTheExtGStateFont(string prologue)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var original = Fixture(prologue);
        var redacted = Redact(original, "SECRET");

        AssertRemoved(redacted, "SECRET");
        AssertSurvivorsUnchanged(original, redacted);
    }

    [Theory]
    [InlineData("BT /F1 10 Tf 72 760 Td (HEADER) Tj ET\n")]
    [InlineData("")]
    public void SequentialRedaction_DrawsInTheExtGStateFont(string prologue)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Pass 2 re-parses pass 1's output, rebuilt block included, and
        // rebuilds the second run.
        var original = Fixture(prologue);
        var redacted = Redact(Redact(original, "SECRET"), "HIDDEN");

        AssertRemoved(redacted, "SECRET");
        AssertRemoved(redacted, "HIDDEN");
        AssertSurvivorsUnchanged(original, redacted);
    }

    private static byte[] Fixture(string prologue) => ContentStreamFixture.Build(
        prologue + Body,
        extraObjects: "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>\nendobj\n",
        extraResources: "/ExtGState << /GS0 << /Font [ 6 0 R 18 ] >> >>");

    private static byte[] Redact(byte[] pdf, string term)
    {
        using var doc = PdfDocument.Open(pdf);
        doc.RedactText(term, RedactionOptions.Default);
        return doc.SaveToBytes();
    }

    private void AssertRemoved(byte[] redacted, string term)
    {
        SavedPdfLeakScanner.FindTerm(redacted, term).Should().BeEmpty();
        var text = MutoolTextExtractor.ExtractPage(WriteTemp(redacted), 1);
        text.Should().NotBeNull();
        text.Should().NotContain(term);
        foreach (var word in Survivors)
            text.Should().Contain(word);
    }

    private void AssertSurvivorsUnchanged(byte[] original, byte[] redacted)
    {
        // Sensitivity: mutool draws the ExtGState font (Courier's flat 600/1000
        // at 18pt), so a survivor in any other font cannot match below.
        Advances(original, "TAIL").Should().AllSatisfy(a => a.Should().BeApproximately(10.8, 0.01));

        foreach (var word in Survivors)
            Advances(redacted, word).Should().Equal(Advances(original, word),
                (after, before) => Math.Abs(after - before) < 0.05,
                $"'{word}' must be drawn in the font it was drawn in before the redaction");
    }

    /// <summary>Glyph-to-glyph advances inside <paramref name="word"/>, as mutool lays it out.</summary>
    private IReadOnlyList<double> Advances(byte[] pdf, string word)
    {
        var glyphs = MutoolGlyphPositions.ExtractPage(WriteTemp(pdf), 1);
        glyphs.Should().NotBeNull();
        foreach (var line in glyphs!.GroupBy(g => Math.Round(g.Y, 1)))
        {
            var ordered = line.OrderBy(g => g.X).ToList();
            var at = string.Concat(ordered.Select(g => g.Char)).IndexOf(word, StringComparison.Ordinal);
            if (at >= 0)
                return Enumerable.Range(at, word.Length - 1)
                    .Select(i => ordered[i + 1].X - ordered[i].X).ToList();
        }
        throw new InvalidOperationException($"mutool found no '{word}' on the page");
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-gsfont-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
