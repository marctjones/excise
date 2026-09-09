using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1188 / #1169 — the per-carrier scrub MODE.
///
/// <para><b>Why this is a security test and not an options test.</b> One policy
/// for every carrier is wrong in both directions. On free-form prose, cutting
/// the term out is right. On a KNOWN string it can hand the term back: strip
/// <c>your</c> from <c>https://www.irs.gov/your-account</c> and the residue
/// <c>https://www.irs.gov/-account</c> tells anyone who knows the site exactly
/// what was removed. The redaction leaks the secret it was performed for.</para>
///
/// <para>These tests pin all three modes, INCLUDING the fact that the default
/// still strips — a default flip is a product decision, not something this
/// change makes silently (#1187 requires defaults to reproduce prior
/// behaviour).</para>
/// </summary>
public sealed class CarrierScrubPolicyTests
{
    private const string KnownUrl = "https://www.irs.gov/your-account";
    private const string Term = "your";

    // A page with one word, plus a link annotation whose /A /URI is a URL whose
    // structure is public knowledge — the #1169 reveal case, in one fixture.
    private static byte[] LinkedPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>",
        Stream("", "BT /F1 12 Tf 72 700 Td (yourself) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /Annot /Subtype /Link /Rect [72 100 300 120] " +
        $"/A << /S /URI /URI ({KnownUrl}) >> >>");

    private static string? UriOf(PdfDocument doc)
    {
        var annots = doc.Resolve(doc.GetPage(1).Dictionary.GetOptional("Annots")!)
            as Excise.Core.Primitives.PdfArray;
        var annot = doc.Resolve(annots![0]) as Excise.Core.Primitives.PdfDictionary;
        var action = doc.Resolve(annot!.GetOptional("A")!) as Excise.Core.Primitives.PdfDictionary;
        return (doc.Resolve(action!.GetOptional("URI") ?? Excise.Core.Primitives.PdfNull.Instance)
            as Excise.Core.Primitives.PdfString)?.Value;
    }

    [Fact]
    public void Strip_IsStillTheDefault_AndLeavesTheRevealingResidue()
    {
        // The behaviour #1169 was filed about, pinned as a FACT rather than
        // quietly changed. If a future change flips the default, this test is
        // where that decision has to be made deliberately.
        using var doc = PdfDocument.Open(LinkedPdf());
        doc.RedactText(Term, RedactionOptions.Default);

        UriOf(doc).Should().Be("https://www.irs.gov/-account",
            "Strip cuts the substring out and leaves the surrounding known URL, " +
            "from which the removed word can be read straight back (#1169)");
    }

    [Fact]
    public void RemoveWhole_DropsTheEntireUri_SoNoStructureSurvivesToInferFrom()
    {
        using var doc = PdfDocument.Open(LinkedPdf());
        var report = doc.RedactText(Term, RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Default
                .With(RedactionCarriers.ActionUris, CarrierScrubMode.RemoveWhole),
        });

        UriOf(doc).Should().BeNull("RemoveWhole drops the whole /URI, not just the term");
        report.Carriers.Single(c => c.Carrier == "link /A /URI").Scrubbed.Should().BeTrue();

