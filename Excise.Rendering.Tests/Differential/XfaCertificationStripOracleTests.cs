using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Security;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2024 (decisions 12 and 19 of docs/architecture/xfa-rendering.md), read by tools that are not
/// excise: a save of a laid-out certified XFA form carries no <c>/Perms</c> DocMDP or UR3, no
/// <c>/Legal</c>, no AppendOnly flag and no object holding a <c>/ByteRange</c> (qpdf's JSON lists
/// every object the file holds, referenced or not); <c>qpdf --check</c> is clean; qpdf reads exactly one
/// field per name; the encryption survives; and mutool and Poppler extract the same text from every
/// page as from the same layout saved without the strip. The baseline, a layout whose strip was never
/// registered, is the #2024 defect itself: qpdf sees <c>/Perms</c> still naming signatures there, which
/// is the planted failure every absence check below is measured against.
/// </summary>
public class XfaCertificationStripOracleTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _temp = new();

    public XfaCertificationStripOracleTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    public static TheoryData<string> CertifiedCorpusForms => new() { "imm5257e", "ohio-expense-report" };

    private static void RequireTools()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
    }

    private static byte[] Corpus(string name)
    {
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", name + ".pdf");
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "xfa-real corpus (scripts/download-xfa-real-corpus.sh)", $"test-pdfs/xfa-real/{name}.pdf"));
        return File.ReadAllBytes(source!);
    }

    private string TempPath(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-2024-{tag}-{Guid.NewGuid():N}.pdf");
        _temp.Add(path);
        return path;
    }

    /// <summary>
    /// Lay the form out and save it (re-encrypted when it was encrypted). <paramref name="withoutStrip"/>
    /// marks the strip as already registered before the layout, so it never is: the #2024 baseline.
    /// </summary>
    private string LayOutAndSave(byte[] bytes, string tag, bool withoutStrip = false)
    {
        using var document = PdfDocument.Open(bytes, new PdfOpenOptions { UserPassword = string.Empty });
        if (withoutStrip)
            document.CertificationRemovals = new List<string>();
        var result = document.ApplyXfaLayout(cancellationToken: TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = TempPath(tag);
        document.Save(path, document.GetReEncryptionOptions(string.Empty));
        return path;
    }

    private void AssertCertificationGone(string path, bool encrypted)
    {
        var password = encrypted ? string.Empty : null;
        var view = QpdfReferenceTool.CertificationView(path, password);
        view.Should().NotBeNull("qpdf reads the saved file");
        _out.WriteLine($"{Path.GetFileName(path)}: /Perms [{string.Join(" ", view!.PermsKeys)}], /Legal {view.HasLegal}, "
            + $"/SigFlags {view.SigFlags}, /ByteRange objects [{string.Join(", ", view.SignatureObjects)}]");
        view!.PermsKeys.Should().BeEmpty("no /Perms entry names a signature whose bytes the save rewrote");
        view.HasLegal.Should().BeFalse("the legal attestation accompanies the certification (ISO 32000-2 §12.8.7)");
        view.SignatureObjects.Should().BeEmpty("no signature dictionary of the original is left in the file");
        if (view.SigFlags is { } flags)
            (flags & 2).Should().Be(0, "AppendOnly claims signatures a full save would invalidate (Table 225)");

        QpdfReferenceTool.Check(path, password)!.Value.Success.Should().BeTrue("qpdf --check is clean");
        var fields = QpdfReferenceTool.FieldObjectsByName(path, password);
        fields.Should().NotBeNull().And.NotBeEmpty();
        fields!.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).Should().BeEmpty(
            "one AcroForm field per XFA field (ISO 32000-2 Annex K.2)");
    }

    private static void AssertSameText(string baseline, string stripped, bool encrypted)
    {
        var password = encrypted ? string.Empty : null;
        var pages = QpdfReferenceTool.PageCount(stripped, password);
        pages.Should().NotBeNull().And.Be(QpdfReferenceTool.PageCount(baseline, password), "the strip removes no page");
        var before = MutoolTextExtractor.ExtractAllPages(baseline, pages!.Value, password);
        var after = MutoolTextExtractor.ExtractAllPages(stripped, pages.Value, password);
        before.Should().NotBeNull();
        after.Should().Equal(before, "mutool reads the same text from every page");
        if (PdftotextTextExtractor.IsAvailable)
        {
            for (int page = 1; page <= pages.Value; page++)
            {
                PdftotextTextExtractor.ExtractPage(stripped, page, password)
                    .Should().Be(PdftotextTextExtractor.ExtractPage(baseline, page, password), $"Poppler reads page {page} the same");
            }
        }
    }

    // ================================================================ synthetic, always runs

    [Fact]
    public void Synthetic_Baseline_TheUnstrippedLayoutStillNamesSignatures_TheDefect()
    {
        RequireTools();
        var baseline = LayOutAndSave(CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm()), "syn-baseline", withoutStrip: true);

        var view = QpdfReferenceTool.CertificationView(baseline)!;
        view.PermsKeys.Should().Contain(new[] { "/DocMDP", "/UR3" });
        view.SignatureObjects.Should().NotBeEmpty("the checks below must be able to see a signature left behind");
        view.HasLegal.Should().BeTrue();
    }

    [Fact]
    public void Synthetic_LaidOutSave_QpdfSeesNoCertification_AndMutoolAndPopplerReadTheSameText()
    {
        RequireTools();
        var certified = CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm());
        var baseline = LayOutAndSave(certified, "syn-baseline", withoutStrip: true);
        var stripped = LayOutAndSave(certified, "syn-stripped");

        AssertCertificationGone(stripped, encrypted: false);
        var widgets = QpdfReferenceTool.AcroFormWidgets(stripped)!;
        widgets.Should().ContainSingle(w => w.FullName == CertifiedFormFixtures.SignatureFieldFullName)
            .Which.Value.Should().BeNull("the signature field is kept unsigned");
        AssertSameText(baseline, stripped, encrypted: false);
        MutoolTextExtractor.ExtractPage(stripped, 1).Should().Contain("Ada Lovelace");
    }

    [Fact]
    public void Synthetic_CertificationFieldOnNoPage_QpdfSeesTwoFieldsOfOneName_UntilTheStrip()
    {
        RequireTools();
        var certified = CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm(), new() { WidgetInAnnots = false });
        var baseline = LayOutAndSave(certified, "syn-offpage-baseline", withoutStrip: true);
        QpdfReferenceTool.FieldObjectsByName(baseline)![CertifiedFormFixtures.SignatureFieldFullName].Should().HaveCount(2,
            "the planted failure: the #2012 cut cannot prune a field whose widget no page lists");

        var stripped = LayOutAndSave(certified, "syn-offpage-stripped");

        AssertCertificationGone(stripped, encrypted: false);
        QpdfReferenceTool.FieldObjectsByName(stripped)![CertifiedFormFixtures.SignatureFieldFullName].Should().ContainSingle();
    }

    [Fact]
    public void Synthetic_EncryptedAes128_RoundTripKeepsTheEncryption_AndStrips()
    {
        RequireTools();
        var certified = CertifiedFormFixtures.Certify(CertifiedFormFixtures.DynamicForm());
        byte[] encrypted;
        using (var source = PdfDocument.Open(certified))
        {
            encrypted = source.SaveToBytes(new PdfEncryptionOptions
            {
                UserPassword = string.Empty, OwnerPassword = "owner", Permissions = -20, Algorithm = PdfEncryptionAlgorithm.Aes128,
            });
        }

        var stripped = LayOutAndSave(encrypted, "syn-encrypted");

        var encryption = QpdfReferenceTool.ShowEncryption(stripped, string.Empty);
        encryption.Should().NotBeNull().And.Contain("R = 4").And.Contain("P = -20").And.Contain("AESv2");
        AssertCertificationGone(stripped, encrypted: true);
    }

    // ================================================================ the CLI, out of process

    [Fact]
    public void Cli_FillFormOnACertifiedStaticXfaForm_StripsAndWarnsOnStderr()
    {
        RequireTools();
        var cli = FindCliAssembly();
        Assert.SkipWhen(cli == null, "Excise.Cli binary unavailable (build Excise.Cli first)");
        var input = TempPath("static-certified");
        File.WriteAllBytes(input, CertifiedFormFixtures.Certify(XfaStaticFillFixtures.Build()));
        QpdfReferenceTool.CertificationView(input)!.PermsKeys.Should().Contain("/DocMDP", "fixture sanity");
        var output = TempPath("static-filled");

        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { cli!, "fill-form", input, output, "--field", $"{XfaStaticFillFixtures.NamePath}=Grace" })
            start.ArgumentList.Add(argument);
        using var child = System.Diagnostics.Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = child.StandardError.ReadToEnd();
        child.WaitForExit(120_000).Should().BeTrue();
        child.ExitCode.Should().Be(0, $"stdout={stdout.Result} stderr={stderr}");

        stderr.Should().Contain("Warning: The form's certification was removed")
            .And.Contain("Warning: certification removed: /Perms /DocMDP")
            .And.Contain("Warning: certification removed: /Perms /UR3")
            .And.Contain("Warning: certification removed: /Legal");
        AssertCertificationGone(output, encrypted: false);
        QpdfReferenceTool.FieldObjectsByName(output)!.Should().ContainKey(CertifiedFormFixtures.SignatureFieldFullName,
            "the signature field stays, unsigned");
    }

    /// <summary>The CLI this checkout built; never another checkout's (see FlattenOcrRedactionTests).</summary>
    private static string? FindCliAssembly()
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = TestRepoLayout.FindFileInLocalCheckout(
                "Excise.Cli", "bin", configuration, "net10.0", "excise.dll");
            if (candidate != null) return candidate;
        }
        return null;
    }

    // ================================================================ the real certified forms

    [Theory]
    [MemberData(nameof(CertifiedCorpusForms))]
    public void CertifiedCorpusForm_LaidOutSave_QpdfSeesNoCertification_AndTheTextIsUnchanged(string name)
    {
        RequireTools();
        var bytes = Corpus(name);
        var source = TempPath(name + "-source");
        File.WriteAllBytes(source, bytes);
        bool encrypted = QpdfReferenceTool.IsEncrypted(source) == true;
        var password = encrypted ? string.Empty : null;
        QpdfReferenceTool.CertificationView(source, password)!.PermsKeys.Should().Contain(new[] { "/DocMDP", "/UR3" },
            "fixture sanity: the form is certified");

        var baseline = LayOutAndSave(bytes, name + "-baseline", withoutStrip: true);
        QpdfReferenceTool.CertificationView(baseline, password)!.SignatureObjects.Should().NotBeEmpty(
            "#2024: without the strip the saved copy still holds the signatures /Perms names");
        var stripped = LayOutAndSave(bytes, name + "-stripped");

        AssertCertificationGone(stripped, encrypted);
        if (encrypted)
        {
            QpdfReferenceTool.ShowEncryption(stripped, string.Empty).Should().Contain("R = 4").And.Contain("P = -20",
                "IMM 5257e is AES-128 with /P -20; the save keeps both");
        }
        AssertSameText(baseline, stripped, encrypted);
    }
}
