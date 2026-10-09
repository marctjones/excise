using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Core.Signatures;
using Excise.Core.Text.Segmentation;
using Excise.Core.Writing;
using Excise.Core.Xfa;
using Excise.TestSupport;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #2024 (decisions 12 and 19 of docs/architecture/xfa-rendering.md): every save of a laid-out
/// dynamic XFA form, and of a static XFA form a fill changed, strips the certification (catalog
/// <c>/Perms</c> DocMDP and UR3, the signatures they name, <c>/Legal</c>) and reports it; the
/// certification field stays as one unsigned field per name; approval signatures stay. Absence is
/// read with the inflating byte scanner (every signer marker), structure with excise's parser;
/// Excise.Rendering.Tests (XfaCertificationStripOracleTests) reads the same files with qpdf and mutool.
/// </summary>
public class XfaCertificationStripTests
{
    private static readonly string[] SignatureMarkers =
    {
        CertifiedFormFixtures.DocMdpSigner, CertifiedFormFixtures.Ur3Signer, CertifiedFormFixtures.LegalMarker,
        CertifiedFormFixtures.SignatureAppearanceMarker,
    };

    private static PdfDocument LaidOut(byte[] pdf, out XfaLayoutResult result, string? password = null)
    {
        var document = PdfDocument.Open(pdf, new PdfOpenOptions { UserPassword = password });
        result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.ShowsForm.Should().BeTrue(result.FailureReason);
        return document;
    }

