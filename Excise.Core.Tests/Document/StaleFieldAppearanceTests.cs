using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.TestSupport;
using Xunit;
using F = Excise.TestSupport.StaleAppearanceFixtures;

namespace Excise.Core.Tests.Document;

/// <summary>
/// #2017: changing a field whose widget carries an appearance excise did not
/// author (Acrobat's, on every file opened from disk) must not leave the OLD
/// appearance, which draws the old value, in the saved file. Before the fix
/// <see cref="PdfField.SetValue"/> set NeedAppearances and kept the old
/// <c>/AP</c>: the old value survived inside it, and a reader that ignores the
/// flag drew it.
///
/// <para>Oracle: the saved bytes, every stream inflated by
/// <see cref="SavedPdfLeakScanner"/>, searched for the old value AND for the
/// unique <c>/StaleMarker</c> name on each old appearance stream, because a
/// kerned <c>TJ</c> array never holds the value contiguously (see
/// <see cref="ScannerBlindSpot_KernedTjValue_IsNotFoundByFindTerm"/>). The
/// qpdf, mutool and Poppler twins are
/// <c>Excise.Rendering.Tests.Differential.StaleFieldAppearanceOracleTests</c>.</para>
/// </summary>
public sealed class StaleFieldAppearanceTests
{
    /// <summary>Fields whose old value is in no legitimate carrier after the change (the choice options list it; SharedB keeps it).</summary>
    private static readonly F.Field[] ValueMustVanish = [F.Name, F.Comb, F.Notes, F.Secret, F.Turned, F.Multi];

    public static TheoryData<OldAppearanceText, string, bool> Cases()
    {
        var data = new TheoryData<OldAppearanceText, string, bool>();
        foreach (var form in Enum.GetValues<OldAppearanceText>())
            foreach (var version in new[] { "1.4", "1.7" })
                foreach (var clear in new[] { false, true })
                    data.Add(form, version, clear);
        return data;
    }

    [Fact]
    public void Fixture_CarriesEveryOldAppearance()
    {
        var source = F.Build();
        foreach (var marker in F.RemovedMarkers().Append(F.SharedMarker))
            SavedPdfLeakScanner.FindTerm(source, marker).Should().NotBeEmpty($"the fixture must carry {marker}");
        foreach (var field in ValueMustVanish)
            SavedPdfLeakScanner.FindTerm(source, field.OldValue).Should().NotBeEmpty($"the fixture must carry {field.OldValue}");
    }

