using System.Text;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1635 step 3: Tab and Shift+Tab visit form fields in the order a person reads them, which the
/// overlay documents as "top to bottom, then left to right". It sorted by the exact top edge, so two
/// fields on one printed row whose tops differ by a few hundredths of a point (every real form has
/// them) were visited right to left: Middle name before First name.
/// </summary>
public class FormTabOrderTests
{
    /// <summary>Two text fields on one visual row; the RIGHT one's top edge is 0.04pt higher.</summary>
    private static byte[] TwoFieldsOnOneRow()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (First) /Rect [72 620 250 633.8] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Middle) /Rect [338 620.04 480 633.84] /P 3 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void FieldsOnOneRow_AreVisitedLeftToRight_EvenWhenTheirTopEdgesDifferSlightly()
    {
        using var doc = PdfDocument.Open(TwoFieldsOnOneRow());
        var fields = doc.GetAcroForm()!.Fields;

        var order = FormFieldInputFactory.OrderFormFieldsForTabbing(fields).Select(f => f.FullName);

        order.Should().Equal("First", "Middle");
    }

    [Fact]
    public void ARowBelowIsVisitedAfterTheRowAbove()
    {
        // The reverse control: a field lower on the page is not pulled into the row above.
        var bytes = TwoFieldsOnOneRow();
        var text = Encoding.Latin1.GetString(bytes).Replace("[338 620.04 480 633.84]", "[338 590 480 603.8]");
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text));
        var fields = doc.GetAcroForm()!.Fields;

        FormFieldInputFactory.OrderFormFieldsForTabbing(fields).Select(f => f.FullName)
            .Should().Equal("First", "Middle");
    }

    /// <summary>
    /// The oracle that does not come from the code under test: the form's author. These forms list their
    /// widgets in /Annots in the order they intend people to fill them in. On the passport forms the
    /// exact-top sort matched that order for fewer than a third of adjacent steps.
    /// </summary>
    [Theory]
    [InlineData("state-ds11-passport.pdf", 5)]
    [InlineData("state-ds11-passport.pdf", 6)]
    [InlineData("state-ds82-passport-renewal.pdf", 5)]
    [InlineData("state-ds82-passport-renewal.pdf", 6)]
    public void OnRealForms_TheTabOrderAgreesWithTheOrderTheAuthorListedTheFields(string file, int pageNumber)
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "smoke", file);
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason(file, "test-pdfs/smoke/" + file));

        using var doc = PdfDocument.Open(path!);
        var page = doc.GetPage(pageNumber);
        var annots = (PdfArray)doc.Resolve(page.Dictionary.GetOptional("Annots")!);
        var authorIndex = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < annots.Count; i++)
            if (doc.Resolve(annots[i]) is PdfDictionary widget)
                authorIndex[widget] = i;

        var fields = doc.GetAcroForm()!.Fields
            .Where(f => f.PageNumber == pageNumber && f.WidgetDictionaries.Count > 0 && authorIndex.ContainsKey(f.WidgetDictionaries[0]))
            .ToList();
        fields.Count.Should().BeGreaterThan(10, "the page has a real form on it");

        var ordered = FormFieldInputFactory.OrderFormFieldsForTabbing(fields)
            .Select(f => authorIndex[f.WidgetDictionaries[0]]).ToList();
        var authorOrder = ordered.OrderBy(i => i).ToList();
        var steps = ordered.Zip(ordered.Skip(1)).Count();
        var agreeing = ordered.Zip(ordered.Skip(1))
            .Count(pair => authorOrder.IndexOf(pair.Second) == authorOrder.IndexOf(pair.First) + 1);

        ((double)agreeing / steps).Should().BeGreaterThanOrEqualTo(0.8,
            $"{agreeing} of {steps} adjacent steps follow the author's order");
    }
}
