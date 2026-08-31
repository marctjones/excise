using System;
using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Coverage for <see cref="SignatureAppearanceAuthoring"/> (#623) — baking a
/// visible <c>/AP /N</c> Form XObject onto a signature widget. Exercises the
/// visible/invisible decision, the appearance-stream structure, width-based
/// truncation, BBox clipping of overflow lines, and PDF-literal escaping.
/// </summary>
public class SignatureAppearanceAuthoringTests
{
    private static PdfDictionary WidgetWithRect(double l, double b, double r, double t)
    {
        var widget = new PdfDictionary();
        widget["Rect"] = PdfArray.FromRectangle(l, b, r, t);
        return widget;
    }

    private static PdfStream ResolveAppearance(PdfDocument doc, PdfDictionary widget)
    {
        widget.GetOptional("AP").Should().NotBeNull("a visible signature must carry /AP");
        var ap = (PdfDictionary)widget["AP"];
        return (PdfStream)doc.Resolve(ap["N"]);
    }

    private static string ContentOf(PdfStream s) => Encoding.ASCII.GetString(s.DecodedData);

    [Fact]
    public void ApplyVisibleAppearance_VisibleRect_BakesFormXObjectAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 300, 660);

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget,
            new[] { "Digitally signed by EXCISE", "Date: 2026-07-27", "Reason: Approval" });

        var ap = ResolveAppearance(doc, widget);
        ap.GetName("Type").Should().Be("XObject");
        ap.GetName("Subtype").Should().Be("Form");
        ap.GetInt("FormType").Should().Be(1);

        var bbox = (PdfArray)ap["BBox"];
        bbox.Count.Should().Be(4);
        // BBox is 0 0 w h (200 x 60).
        ((PdfReal)bbox[2]).Value.Should().BeApproximately(200, 0.001);
        ((PdfReal)bbox[3]).Value.Should().BeApproximately(60, 0.001);

        var resources = (PdfDictionary)ap["Resources"];
        var fonts = (PdfDictionary)resources["Font"];
        var helv = (PdfDictionary)doc.Resolve(fonts["Helv"]);
        helv.GetName("BaseFont").Should().Be("Helvetica");
        helv.GetName("Encoding").Should().Be("WinAnsiEncoding");

        var content = ContentOf(ap);
        content.Should().Contain("re S", "the border is stroked");
        content.Should().Contain("BT").And.Contain("ET");
        content.Should().Contain("/Helv").And.Contain("Tf");
        content.Should().Contain("Tj");
        content.Should().Contain("Digitally signed by EXCISE");
    }

    [Fact]
    public void ApplyVisibleAppearance_UsesCanonicalPdfNumberPrecision()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 200.12345678, 660);

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, new[] { "signed" });

        ContentOf(ResolveAppearance(doc, widget))
            .Should().Contain("99.123457 59 re S");
    }

    [Fact]
    public void ApplyVisibleAppearance_EmptyLines_LeavesWidgetUntouched()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 300, 660);

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget, Array.Empty<string>());

        widget.GetOptional("AP").Should().BeNull("no lines means nothing to draw");
    }

    [Fact]
    public void ApplyVisibleAppearance_MissingRect_AddsNoAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = new PdfDictionary(); // no /Rect

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget, new[] { "signed" });

        widget.GetOptional("AP").Should().BeNull();
    }

    [Fact]
    public void ApplyVisibleAppearance_RectNotAnArray_AddsNoAppearance()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = new PdfDictionary();
        widget["Rect"] = new PdfString("not-an-array");

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget, new[] { "signed" });

        widget.GetOptional("AP").Should().BeNull();
    }

    [Theory]
    [InlineData(100, 600, 100, 660)] // zero width
    [InlineData(100, 600, 300, 600)] // zero height
    public void ApplyVisibleAppearance_ZeroAreaRect_IsInvisibleSignature(
        double l, double b, double r, double t)
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(l, b, r, t);

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget, new[] { "signed" });

        widget.GetOptional("AP").Should().BeNull(
            "a zero-area /Rect is the deliberate invisible-signature case");
    }

    [Fact]
    public void ApplyVisibleAppearance_LongLine_IsTruncatedWithEllipsis()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 160, 660); // narrow, 60pt wide

        var longLine = "This is a very long signature attestation line that cannot fit";
        SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, new[] { longLine });

        var content = ContentOf(ResolveAppearance(doc, widget));
        content.Should().Contain("...", "an over-wide line is truncated with an ellipsis");
        content.Should().NotContain(longLine, "the full over-wide line must not be drawn verbatim");
    }

    [Fact]
    public void ApplyVisibleAppearance_ExtremelyNarrowBox_DrawsUntruncatedLine()
    {
        using var doc = PdfDocument.CreateNew();
        // Width 5pt: available text width clamps to 1pt, so not even one
        // character plus the ellipsis fits — the truncator returns the text
        // unchanged rather than an ellipsis-only string.
        var widget = WidgetWithRect(100, 600, 105, 620);

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, new[] { "AB" });

        var content = ContentOf(ResolveAppearance(doc, widget));
        content.Should().Contain("(AB)");
    }

    [Fact]
    public void ApplyVisibleAppearance_MoreLinesThanFit_StopsAtBBoxBottom()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 300, 630); // short: 30pt tall

        var lines = new List<string>();
        for (int i = 0; i < 30; i++) lines.Add($"Line{i:D2}");

        SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, lines);

        var content = ContentOf(ResolveAppearance(doc, widget));
        content.Should().Contain("Line00", "the first line is always drawn");
        content.Should().NotContain("Line29",
            "lines whose baseline falls below the BBox are not emitted");
    }

    [Fact]
    public void ApplyVisibleAppearance_EscapesLiteralStringMetacharactersAndNonAscii()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 400, 660);

        // '(' ')' '\' each need escaping; a control char and a non-ASCII
        // letter both render as '?' in the printable-ASCII MVP.
        SignatureAppearanceAuthoring.ApplyVisibleAppearance(
            doc, widget, new[] { "a(b)c\\d\u0001eéf" });

        var content = ContentOf(ResolveAppearance(doc, widget));
        content.Should().Contain(@"a\(b\)c\\d?e?f");
    }

    [Fact]
    public void ApplyVisibleAppearance_NullArguments_Throw()
    {
        using var doc = PdfDocument.CreateNew();
        var widget = WidgetWithRect(100, 600, 300, 660);
        var lines = new[] { "signed" };

        ((Action)(() => SignatureAppearanceAuthoring.ApplyVisibleAppearance(null!, widget, lines)))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, null!, lines)))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => SignatureAppearanceAuthoring.ApplyVisibleAppearance(doc, widget, null!)))
            .Should().Throw<ArgumentNullException>();
    }
}