    /// <summary>
    /// The blind spot this issue's history warned about: the scanner searches
    /// contiguous bytes and each string operand alone, so a value split across a
    /// kerned TJ array is invisible to it. Pinned so the marker-based assertions
    /// below are known to be load-bearing; tracked by #2034. A hex string is
    /// decoded, so it is found.
    /// </summary>
    [Fact]
    public void ScannerBlindSpot_KernedTjValue_IsNotFoundByFindTerm()
    {
        static byte[] OnlyInAppearance(OldAppearanceText form)
        {
            using var doc = PdfDocument.Open(F.Build(form));
            // Remove the plain /V so the appearance stream is the only carrier.
            doc.GetAcroForm()!.FindField(F.Name.Name)!.RawDictionary.Remove("V");
            return doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(OnlyInAppearance(OldAppearanceText.Tj), F.Name.OldValue).Should().NotBeEmpty();
        SavedPdfLeakScanner.FindTerm(OnlyInAppearance(OldAppearanceText.Hex), F.Name.OldValue).Should().NotBeEmpty();
        SavedPdfLeakScanner.FindTerm(OnlyInAppearance(OldAppearanceText.KernedTj), F.Name.OldValue).Should().BeEmpty(
            "a kerned TJ split is not contiguous; only the stream marker can prove the appearance is gone");
        SavedPdfLeakScanner.FindTerm(OnlyInAppearance(OldAppearanceText.KernedTj), F.NormalMarker(F.Name)).Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SetValue_OnForeignAppearances_TheOldAppearanceIsNotSaved(OldAppearanceText form, string version, bool clear)
    {
        byte[] saved;
        using (var doc = PdfDocument.Open(F.Build(form, version)))
        {
            ChangeAll(doc, clear);
            saved = doc.SaveToBytes();
        }

        AssertOldAppearancesGone(saved, $"{form}/{version}/{(clear ? "clear" : "change")}");

        // Save As again from the saved copy: nothing comes back.
        using (var reopened = PdfDocument.Open(saved))
            AssertOldAppearancesGone(reopened.SaveToBytes(), $"{form}/{version} resaved");
    }

    [Fact]
    public void SetValue_SaveToPath_TheOldAppearanceIsNotSaved()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stale-ap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "in.pdf");
            var output = Path.Combine(dir, "out.pdf");
            File.WriteAllBytes(input, F.Build(OldAppearanceText.KernedTj));
            using (var doc = PdfDocument.Open(input))
            {
                ChangeAll(doc, clear: false);
                doc.Save(output);
            }
            AssertOldAppearancesGone(File.ReadAllBytes(output), "path");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetValue_RegeneratesTextAppearances_AndDropsOnlyWhatItCannotDraw()
    {
        using var doc = PdfDocument.Open(F.Build(OldAppearanceText.KernedTj));
        ChangeAll(doc, clear: false);
        var form = doc.GetAcroForm()!;

        foreach (var field in F.Changed)
        {
            foreach (var widget in form.FindField(field.Name)!.WidgetDictionaries)
            {
                var content = NormalAppearanceContent(doc, widget);
                if (!field.Regenerates)
                {
                    content.Should().BeNull($"{field.Name}: excise cannot draw this widget, so its stale /AP is dropped");
                    continue;
                }

                content.Should().NotBeNull($"{field.Name}: the appearance is regenerated");
                widget.GetOptional("AP").Should().BeOfType<PdfDictionary>($"{field.Name}: a new direct /AP, never an edit of a shared one");
                var ap = (PdfDictionary)widget.GetOptional("AP")!;
                ap.Keys.Select(k => k.Value).Should().Equal(["N"], $"{field.Name}: the old /D is gone with the old /N");
                content.Should().Contain("/Tx BMC").And.Contain("EMC");
                content.Should().NotContain(field.OldValue);
                if (field == F.Secret)
                {
                    content.Should().Contain("(*******) Tj", "a password field draws one mask character per character");
                    content.Should().NotContain(field.NewValue, "a password appearance never draws the password");
                }
                else if (field == F.Comb)
                {
                    foreach (var c in field.NewValue)
                        content.Should().Contain($"({c}) Tj", "a comb field draws each character in its own cell");
                }
                else
                {
                    content.Should().Contain($"({field.NewValue}) Tj");
                }
            }
        }

        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!)!;
        acroForm.GetBool("NeedAppearances").Should().BeTrue("the list box and the rotated widget were left to the reader");
    }

