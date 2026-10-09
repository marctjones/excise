using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Security;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using SkiaSharp;
using F = Excise.TestSupport.StaleAppearanceFixtures;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #2017: after <see cref="PdfField.SetValue"/> changes (or clears) a field
/// whose widget had an appearance excise did not author, the saved file holds
/// neither the old appearance streams nor the old value, on every save path,
/// judged by tools that are not excise: qpdf's JSON dump of every object
/// (decrypting an encrypted save itself), <c>qpdf --check</c>,
/// <see cref="SavedPdfLeakScanner"/>, and what mutool and Poppler DRAW.
///
/// <para>The renderers are asked about a copy with <c>/NeedAppearances</c>
/// neutralised (qpdf rewrites it uncompressed, then the key is renamed in
/// place), because both honour the flag and redraw from <c>/V</c>, which hid
/// this defect: the reproduction showed mutool's page text clean while the old
/// appearance was still in the file and drawn by readers that ignore the
/// flag.</para>
/// </summary>
public sealed class StaleFieldAppearanceOracleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stale-ap-oracle-{Guid.NewGuid():N}");

    public StaleFieldAppearanceOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public enum SavePath
    {
        Classic,         // %PDF-1.4: classic xref table
        Compressed,      // %PDF-1.7: object streams + xref stream
        Encrypted,       // encrypted on save (AES-256)
        EncryptedSource, // qpdf-encrypted source (AES-128 R4), saved re-encrypted
        ReopenResave,    // save, reopen, save again
    }

    /// <summary>Fields whose old value is in no legitimate carrier after the change (the choice options list theirs; SharedB keeps SharedA's).</summary>
    private static readonly F.Field[] ValueMustVanish = [F.Name, F.Comb, F.Notes, F.Secret, F.Turned, F.Multi];

    public static TheoryData<OldAppearanceText, SavePath, bool> Cases()
    {
        var data = new TheoryData<OldAppearanceText, SavePath, bool>();
        foreach (var form in Enum.GetValues<OldAppearanceText>())
            foreach (var path in Enum.GetValues<SavePath>())
                foreach (var clear in new[] { false, true })
                    data.Add(form, path, clear);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChangeOrClear_ThenSave_NoOldAppearanceInTheFile(OldAppearanceText form, SavePath path, bool clear)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        var label = $"{form}/{path}/{(clear ? "clear" : "change")}";
        var source = F.Build(form, path == SavePath.Classic ? "1.4" : "1.7");
        var sourceDump = QpdfDump(source);
        foreach (var marker in F.RemovedMarkers())
            sourceDump.Should().Contain(marker, $"fixture sanity: qpdf sees {marker}");

        var saved = path switch
        {
            SavePath.EncryptedSource => ChangeEncryptedSource(source, clear),
            _ => Change(source, path, clear),
        };
        var plain = path is SavePath.Encrypted or SavePath.EncryptedSource ? Decrypt(saved) : saved;

        var check = QpdfReferenceTool.Check(Write(plain));
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"{label}: qpdf --check must pass: {check.Value.Output}");

        var dump = QpdfDump(plain);
        foreach (var marker in F.RemovedMarkers())
        {
            dump.Should().NotContain(marker, $"{label}: qpdf's dump of every object must not hold the old appearance {marker}");
            SavedPdfLeakScanner.FindTerm(plain, marker).Should().BeEmpty($"{label}: {marker}");
        }
        dump.Should().Contain(F.SharedMarker, $"{label}: SharedB still uses the shared appearance; copy-on-write left it alone");
        foreach (var field in ValueMustVanish)
        {
            dump.Should().NotContain(field.OldValue, $"{label}: {field.Name}'s old value");
            SavedPdfLeakScanner.FindTerm(plain, field.OldValue).Should().BeEmpty($"{label}: {field.Name}'s old value");
        }

        // What independent renderers DRAW from the appearances alone.
        var neutral = NeutraliseNeedAppearances(plain);
        var mutoolText = MutoolTextExtractor.ExtractPage(neutral, 1);
        mutoolText.Should().NotBeNull();
        foreach (var field in ValueMustVanish)
            mutoolText.Should().NotContain(field.OldValue, $"{label}: mutool must not draw {field.Name}'s old value");
        if (!clear)
        {
            foreach (var field in new[] { F.Name, F.Notes, F.Pick, F.Multi, F.SharedA })
                mutoolText.Should().Contain(field.NewValue, $"{label}: mutool draws {field.Name}'s regenerated appearance");
            if (PdftotextTextExtractor.IsAvailable)
                PdftotextTextExtractor.ExtractPage(neutral, 1).Should().Contain(F.Name.NewValue, $"{label}: Poppler draws the new value");
        }
        else
        {
            AssertNoInk(neutral, F.Name.Rect, $"{label}: a cleared field draws nothing");
            AssertNoInk(neutral, F.Notes.Rect, $"{label}: a cleared multiline field draws nothing");
        }
    }

    [Fact]
    public void Fixture_OldAppearanceDraws_WhenNeedAppearancesIsAbsent()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftoppmReferenceRenderer.IsAvailable, "pdftoppm not installed");
        var path = Write(F.Build(OldAppearanceText.KernedTj));
        MutoolTextExtractor.ExtractPage(path, 1).Should().Contain(F.Name.OldValue,
            "fixture sanity: the kerned TJ appearance draws the old value");
        using var poppler = PdftoppmReferenceRenderer.RenderPage(path, 1, Dpi);
        InkFractionIn(poppler!, F.Name.Rect).Should().BeGreaterThan(0.005, "fixture sanity: Poppler draws the old appearance");
    }

    /// <summary>
    /// The confirmed reproduction (#2017, 2026-10-08): a W-9 Acrobat filled with
    /// ALPHAOLD. Not committed (it holds whatever was typed into it); set
    /// <c>EXCISE_2017_ACROBAT_W9</c> to its path to run. The synthetic theory
    /// above carries the regression.
    /// </summary>
    [Fact]
    public void AcrobatFilledW9_ChangeName_OldValueAndAppearanceGone()
    {
        var file = Environment.GetEnvironmentVariable("EXCISE_2017_ACROBAT_W9");
        Assert.SkipWhen(string.IsNullOrEmpty(file) || !File.Exists(file),
            "EXCISE_2017_ACROBAT_W9 does not name an Acrobat-filled W-9 (a private file, not in the repo)");
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the independent oracle here (brew install qpdf)");
        const string field = "topmostSubform[0].Page1[0].f1_01[0]";
        var source = File.ReadAllBytes(file!);
        SavedPdfLeakScanner.FindTerm(source, "ALPHAOLD").Should().NotBeEmpty("the file was filled with ALPHAOLD");

        byte[] saved;
        using (var doc = PdfDocument.Open(source))
        {
            doc.GetAcroForm()!.FindField(field)!.SetValue("BETANEW");
            saved = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(saved, "ALPHAOLD").Should().BeEmpty();
        QpdfDump(saved).Should().NotContain("ALPHAOLD");
        var neutral = NeutraliseNeedAppearances(saved);
        MutoolTextExtractor.ExtractPage(neutral, 1).Should().Contain("BETANEW").And.NotContain("ALPHAOLD");
        if (PdftotextTextExtractor.IsAvailable)
            PdftotextTextExtractor.ExtractPage(neutral, 1).Should().Contain("BETANEW").And.NotContain("ALPHAOLD");
    }

    // ── saving ──────────────────────────────────────────────────────────

    private const string Password = "stale-appearance-test";

    private static void ChangeAll(PdfDocument doc, bool clear)
    {
        var form = doc.GetAcroForm()!;
        foreach (var field in F.Changed)
            form.FindField(field.Name)!.SetValue(clear ? null : field.NewValue);
    }

    private static byte[] Change(byte[] source, SavePath path, bool clear)
    {
        using var doc = PdfDocument.Open(source);
        ChangeAll(doc, clear);
        switch (path)
        {
            case SavePath.Encrypted:
                return doc.SaveToBytes(new PdfEncryptionOptions
                {
                    UserPassword = Password,
                    OwnerPassword = Password,
                    Algorithm = PdfEncryptionAlgorithm.Aes256,
                });
            case SavePath.ReopenResave:
            {
                using var reopened = PdfDocument.Open(doc.SaveToBytes());
                return reopened.SaveToBytes();
            }
            default:
                return doc.SaveToBytes();
        }
    }

    private byte[] ChangeEncryptedSource(byte[] source, bool clear)
    {
        var encryptedSource = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        QpdfReferenceTool.EncryptR4(Write(source), encryptedSource, Password, Password)
            .Should().BeTrue("qpdf must encrypt the fixture");
        using var doc = PdfDocument.Open(File.ReadAllBytes(encryptedSource), new PdfOpenOptions { UserPassword = Password });
        ChangeAll(doc, clear);
        var reEncrypt = doc.GetReEncryptionOptions(Password);
        reEncrypt.Should().NotBeNull("an encrypted source saves encrypted");
        var saved = doc.SaveToBytes(reEncrypt);
        QpdfReferenceTool.IsEncrypted(Write(saved)).Should().BeTrue("the round trip keeps the file encrypted");
        return saved;
    }

    // ── oracles ─────────────────────────────────────────────────────────

    private const int Dpi = 150;

    private string Write(byte[] bytes)
    {
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private string QpdfDump(byte[] saved) => CarrierTrapIndependentCorroborationTests.QpdfDump(Write(saved));

    private byte[] Decrypt(byte[] encrypted)
    {
        var output = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        QpdfReferenceTool.Decrypt(Write(encrypted), output, Password).Should().BeTrue("qpdf must decrypt excise's output");
        return File.ReadAllBytes(output);
    }

    /// <summary>
    /// qpdf rewrites the file uncompressed with no object streams; then
    /// <c>/NeedAppearances</c> is renamed to a same-length unknown key, so no
    /// offset moves and no renderer regenerates an appearance from <c>/V</c>.
    /// </summary>
    private string NeutraliseNeedAppearances(byte[] pdf)
    {
        var qdf = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        var input = Write(pdf);
        var psi = new System.Diagnostics.ProcessStartInfo("qpdf")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--qdf", "--object-streams=disable", input, qdf }) psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000).Should().BeTrue("qpdf --qdf must finish");
            p.ExitCode.Should().BeOneOf([0, 3], "qpdf --qdf must succeed (3 = warnings)");
        }

        var bytes = File.ReadAllBytes(qdf);
        var key = Encoding.ASCII.GetBytes("/NeedAppearances");
        var junk = Encoding.ASCII.GetBytes("/NeedAppearance0");
        for (var i = 0; i + key.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, key.Length).SequenceEqual(key))
                junk.CopyTo(bytes, i);
        var neutral = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-neutral.pdf");
        File.WriteAllBytes(neutral, bytes);
        return neutral;
    }

    private static void AssertNoInk(string path, double[] rect, string because)
    {
        using var mupdf = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        mupdf.Should().NotBeNull();
        InkFractionIn(mupdf!, rect).Should().BeLessThan(0.001, $"{because} (mutool)");
        if (!PdftoppmReferenceRenderer.IsAvailable) return;
        using var poppler = PdftoppmReferenceRenderer.RenderPage(path, 1, Dpi);
        poppler.Should().NotBeNull();
        InkFractionIn(poppler!, rect).Should().BeLessThan(0.001, $"{because} (Poppler)");
    }

    private static double InkFractionIn(SKBitmap bmp, double[] rect)
    {
        const double scale = Dpi / 72.0;
        const double pageHeight = 792;
        int x0 = Math.Max(0, (int)(rect[0] * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(rect[2] * scale));
        int y0 = Math.Max(0, (int)((pageHeight - rect[3]) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - rect[1]) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }
}
