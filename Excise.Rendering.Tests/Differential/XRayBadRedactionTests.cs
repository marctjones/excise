using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1122 — the Free Law Project's <c>x-ray</c> as an independent oracle for
/// "did this redaction leave the text under a black box".
///
/// <para><b>Why a fourth kind of oracle.</b> Every other oracle here answers
/// "what text is in this file". A fake redaction — a black rectangle painted
/// over intact text — passes all of them, because the text is *supposed* to be
/// extractable from a normal document. The leak is the COMBINATION of readable
/// text and a covering box, and x-ray is the only instrument we have that
/// looks for the combination.</para>
///
/// <para>It is also an independent IMPLEMENTATION of what excise's own
/// <c>HiddenTextDetector</c> claims, which was previously verified only by
/// excise — and invisibly so, because <c>check-redaction-oracles.sh</c> scans
/// <c>*Redaction*Tests.cs</c> and <c>HiddenTextDetectorTests.cs</c> does not
/// match that pattern.</para>
/// </summary>
public class XRayBadRedactionTests
{
    private const string Secret = "Louise Anne Farrar";

    /// <summary>
    /// A one-page PDF with the secret drawn as text. When
    /// <paramref name="paintBoxOverIt"/> is true it also paints an opaque
    /// black rectangle across the name and removes nothing — the classic fake
    /// redaction, and the shape x-ray exists to catch.
    /// </summary>
    private static byte[] BuildPdf(bool paintBoxOverIt)
    {
        var content = new StringBuilder();
        content.Append("BT /F1 24 Tf 72 700 Td (Name: ").Append(Secret).Append(") Tj ET\n");
        if (paintBoxOverIt)
            content.Append("0 0 0 rg\n137 694 232 26 re f\n");
        var body = content.ToString();

        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(body)} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string WriteTemp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-xray-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>
    /// THE POSITIVE CONTROL, and the reason the other test means anything.
    /// If x-ray cannot detect a redaction we deliberately faked, then "x-ray
    /// found nothing in excise's output" is not evidence of anything.
    /// </summary>
    [Fact]
    public void XRay_DetectsADeliberatelyFakeRedaction()
    {
        Assert.SkipUnless(XRayBadRedactionDetector.IsAvailable,
            "needs a python that can import xray [requires: tool:xray]");

        var path = WriteTemp(BuildPdf(paintBoxOverIt: true));
        try
        {
            var found = XRayBadRedactionDetector.Inspect(path);

            found.Should().NotBeNull("x-ray reported available but then refused to run");
            found!.Should().NotBeEmpty(
                "a black rectangle painted over text that was never removed is the " +
                "textbook bad redaction; an oracle that misses it cannot vouch for ours");
            string.Concat(found.Select(f => f.Text)).Should().Contain("Farrar",
                "x-ray should read back the text hiding under the box");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The property that matters: excise removes glyphs, so there is nothing
    /// left under the marker to recover.
    /// </summary>
    [Fact]
    public void ExciseRedaction_LeavesNoBadRedactionForXRayToFind()
    {
        Assert.SkipUnless(XRayBadRedactionDetector.IsAvailable,
            "needs a python that can import xray [requires: tool:xray]");

        var source = WriteTemp(BuildPdf(paintBoxOverIt: false));
        var output = Path.Combine(Path.GetTempPath(), $"excise-xray-out-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(source))
            {
                doc.RedactText(Secret).VerifiedRemovals.Should().Be(1,
                    "guard: the fixture must actually be redacted, or the clean verdict below is vacuous");
                doc.Save(output);
            }

            var found = XRayBadRedactionDetector.Inspect(output);

            // NOT `found.Should().BeEmpty()` on a null — null means x-ray did
            // not answer, and reading that as "clean" is precisely how an
            // absent oracle becomes a passing gate.
            found.Should().NotBeNull("x-ray must actually run for this to be a verdict");
            found!.Should().BeEmpty(
                "excise removes the glyphs, so the black marker covers nothing recoverable");
        }
        finally { File.Delete(source); if (File.Exists(output)) File.Delete(output); }
    }

    /// <summary>
    /// excise ships its own bad-redaction detector (<c>audit</c>). Neither it
    /// nor x-ray is ground truth, so this asserts AGREEMENT on the two cases
    /// where the answer is not in doubt, rather than electing a winner.
    /// </summary>
    [Fact]
    public void ExciseAudit_AgreesWithXRay_OnTheFakeAndTheRealRedaction()
    {
        Assert.SkipUnless(XRayBadRedactionDetector.IsAvailable,
            "needs a python that can import xray [requires: tool:xray]");

        var fake = WriteTemp(BuildPdf(paintBoxOverIt: true));
        try
        {
            var xrayFound = XRayBadRedactionDetector.Inspect(fake);
            xrayFound.Should().NotBeNull();

            using var doc = PdfDocument.Open(fake);
            var exciseFound = HiddenTextDetector.Scan(doc).Count;

            (xrayFound!.Count > 0).Should().Be(exciseFound > 0,
                $"x-ray found {xrayFound.Count} bad redaction(s) and excise audit found " +
                $"{exciseFound}. A disagreement here is a real finding in one of the two " +
                "detectors — investigate rather than adjusting this assertion");
        }
        finally { File.Delete(fake); }
    }
}