    private static byte[] CertifiedDynamic(CertifiedFormFixtures.Options? options = null)
        => CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm(), options);

    private static PdfDictionary AcroFormDictionary(PdfDocument document)
        => (PdfDictionary)document.Resolve(document.Catalog.GetOptional("AcroForm")!);

    private static List<PdfField> FieldsNamed(PdfDocument document, string fullName)
        => document.GetAcroForm()!.Fields.Where(f => f.FullName == fullName).ToList();

    [Fact]
    public void PositiveControl_TheFixtureCarriesEveryMarker_AndALayoutWithoutSaveKeepsThem()
    {
        var input = CertifiedDynamic();
        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(input, marker).Should().NotBeEmpty($"the scanner must see {marker} in the input");

        using var document = LaidOut(input, out var result);
        result.CertificationRemovedOnSave.Should().BeTrue();
        CertificationStripper.HasCertification(document).Should().BeTrue("the strip runs on save, not on open");
        document.CertificationRemovals.Should().BeEmpty("nothing was saved yet");
    }

    [Fact]
    public void LaidOutForm_Save_RemovesPermsSignaturesAndLegal_AndReportsEach()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        var saved = document.SaveToBytes();

        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty($"{marker} names the voided certification");
        using var reopened = PdfDocument.Open(saved);
        reopened.Catalog.ContainsKey("Perms").Should().BeFalse();
        reopened.Catalog.ContainsKey("Legal").Should().BeFalse();
        document.CertificationRemovals.Should().Contain(l => l.StartsWith("/Perms /DocMDP", StringComparison.Ordinal))
            .And.Contain(l => l.StartsWith("/Perms /UR3", StringComparison.Ordinal))
            .And.Contain(l => l.StartsWith("/Legal", StringComparison.Ordinal));
    }

    [Fact]
    public void LaidOutForm_Save_LeavesExactlyOneUnsignedFieldPerName_AndClearsAppendOnly()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        using var reopened = PdfDocument.Open(document.SaveToBytes());

        var names = reopened.GetAcroForm()!.Fields.Select(f => f.FullName).ToList();
        names.Should().OnlyHaveUniqueItems("one AcroForm field per XFA field (ISO 32000-2 Annex K.2)");
        var signature = FieldsNamed(reopened, CertifiedFormFixtures.SignatureFieldFullName).Should().ContainSingle().Subject;
        signature.FieldType.Should().Be(PdfFieldType.Signature);
        signature.RawDictionary.ContainsKey("V").Should().BeFalse("the field is unsigned");
        SignedFieldDetector.HasSignedField(reopened).Should().BeFalse();
        var sigFlags = (PdfInteger)reopened.Resolve(AcroFormDictionary(reopened).GetOptional("SigFlags")!);
        (sigFlags.Value & 2).Should().Be(0, "AppendOnly (Table 225) claims signatures a full save would invalidate");
        (sigFlags.Value & 1).Should().Be(1, "SignaturesExist: a signature field remains");
    }

    [Fact]
    public void InMemory_BeforeTheSave_BothFieldsShareTheName_TheSaveLeavesTheGeneratedOne()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        FieldsNamed(document, CertifiedFormFixtures.SignatureFieldFullName).Should().HaveCount(2,
            "fixture sanity: the certification field and the generated one (#2024 S1 note)");
        var generated = FieldsNamed(document, CertifiedFormFixtures.SignatureFieldFullName)
            .Single(f => f.RawDictionary.GetOptional("V") == null);

        document.SaveToBytes();

        FieldsNamed(document, CertifiedFormFixtures.SignatureFieldFullName).Should().ContainSingle()
            .Which.RawDictionary.Should().BeSameAs(generated.RawDictionary);
    }

    [Fact]
    public void CertificationFieldNotOnAnyPage_IsNotPrunedByThePageCut_SoTheStripRemovesTheDuplicate()
    {
        using var document = LaidOut(CertifiedDynamic(new() { WidgetInAnnots = false }), out _);
        var saved = document.SaveToBytes();

        document.CertificationRemovals.Should().Contain(l => l.Contains("removed, another field of that name remains"));
        using var reopened = PdfDocument.Open(saved);
        FieldsNamed(reopened, CertifiedFormFixtures.SignatureFieldFullName).Should().ContainSingle()
            .Which.RawDictionary.ContainsKey("V").Should().BeFalse();
        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
    }

    [Fact]
    public void Ur3AsADirectDictionary_OhioShape_IsRemoved()
    {
        using var document = LaidOut(CertifiedDynamic(new() { Ur3Direct = true }), out _);
        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, CertifiedFormFixtures.Ur3Signer).Should().BeEmpty();
        PdfDocument.Open(saved).Catalog.ContainsKey("Perms").Should().BeFalse();
    }

    [Fact]
    public void Ur3Only_HsbcShape_RemovesIndirectPermsWhole_AndKeepsLegal()
    {
        using var document = LaidOut(CertifiedDynamic(new() { DocMdp = false, PermsIndirect = true }), out var result);
        result.CertificationRemovedOnSave.Should().BeTrue();
        var saved = document.SaveToBytes();

        using var reopened = PdfDocument.Open(saved);
        reopened.Catalog.ContainsKey("Perms").Should().BeFalse();
        SavedPdfLeakScanner.FindTerm(saved, CertifiedFormFixtures.Ur3Signer).Should().BeEmpty();
        reopened.Catalog.ContainsKey("Legal").Should().BeTrue("a legal attestation accompanies a certification (§12.8.7); there was none");
        document.CertificationRemovals.Should().Contain(l => l.StartsWith("/Perms /UR3", StringComparison.Ordinal))
            .And.NotContain(l => l.StartsWith("/Perms /DocMDP", StringComparison.Ordinal) || l.StartsWith("/Legal", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovalSignature_PermsDoesNotName_StaysSigned_AndAppendOnlyStays()
    {
        using var document = LaidOut(CertifiedDynamic(new() { Approval = true, WidgetInAnnots = false }), out _);
        // The approval widget sat on the placeholder page; keep it on a laid-out page so the
        // #2012 cut does not prune it and the strip is what this checks.
        var approval = document.GetAcroForm()!.FindField(CertifiedFormFixtures.ApprovalFieldName)!.RawDictionary;
        var annots = document.Resolve(document.Pages[0].Dictionary.GetOptional("Annots") ?? PdfNull.Instance) as PdfArray ?? new PdfArray();
        annots.Add(document.GetReferenceTo(approval)!);
        document.Pages[0].Dictionary["Annots"] = annots;

        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, CertifiedFormFixtures.ApprovalSigner).Should().NotBeEmpty(
            "an approval signature is not certification; the #1415 warning covers it");
        SavedPdfLeakScanner.FindTerm(saved, CertifiedFormFixtures.DocMdpSigner).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        reopened.GetAcroForm()!.FindField(CertifiedFormFixtures.ApprovalFieldName)!.RawDictionary.ContainsKey("V").Should().BeTrue();
        var sigFlags = (PdfInteger)reopened.Resolve(AcroFormDictionary(reopened).GetOptional("SigFlags")!);
        (sigFlags.Value & 2).Should().Be(2, "a signed field remains");
    }

    [Fact]
    public void SecondSave_AndASaveOfTheSavedCopy_FindNothingNew()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        document.SaveToBytes();
        var lines = document.CertificationRemovals!.ToList();
        var second = document.SaveToBytes();
        document.CertificationRemovals.Should().Equal(lines, "a second save removes nothing and repeats nothing");

        using var reopened = LaidOut(second, out var again);
        again.Status.Should().Be(XfaLayoutStatus.AlreadyLaidOut);
        again.CertificationRemovedOnSave.Should().BeFalse();
        reopened.SaveToBytes();
        reopened.CertificationRemovals.Should().BeEmpty();
    }

    [Fact]
    public void AlreadyLaidOutCopy_ThatStillNamesACertification_IsStrippedOnSave()
    {
        // An earlier excise saved the laid-out copy with /Perms intact (before #2024).
        using var first = LaidOut(CertifiedFormFixtures.DynamicForm(), out _);
        var stale = CertifiedFormFixtures.Certify(first.SaveToBytes(), new() { WidgetInAnnots = false });

        using var document = LaidOut(stale, out var result);
        result.Status.Should().Be(XfaLayoutStatus.AlreadyLaidOut);
        result.CertificationRemovedOnSave.Should().BeTrue();
        var saved = document.SaveToBytes();

        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
    }

    [Fact]
    public void EncryptedForm_RoundTripKeepsAes128AndP_AndStripsTheCertification()
    {
        var plain = CertifiedDynamic();
        using (var source = PdfDocument.Open(plain))
        {
            plain = source.SaveToBytes(new PdfEncryptionOptions
            {
                UserPassword = "", OwnerPassword = "owner", Permissions = -20, Algorithm = PdfEncryptionAlgorithm.Aes128,
            });
        }

        using var document = LaidOut(plain, out _, password: "");
        document.IsEncrypted.Should().BeTrue();
        var saved = document.SaveToBytes(document.GetReEncryptionOptions(""));

        using var reopened = PdfDocument.Open(saved, new PdfOpenOptions { UserPassword = "" });
        reopened.IsEncrypted.Should().BeTrue("encryption round-trips");
        reopened.Permissions.RawValue.Should().Be(-20);
        reopened.Catalog.ContainsKey("Perms").Should().BeFalse();
        reopened.Catalog.ContainsKey("Legal").Should().BeFalse();
    }

    [Fact]
    public void ObjectStreamOutput_AndReduceFileSize_CarryNoCertification()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        var saved = document.SaveToBytes();
        System.Text.Encoding.ASCII.GetString(saved).Should().Contain("/ObjStm", "fixture sanity: an unencrypted 1.5+ save packs objects");
        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);

        var path = Path.Combine(Path.GetTempPath(), $"excise-2024-reduce-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfDocumentOptimizer.SaveOptimizedCopy(saved, path, PdfOptimizationOptions.ForPreset(PdfOptimizationPreset.Lossless));
            var reduced = File.ReadAllBytes(path);
            foreach (var marker in SignatureMarkers)
                SavedPdfLeakScanner.FindTerm(reduced, marker).Should().BeEmpty(marker);
            PdfDocument.Open(reduced).Catalog.ContainsKey("Perms").Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RedactedCopyOfALaidOutForm_CarriesNoCertification()
    {
        using var document = LaidOut(CertifiedDynamic(), out _);
        document.GetPage(1).RedactArea(new PdfRectangle(60, 700, 300, 740), RedactionOptions.Default with { DrawBox = false });
        var saved = document.SaveToBytes();

        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
        PdfDocument.Open(saved).Catalog.ContainsKey("Perms").Should().BeFalse();
    }

    // ------------------------------------------------------------ what stays as today

    [Fact]
    public void DocumentThatIsNotALaidOutForm_KeepsItsPerms()
    {
        var notXfa = PdfDocument.CreateNew();
        notXfa.Pages.AddBlank(612, 792);
        var certified = CertifiedFormFixtures.Certify(notXfa.SaveToBytes());

        using var document = PdfDocument.Open(certified);
        var saved = document.SaveToBytes();

        PdfDocument.Open(saved).Catalog.ContainsKey("Perms").Should().BeTrue("only an XFA save path asks for the strip");
        document.CertificationRemovals.Should().BeNull();
    }

    [Fact]
    public void StaticXfaForm_OpenedAndSavedWithoutAFill_KeepsItsPerms()
    {
        using var document = PdfDocument.Open(CertifiedFormFixtures.Certify(XfaStaticFillFixtures.Build()));
        document.DetectXfaForm().Should().Be(PdfXfaFormKind.Static);
        var saved = document.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, CertifiedFormFixtures.DocMdpSigner).Should().NotBeEmpty(
            "decision 12 applies to a filled save; an unfilled save is reported on #2024, not changed");
    }

    [Fact]
    public void StaticXfaForm_Filled_StripsOnSave_AndKeepsTheSignatureFieldUnsignedWithItsLock()
    {
        using var document = PdfDocument.Open(CertifiedFormFixtures.Certify(XfaStaticFillFixtures.Build(), new() { Lock = true }));
        document.GetAcroForm()!.FindField(XfaStaticFillFixtures.NamePath)!.SetValue("Grace Hopper");
        var saved = document.SaveToBytes();

        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
        document.CertificationRemovals.Should().Contain(l => l.Contains("kept unsigned"));
        using var reopened = PdfDocument.Open(saved);
        var field = reopened.GetAcroForm()!.FindField(CertifiedFormFixtures.SignatureFieldFullName)!.RawDictionary;
        field.ContainsKey("V").Should().BeFalse();
        field.ContainsKey("AP").Should().BeFalse("the drawn signature is part of signing (§12.7.5.5)");
        field.ContainsKey("Lock").Should().BeTrue("Table 235: the fields to lock WHEN the field is signed; an authoring choice");
    }

    [Fact]
    public void StaticXfaForm_FillTheDatasetsCannotMap_StillStripsOnSave()
    {
        using var document = PdfDocument.Open(CertifiedFormFixtures.Certify(XfaStaticFillFixtures.Build()));
        document.GetAcroForm()!.FindField(XfaStaticFillFixtures.StrayPath)!.SetValue("nowhere");
        document.XfaStaticDataSync!.Notes.Should().NotBeEmpty("fixture sanity: the datasets could not take this value");
        var saved = document.SaveToBytes();

        foreach (var marker in SignatureMarkers)
            SavedPdfLeakScanner.FindTerm(saved, marker).Should().BeEmpty(marker);
    }

    [Fact]
    public void RemovedDuplicate_LeavesNoEmptyParentField()
    {
        using var document = LaidOut(CertifiedDynamic(new() { WidgetInAnnots = false }), out _);
        using var reopened = PdfDocument.Open(document.SaveToBytes());

        var fields = (PdfArray)reopened.Resolve(AcroFormDictionary(reopened).GetOptional("Fields")!);
        fields.Select(f => (PdfDictionary)reopened.Resolve(f))
            .Where(f => (reopened.Resolve(f.GetOptional("T") ?? PdfNull.Instance) as PdfString)?.Value == CertifiedFormFixtures.ParentName)
            .Should().ContainSingle("the certification field's emptied parent goes with it; the generated form1[0] stays");
    }

    // ------------------------------------------------------------ #2025

    [Fact]
    public void SignedFieldDetector_FindsASignedFieldUnderASubform()
    {
        var notXfa = PdfDocument.CreateNew();
        notXfa.Pages.AddBlank(612, 792);
        using var document = PdfDocument.Open(CertifiedFormFixtures.Certify(notXfa.SaveToBytes(), new() { Ur3 = false }));

        var acroForm = AcroFormDictionary(document);
        var top = (PdfArray)document.Resolve(acroForm.GetOptional("Fields")!);
        ((PdfDictionary)document.Resolve(top[0])).ContainsKey("FT").Should().BeFalse("fixture sanity: the signed field is a kid");
        SignedFieldDetector.HasSignedField(document).Should().BeTrue("#2025: the whole field tree is walked");
    }

    [Fact]
    public void SignedFieldDetector_FindsImm5257esCertificationField()
    {
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", "imm5257e.pdf");
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", "test-pdfs/xfa-real/imm5257e.pdf"));
        using var document = PdfDocument.Open(File.ReadAllBytes(source!));

        SignedFieldDetector.Fields(document).Should().Contain(f => f.FullName == "form1[0].SignatureField4[0]",
            "fixture sanity: Designer filed the DocMDP field under form1[0]");
        SignedFieldDetector.HasSignedField(document).Should().BeTrue("#2025: the signed field is a kid, not a top-level field");
    }
}