        // Carrier-agnostic: nothing anywhere in the saved file, compressed
        // streams included, still names the account path (CLAUDE.md assertion 1).
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), "irs.gov").Should().BeEmpty(
            "the whole URL value is gone, so its fixed structure cannot be used " +
            "to reconstruct the redacted word");
    }

    [Fact]
    public void ReportOnly_ChangesNothing_AndSaysTheCarrierStillHoldsTheTerm()
    {
        // ⚠️ The dangerous mode. It MUST NOT be reportable as scrubbed: a mode
        // that leaves the term and claims success is the exact shape that
        // shipped three leaks past a green suite.
        using var doc = PdfDocument.Open(LinkedPdf());
        var report = doc.RedactText(Term, RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Default
                .With(RedactionCarriers.ActionUris, CarrierScrubMode.ReportOnly),
        });

        UriOf(doc).Should().Be(KnownUrl, "ReportOnly does not modify the document");

        var row = report.Carriers.Single(c => c.Carrier == "link /A /URI");
        row.Scrubbed.Should().BeFalse("nothing was scrubbed");
        row.RefusedReason.Should().Contain("HOLDS THE TERM",
            "the user has to be told the term survived, not merely that a mode was set");
        report.IsCleanSuccess.Should().BeFalse(
            "a run with a carrier still holding the term is not a clean success");
    }

    [Fact]
    public void ReportOnly_WithoutAHit_SaysSo_RatherThanImplyingALeak()
    {
        using var doc = PdfDocument.Open(LinkedPdf());
        var report = doc.RedactText("NOTPRESENTANYWHERE", RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Uniform(CarrierScrubMode.ReportOnly),
        });

        var row = report.Carriers.Single(c => c.Carrier == "link /A /URI");
        row.Scrubbed.Should().BeFalse("nothing needed scrubbing");
        row.RefusedReason.Should().BeNull(
            "a ReportOnly carrier that does not hold the term is a CLEAN outcome; " +
            "flagging it would report a leak-free run as unclean");
        report.IsCleanSuccess.Should().BeTrue(
            "nothing survived and no carrier was refused");
    }

    [Fact]
    public void RefusalOnAnUnnamedCarrier_StillReachesTheReport()
    {
        // #1188: RedactText names five carriers in its summary. A mode refused
        // on one of the others (here XFA, which has no RemoveWhole) must not be
        // dropped just because the carrier is absent from that list — silently
        // skipping it is exactly what the carrier policy forbids.
        var xfaPdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /XFA 6 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("", "BT ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("", "<xdp><field>yourself</field></xdp>"));

        using var doc = PdfDocument.Open(xfaPdf);
        var report = doc.RedactText(Term, RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Uniform(CarrierScrubMode.RemoveWhole),
        });

        report.Carriers.Should().Contain(c => c.RefusedReason != null && c.RefusedReason.Contains("XFA"),
            "the refusal is surfaced even though XFA is not one of the five named carriers");
        report.IsCleanSuccess.Should().BeFalse("a refused carrier is not a clean success");
    }

    [Fact]
    public void NoRefusalRow_ForACarrierTheDocumentDoesNotHave()
    {
        // The other half: a refusal on a carrier that is not present is noise,
        // and noise is what trains people to ignore refusal rows.
        using var doc = PdfDocument.Open(LinkedPdf());   // no XFA
        var report = doc.RedactText(Term, RedactionOptions.Default with
        {
            CarrierPolicy = CarrierScrubPolicy.Uniform(CarrierScrubMode.RemoveWhole),
        });

        report.Carriers.Should().NotContain(c => c.RefusedReason != null && c.RefusedReason.Contains("XFA"));
    }

    [Fact]
    public void DefaultPolicy_ChangesNothingVersusThePreOptionCall()
    {
        // #1187's rule: constructing the defaults changes nothing.
        using var withPolicy = PdfDocument.Open(LinkedPdf());
        PdfDocumentSanitizer.ScrubTerms(
            withPolicy, new[] { Term }, caseSensitive: false,
            RedactionCarriers.All, CarrierScrubPolicy.Default);

        using var withoutPolicy = PdfDocument.Open(LinkedPdf());
        PdfDocumentSanitizer.ScrubTerms(withoutPolicy, new[] { Term });

        UriOf(withPolicy).Should().Be(UriOf(withoutPolicy));

        // Not raw byte equality: MEASURED here, two identical legacy scrubs
        // already differ (at index 672) because the writer stamps a fresh
        // trailer /ID per save (§14.4). Identical bytes is therefore not a
        // property this writer HAS, and asserting it would fail for a reason
        // that has nothing to do with policy.
        //
        // Assert the security-relevant property instead, carrier-agnostically:
        // the term survives in exactly the same places in both saved files —
        // compressed streams included (CLAUDE.md assertion 1).
        SavedPdfLeakScanner.FindTerm(withPolicy.SaveToBytes(), Term)
            .Should().Equal(SavedPdfLeakScanner.FindTerm(withoutPolicy.SaveToBytes(), Term));
    }

    [Fact]
    public void ScrubTerms_ReportsPerCarrierWhatRan()
    {
        using var doc = PdfDocument.Open(LinkedPdf());
        var outcome = PdfDocumentSanitizer.ScrubTerms(
            doc, new[] { Term }, caseSensitive: false, RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.ActionUris, CarrierScrubMode.ReportOnly));

        var uri = outcome.For(RedactionCarriers.ActionUris)!;
        uri.Mode.Should().Be(CarrierScrubMode.ReportOnly);
        uri.TermFound.Should().BeTrue();
        uri.Modified.Should().BeFalse();
        outcome.NeedingAttention.Should().Contain(uri);
    }

    [Fact]
    public void DisabledCarrier_IsNotEvenExamined_SoItGetsNoRow()
    {
        // Scope (Carriers) and mode (CarrierPolicy) are orthogonal: OFF means
        // "not looked at", ReportOnly means "looked at, left alone, told you".
        using var doc = PdfDocument.Open(LinkedPdf());
        var outcome = PdfDocumentSanitizer.ScrubTerms(
            doc, new[] { Term }, caseSensitive: false,
            RedactionCarriers.All & ~RedactionCarriers.ActionUris,
            CarrierScrubPolicy.Default);

        outcome.For(RedactionCarriers.ActionUris).Should().BeNull();
        UriOf(doc).Should().Be(KnownUrl, "a disabled carrier is untouched");
    }

    [Fact]
    public void RemoveWhole_OnXfa_IsRefusedWithAReason_NotSilentlyDowngraded()
    {
        // The carrier policy: surface, don't guess. An XFA packet is one XML
        // form; there is no "the value the term was in" to drop, so the mode is
        // refused out loud rather than quietly executed as Strip.
        var xfaPdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /XFA 6 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("", "BT ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("", "<xdp><field>yourself</field></xdp>"));

        using var doc = PdfDocument.Open(xfaPdf);
        var outcome = PdfDocumentSanitizer.ScrubTerms(
            doc, new[] { Term }, caseSensitive: false, RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.Xfa, CarrierScrubMode.RemoveWhole));

        var xfa = outcome.For(RedactionCarriers.Xfa)!;
        xfa.RefusedReason.Should().NotBeNull();
        xfa.Modified.Should().BeFalse();
    }

    [Fact]
    public void RemoveWhole_EmptiesTheWholeInfoField_RatherThanCuttingTheTerm()
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("", "BT ET"),
            "<< /Title (Case 2024-your-1234 filing) >>",
            infoObjectIndex: 5);

        using var strip = PdfDocument.Open(pdf);
        PdfDocumentSanitizer.ScrubTerms(strip, new[] { Term });
        (strip.Resolve(strip.Info!.GetOptional("Title")!) as Excise.Core.Primitives.PdfString)!
            .Value.Should().Be("Case 2024--1234 filing",
                "Strip leaves the structured template around the hole");

        using var whole = PdfDocument.Open(pdf);
        PdfDocumentSanitizer.ScrubTerms(whole, new[] { Term }, caseSensitive: false,
            RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.Info, CarrierScrubMode.RemoveWhole));
        whole.Info!.ContainsKey("Title").Should().BeFalse(
            "RemoveWhole drops the whole field, so the surrounding template " +
            "cannot narrow what was removed");
    }

    [Fact]
    public void Policy_HasValueSemantics()
    {
        CarrierScrubPolicy.Default.Should().Be(CarrierScrubPolicy.Uniform(CarrierScrubMode.Strip));
        CarrierScrubPolicy.Default.ModeFor(RedactionCarriers.Info).Should().Be(CarrierScrubMode.Strip);

        var one = CarrierScrubPolicy.Default.With(RedactionCarriers.Info, CarrierScrubMode.ReportOnly);
        one.Should().NotBe(CarrierScrubPolicy.Default, "With() must not mutate in place");
        one.ModeFor(RedactionCarriers.Info).Should().Be(CarrierScrubMode.ReportOnly);
        one.ModeFor(RedactionCarriers.Xmp).Should().Be(CarrierScrubMode.Strip);

        // A combination sets every flag it names.
        var two = CarrierScrubPolicy.Default.With(
            RedactionCarriers.Info | RedactionCarriers.Xmp, CarrierScrubMode.RemoveWhole);
        two.ModeFor(RedactionCarriers.Info).Should().Be(CarrierScrubMode.RemoveWhole);
        two.ModeFor(RedactionCarriers.Xmp).Should().Be(CarrierScrubMode.RemoveWhole);
        two.ModeFor(RedactionCarriers.Outlines).Should().Be(CarrierScrubMode.Strip);

        CarrierScrubPolicy.AllCarriers.Should().HaveCount(10, "every RedactionCarriers flag is covered");
    }

    private static string Stream(string dictExtra, string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {bytes.Length} >>\nstream\n{content}\nendstream";
    }

    private static byte[] Build(params string[] bodies) => BuildCore(bodies, infoObjectIndex: 0);

    // Overload used by the /Info fixture: names which object is the /Info dict.
    private static byte[] Build(
        string a, string b, string c, string d, string e, int infoObjectIndex) =>
        BuildCore(new[] { a, b, c, d, e }, infoObjectIndex);

    private static byte[] BuildCore(string[] bodies, int infoObjectIndex)
    {
        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        var info = infoObjectIndex > 0 ? $" /Info {infoObjectIndex} 0 R" : "";
        Write($"trailer\n<< /Root 1 0 R{info} /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
