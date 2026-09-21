using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Excise.Cli.Tests;

/// <summary>
/// The hand-written one-page PDFs the <c>unredact</c> CLI tests run on.
///
/// <para><b>Shared on purpose (#1707).</b> These bodies were private to
/// <see cref="UnredactDeferredChannelTests"/>. The exit-status gate needs the
/// SAME image-under-a-box document the channel gate uses — a second copy could
/// drift into a fixture where the leak is shaped differently, and then the two
/// files would be asserting different things about a document each believed was
/// the other's.</para>
///
/// <para>Structurally valid, not merely excise-parseable: the same builder's
/// output is checked with <c>qpdf --check</c> in
/// <c>RecoveryOracleTests.Fixtures_AreStructurallyValidPdfs</c>.</para>
/// </summary>
internal static class UnredactFixtures
{
    internal readonly record struct Page(string Content, string? ExtraObject, string? Resources);

    /// <summary>
    /// A 2x2 grey image drawn at 120x120, fully covered by an opaque black box.
    /// The image XObject is intact — the commonest viewer-based "redaction",
    /// and the document #1707 was filed over.
    /// </summary>
    internal static Page ImageUnderBox() => new(
        "q 120 0 0 120 72 600 cm /Im0 Do Q\n" +
        "q 0 0 0 rg 72 600 120 120 re f Q\n",
        "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray " +
        "/BitsPerComponent 8 /Length 4 >>\nstream\n\x00\x40\x80\xFF\nendstream",
        "/XObject << /Im0 6 0 R >>");

    /// <summary>Nothing hidden and nothing redacted: the all-clear branch.</summary>
    internal static Page EmptyPage() => new(
        "BT /F1 14 Tf 72 700 Td (Nothing to see here) Tj ET\n", null, null);

    /// <summary>A filled vector path under an opaque black box — the Tier 1 half.</summary>
    internal static Page VectorUnderBox() => new(
        "q 0 0 1 rg 80 610 100 100 re f Q\n" +
        "q 0 0 0 rg 72 600 120 120 re f Q\n",
        null, null);

    /// <summary>
    /// Text under an opaque black box: the Tier 1 recovery that must keep
    /// exiting 3. Present here so the exit-status gate can show that the new
    /// mark-linked codes did not displace the text one.
    /// </summary>
    internal static Page TextUnderBox(string secret) => new(
        $"BT /F1 14 Tf 72 700 Td ({secret}) Tj ET\n" +
        "q 0 0 0 rg 68 694 120 24 re f Q\n",
        null, null);

    internal static byte[] Build(Page fixture)
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

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    internal static string Write(Page fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-unredact-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Build(fixture));
        return path;
    }
}
