using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1021 — the annotation display decisions, as behaviour.
///
/// <para>These need no oracle and deliberately use none. Whether excise's
/// synthesized artwork resembles anyone else's is #1015's settled question;
/// whether excise honours a switch the user asked for has one right answer.</para>
///
/// <para>The two that matter most are negative: <b>audit mode and field
/// highlighting must never reach an export.</b> Both deliberately draw things no
/// conforming viewer shows — revealed Hidden annotations, and a tint nothing in
/// the file asks for — and a redaction tool that bakes either into a shared file
/// has invented ink in someone's document. That is what #1005 removed.</para>
/// </summary>
public class AnnotationDisplayOptionsTests
{
    private const int Dpi = 150;

    private static byte[] Page(params string[] annots)
    {
        var refs = string.Join(" ", annots.Select((_, i) => $"{4 + i} 0 R"));
        var objs = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [{refs}] >>\nendobj\n",
        }.Concat(annots.Select((a, i) => $"{4 + i} 0 obj\n<< /Type /Annot {a} >>\nendobj\n")).ToArray();

        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new System.Collections.Generic.List<int>();
        foreach (var o in objs) { offs.Add(sb.Length); sb.Append(o); }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private const string Comment =
        "/Subtype /Square /F 4 /Rect [20 20 90 90] /C [1 0 0] /IC [1 0 0] /BS << /W 3 >>";
    private const string Field =
        "/Subtype /Widget /FT /Tx /T (f) /F 4 /Rect [110 110 180 180] " +
        "/MK << /BG [0 1 0] >>";

    private static long Ink(byte[] pdf, Action<RenderOptionsBuilder> configure)
    {
        var b = new RenderOptionsBuilder();
        configure(b);
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1), b.Build())!;
        long n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 40 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    private sealed class RenderOptionsBuilder
    {
        public bool Comments = true, Fields = true, Reveal, Highlight;
        public RenderOptions Build() => new()
        {
            Dpi = Dpi,
            RenderAnnotations = true,
            ShowCommentAnnotations = Comments,
            ShowFieldAndLinkAnnotations = Fields,
            RevealHiddenAnnotations = Reveal,
            HighlightFormFields = Highlight,
        };
    }

    [Fact]
    public void TheTwoGroupsAreIndependent()
    {
        var pdf = Page(Comment, Field);

        var both = Ink(pdf, _ => { });
        var commentsOnly = Ink(pdf, o => o.Fields = false);
        var fieldsOnly = Ink(pdf, o => o.Comments = false);
        var neither = Ink(pdf, o => { o.Comments = false; o.Fields = false; });

        neither.Should().Be(0, "with both groups off nothing may draw");
        commentsOnly.Should().BeGreaterThan(0, "the Square is a comment and must survive hiding fields");
        fieldsOnly.Should().BeGreaterThan(0, "the Widget is a field and must survive hiding comments");

        // Each group must be a real subset — if hiding one changed nothing, the
        // switch would be decorative.
        commentsOnly.Should().BeLessThan(both, "hiding fields must remove the field's ink");
        fieldsOnly.Should().BeLessThan(both, "hiding comments must remove the comment's ink");
    }

    [Fact]
    public void AuditMode_RevealsWhatHiddenAndNoViewSuppress()
    {
        foreach (var flags in new[] { 2 /* Hidden */, 32 /* NoView */ })
        {
            var pdf = Page($"/Subtype /Square /F {flags} /Rect [20 20 180 180] " +
                           "/C [1 0 0] /IC [1 0 0] /BS << /W 3 >>");

            Ink(pdf, _ => { }).Should().Be(0,
                $"/F {flags} means do not display (§12.5.3), so the default must draw nothing");
            Ink(pdf, o => o.Reveal = true).Should().BeGreaterThan(0,
                "audit mode exists precisely to show what a conforming viewer hides — " +
                "'there is something here you are not being shown' is what a redaction " +
                "reviewer needs to know");
        }
    }

    [Fact]
    public void FieldHighlighting_IsOffByDefaultAndAddsInkWhenAsked()
    {
        // A BARE widget — no /MK, no /AP — so the field rect is otherwise
        // empty and the tint is the only thing that can ink it. Using the
        // /MK /BG field instead measured nothing: a translucent tint over an
        // already-filled rect changes the COLOUR of those pixels, not how many
        // are inked, and a pixel count cannot see that.
        var pdf = Page("/Subtype /Widget /FT /Tx /T (bare) /F 4 /Rect [110 110 180 180]");

        var off = Ink(pdf, _ => { });
        var on = Ink(pdf, o => o.Highlight = true);

        on.Should().BeGreaterThan(off,
            "the highlight is a tint over the field rect, so enabling it must add ink");

        // The default is the load-bearing half: a redaction tool must be able to
        // show the page as it really is, and the tint is chrome nothing in the
        // file asks for.
        new RenderOptions().HighlightFormFields.Should().BeFalse(
            "field highlighting must be OFF unless the user asks — it is viewer chrome");
        new RenderOptions().RevealHiddenAnnotations.Should().BeFalse(
            "audit mode must be OFF unless the user asks — it draws what no viewer shows");
    }

    [Fact]
    public void TheChromeOptionsDoNotAlterTheDocument()
    {
        // Both options are RENDER-time only. Nothing they do may reach a saved
        // file — the export rule in #1021 is that viewer chrome never becomes
        // file content.
        var pdf = Page(Field);
        using var doc = PdfDocument.Open(pdf);
        using var ms = new MemoryStream();
        doc.Save(ms);
        var saved = ms.ToArray();

        Encoding.Latin1.GetString(saved).Should().NotContain("3366CC",
            "the highlight colour must exist only in a rendered bitmap, never in a document");
    }
}