    [Fact]
    public void SetValue_EveryWidgetRegenerated_DoesNotSetNeedAppearances()
    {
        using var doc = PdfDocument.Open(F.Build());
        doc.GetAcroForm()!.FindField(F.Name.Name)!.SetValue(F.Name.NewValue);
        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!)!;
        acroForm.GetBool("NeedAppearances").Should().BeFalse("the one changed widget has a faithful new appearance");
    }

    /// <summary>
    /// A base-14 font with no <c>/Encoding</c> uses StandardEncoding, which
    /// agrees with ASCII except at the quotes: plain ASCII is redrawn, a value
    /// with a straight quote is left to the reader rather than drawn wrong.
    /// </summary>
    [Theory]
    [InlineData("BETANEW", true)]
    [InlineData("O'BRIEN", false)]
    public void SetValue_FontWithoutEncoding_RedrawsOnlyWhatStandardEncodingCanShow(string value, bool redrawn)
    {
        using var doc = PdfDocument.Open(F.Build());
        var acroForm = (PdfDictionary)doc.Resolve(doc.Catalog.GetOptional("AcroForm")!)!;
        var dr = (PdfDictionary)doc.Resolve(acroForm.GetOptional("DR")!)!;
        var helv = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(dr.GetOptional("Font")!)!).GetOptional("Helv")!)!;
        helv.Remove("Encoding");

        var field = doc.GetAcroForm()!.FindField(F.Name.Name)!;
        field.SetValue(value);

        var content = NormalAppearanceContent(doc, field.WidgetDictionaries[0]);
        if (redrawn)
            content.Should().Contain($"({value}) Tj");
        else
            content.Should().BeNull("the stale appearance is dropped and the reader redraws it");
        acroForm.GetBool("NeedAppearances").Should().Be(!redrawn);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), F.NormalMarker(F.Name)).Should().BeEmpty();
    }

    [Fact]
    public void SetValue_Clear_DrawsAnEmptyAppearance()
    {
        using var doc = PdfDocument.Open(F.Build());
        var field = doc.GetAcroForm()!.FindField(F.Name.Name)!;
        field.SetValue(null);
        var content = NormalAppearanceContent(doc, field.WidgetDictionaries[0]);
        content.Should().NotBeNull();
        content.Should().NotContain("Tj").And.Contain("/Tx BMC");
    }

    [Fact]
    public void SetValue_SharedAppearance_IsCopiedOnWrite_TheOtherFieldKeepsIt()
    {
        using var doc = PdfDocument.Open(F.Build());
        var form = doc.GetAcroForm()!;
        var b = form.FindField(F.SharedB.Name)!.WidgetDictionaries[0];
        var bApBefore = b.GetOptional("AP");
        form.FindField(F.SharedA.Name)!.SetValue(F.SharedA.NewValue);

        var saved = doc.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, F.SharedMarker).Should().NotBeEmpty("SharedB still draws the shared appearance");

        using var reopened = PdfDocument.Open(saved);
        var reForm = reopened.GetAcroForm()!;
        NormalAppearanceContent(reopened, reForm.FindField(F.SharedA.Name)!.WidgetDictionaries[0])
            .Should().Contain($"({F.SharedA.NewValue}) Tj");
        var bAp = (PdfDictionary)reopened.Resolve(reForm.FindField(F.SharedB.Name)!.WidgetDictionaries[0].GetOptional("AP")!)!;
        var bNormal = (PdfStream)reopened.Resolve(bAp.GetOptional("N")!)!;
        bNormal.GetNameOrNull("StaleMarker").Should().Be(F.SharedMarker, "the other field's appearance is untouched");
        bApBefore.Should().BeOfType<PdfReference>("fixture sanity: the /AP is shared by reference");
    }

    [Fact]
    public void SetValue_CheckBox_KeepsItsStateAppearances()
    {
        using var doc = PdfDocument.Open(F.Build());
        var box = doc.GetAcroForm()!.FindField(F.CheckBoxName)!;
        box.SetValue("Off");
        var widget = box.WidgetDictionaries[0];
        widget.GetNameOrNull("AS").Should().Be("Off");
        var states = (PdfDictionary)((PdfDictionary)doc.Resolve(widget.GetOptional("AP")!)!).GetOptional("N")!;
        states.Keys.Select(k => k.Value).Should().BeEquivalentTo(["Yes", "Off"],
            "a checkbox's /AP states draw a fixed mark, not value text; /AS selects among them");
    }

    [Fact]
    public void SetValue_RichTextValue_IsRemoved()
    {
        using var doc = PdfDocument.Open(F.Build());
        var field = doc.GetAcroForm()!.FindField(F.Name.Name)!;
        field.RawDictionary.SetString("RV", "<body><p>ALPHAOLD</p></body>");
        field.SetValue(F.Name.NewValue);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), F.Name.OldValue).Should().BeEmpty(
            "the rich-text value is the old value in markup");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    internal static void ChangeAll(PdfDocument doc, bool clear)
    {
        var form = doc.GetAcroForm()!;
        foreach (var field in F.Changed)
            form.FindField(field.Name)!.SetValue(clear ? null : field.NewValue);
    }

    private static void AssertOldAppearancesGone(byte[] saved, string label)
    {
        foreach (var marker in F.RemovedMarkers())
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty($"{label}: the old appearance {marker} must not be saved");
        foreach (var field in ValueMustVanish)
            SavedPdfLeakScanner.FindTerm(saved, field.OldValue).Should().BeEmpty($"{label}: {field.Name}'s old value must not be saved");
    }

    private static string? NormalAppearanceContent(PdfDocument doc, PdfDictionary widget)
    {
        if (widget.GetOptional("AP") is not { } apObj || doc.Resolve(apObj) is not PdfDictionary ap) return null;
        if (ap.GetOptional("N") is not { } nObj || doc.Resolve(nObj) is not PdfStream normal) return null;
        return Encoding.Latin1.GetString(normal.DecodedData);
    }
}
