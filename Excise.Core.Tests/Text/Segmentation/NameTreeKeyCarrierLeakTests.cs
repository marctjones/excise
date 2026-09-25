using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1852: a term in a name-tree key (<c>/Names /Dests /Names [(KESTREL chapter) …]</c>)
/// or a legacy <c>/Dests</c> key survived <c>RedactText</c> and <c>ScrubTerms</c>,
/// unreported. A destination is found by its name, so masking the key must keep
/// the tree valid (§7.9.6: sorted, unique) and keep every reference landing where
/// it did. The saved bytes are read by <see cref="SavedPdfLeakScanner"/>; navigation
/// is checked by mutool in <c>NavigationCarrierOracleTests</c>.
/// </summary>
public class NameTreeKeyCarrierLeakTests
{
    private const string Latin = "KESTREL";
    private const string Arabic = "سلام";
    private const string Redacted = "[redacted]";

    public enum Entry { RedactText, ScrubTerms }

    private static string Utf16Hex(string text) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string Utf16Octal(string text) =>
        @"(\376\377" + string.Concat(Encoding.BigEndianUnicode.GetBytes(text)
            .Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + ")";

    /// <summary>The key as PDF source, its term, and what Strip leaves.</summary>
    private static (string Key, string Term, string Stripped) Key(string shape) => shape switch
    {
        "literal" => ($"({Latin} chapter)", Latin, "chapter"),
        "utf16-hex" => (Utf16Hex($"{Latin} chapter"), Latin, "chapter"),
        "utf16-octal" => (Utf16Octal($"{Latin} chapter"), Latin, "chapter"),
        "arabic" => (Utf16Hex($"{Arabic} chapter"), Arabic, "chapter"),
        "partial-word" => ($"(sec.X{Latin}Y.2)", Latin, "sec.XY.2"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>
    /// One destination named <paramref name="key"/>, reached by name from a
    /// bookmark (/Dest), a link (GoTo /D), the page's /AA and the /OpenAction.
    /// </summary>
    private static byte[] Referenced(string key) => CarrierTrapFixtures.WithCatalog(
        $"/Names << /Dests << /Names [{key} [3 0 R /XYZ 0 700 0]] >> >> /Outlines 6 0 R " +
        $"/OpenAction << /S /GoTo /D {key} >>",
        page: $"/Annots [8 0 R] /AA << /O << /S /GoTo /D {key} >> >>",
        extra: new[]
        {
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            $"<< /Title (Chapter) /Parent 6 0 R /Dest {key} >>",
            $"<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /A << /S /GoTo /D {key} >> >>",
        });

    public static TheoryData<string, RedactionProfile, Entry> Matrix()
    {
        var data = new TheoryData<string, RedactionProfile, Entry>();
        foreach (var shape in new[] { "literal", "utf16-hex", "utf16-octal", "arabic", "partial-word" })
            foreach (var profile in new[] { RedactionProfile.Standard, RedactionProfile.Maximum })
                foreach (var entry in new[] { Entry.RedactText, Entry.ScrubTerms })
                    data.Add(shape, profile, entry);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void DestinationKey_IsMaskedEverywhereItIsNamed(string shape, RedactionProfile profile, Entry entry)
    {
        var (key, term, stripped) = Key(shape);
        var pdf = Referenced(key);
        SavedPdfLeakScanner.FindTerm(pdf, term).Should().NotBeEmpty("input-side control");
        var options = RedactionOptions.ForProfile(profile);
        var expected = profile == RedactionProfile.Maximum ? Redacted : stripped;

        using var document = PdfDocument.Open(pdf);
        if (entry == Entry.RedactText)
        {
            document.RedactText(term, options).Carriers
                .Should().ContainSingle(c => c.Carrier == "/Names and /Dests keys")
                .Which.Scrubbed.Should().BeTrue();
        }
        else
        {
            PdfDocumentSanitizer.ScrubTerms(document, new[] { term }, caseSensitive: false,
                    RedactionCarriers.All, options.CarrierPolicy)
                .For(RedactionCarriers.NameTreeKeys)
                .Should().BeEquivalentTo(new { TermFound = true, Modified = true, RefusedReason = (string?)null });
        }

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, term).Should().BeEmpty($"{shape}: no key or reference may keep the term");

        using var reopened = PdfDocument.Open(saved);
        var destination = reopened.GetNamedDestinations().Should().ContainSingle().Which.Value;
        destination.Name.Should().Be(expected);
        destination.PageNumber.Should().Be(1);
        destination.Y.Should().Be(700, "the destination itself is untouched");

        var references = NamedReferences(reopened).ToList();
        references.Should().NotBeEmpty();
        references.Should().OnlyContain(r => r == expected, "every reference names the renamed key");
        if (entry == Entry.ScrubTerms || profile == RedactionProfile.Standard)
        {
            references.Should().HaveCount(4, "bookmark, link, page /AA and /OpenAction all name it");
            PdfOutlineParser.Parse(reopened).Single().PageNumber.Should().Be(1);
        }
    }

    /// <summary>
    /// The masked key leaves its leaf's range: "chapter" (0x63) sorts after
    /// "Zulu" (0x5A). Editing the leaf in place would leave the tree unsorted
    /// and its /Limits wrong.
    /// </summary>
    [Fact]
    public void MaskedKeyThatLeavesItsLeaf_KeepsTheTreeSortedAndEveryNameOnItsDestination()
    {
        var pdf = CarrierTrapFixtures.WithCatalog("/Names << /Dests 6 0 R >> /Outlines 9 0 R", extra: new[]
        {
            "<< /Kids [7 0 R 8 0 R] >>",
            $"<< /Limits [(Alpha) ({Latin} chapter)] /Names [(Alpha) [3 0 R /XYZ 0 100 0] ({Latin} chapter) [3 0 R /XYZ 0 200 0]] >>",
            "<< /Limits [(Mango) (Zulu)] /Names [(Mango) [3 0 R /XYZ 0 300 0] (Zulu) [3 0 R /XYZ 0 400 0]] >>",
            "<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 1 >>",
            $"<< /Title (Chapter) /Parent 9 0 R /Dest ({Latin} chapter) >>",
        });
        using var document = PdfDocument.Open(pdf);

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Latin).Should().BeEmpty("neither a key nor a /Limits bound may keep it");
        using var reopened = PdfDocument.Open(saved);
        var root = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(reopened.Catalog.GetOptional("Names")!))
            .GetOptional("Dests")!);
        root.ContainsKey("Kids").Should().BeFalse();
        root.ContainsKey("Limits").Should().BeFalse("a root has no /Limits (§7.9.6)");
        var keys = PdfNameTree.Enumerate(reopened, root).Select(p => (PdfString)reopened.Resolve(p.Key)).ToList();
        keys.Select(k => k.Value).Should().Equal("Alpha", "Mango", "Zulu", "chapter");
        keys.Select(k => k.Bytes).Should().BeInAscendingOrder(
            Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)), "keys sort by their bytes");

        var dests = reopened.GetNamedDestinations();
        dests.ToDictionary(d => d.Key, d => d.Value.Y).Should().BeEquivalentTo(new Dictionary<string, double?>
        {
            ["Alpha"] = 100, ["chapter"] = 200, ["Mango"] = 300, ["Zulu"] = 400,
        });
        NamedReferences(reopened).Should().Equal("chapter");
    }

    [Fact]
    public void MaskedKeyThatCollides_IsMadeUnique_AndEachReferenceKeepsItsDestination()
    {
        var pdf = CarrierTrapFixtures.WithCatalog(
            $"/Names << /Dests << /Names [({Latin} chapter) [3 0 R /XYZ 0 100 0] (chapter) [3 0 R /XYZ 0 200 0]] >> >> /Outlines 6 0 R",
            page: "/Annots [8 0 R]",
            extra: new[]
            {
                "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
                $"<< /Title (Chapter) /Parent 6 0 R /Dest ({Latin} chapter) >>",
                "<< /Type /Annot /Subtype /Link /Rect [72 600 372 620] /Dest (chapter) >>",
            });
        using var document = PdfDocument.Open(pdf);

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Latin).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        reopened.GetNamedDestinations().ToDictionary(d => d.Key, d => d.Value.Y).Should()
            .BeEquivalentTo(new Dictionary<string, double?> { ["chapter"] = 200, ["chapter 2"] = 100 });
        var outline = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(reopened.Catalog.GetOptional("Outlines")!))
            .GetOptional("First")!);
        ((PdfString)outline.GetOptional("Dest")!).Value.Should().Be("chapter 2");
        var link = (PdfDictionary)reopened.Resolve(((PdfArray)reopened.GetPage(1).Dictionary.GetOptional("Annots")!)[0]);
        ((PdfString)link.GetOptional("Dest")!).Value.Should().Be("chapter", "a key that held no term keeps its name");
    }

    [Fact]
    public void LegacyDestsKey_IsRenamed_AndANameReferenceFollowsIt()
    {
        var pdf = CarrierTrapFixtures.WithCatalog($"/Dests << /{Latin}#20chapter [3 0 R /XYZ 0 100 0] >> /Outlines 6 0 R",
            extra: new[]
            {
                "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
                $"<< /Title (Chapter) /Parent 6 0 R /Dest /{Latin}#20chapter >>",
            });
        using var document = PdfDocument.Open(pdf);

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Latin).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        var dests = (PdfDictionary)reopened.Resolve(reopened.Catalog.GetOptional("Dests")!);
        dests.Keys.Select(k => k.Value).Should().Equal("chapter");
        var outline = (PdfDictionary)reopened.Resolve(((PdfDictionary)reopened.Resolve(reopened.Catalog.GetOptional("Outlines")!))
            .GetOptional("First")!);
        outline.GetOptional("Dest").Should().BeOfType<PdfName>().Which.Value.Should().Be("chapter");
        PdfOutlineParser.Parse(reopened).Single().PageNumber.Should().Be(1);
    }

    [Fact]
    public void OtherTrees_KeepTheirValues_UnderAMaskedKey()
    {
        var pdf = CarrierTrapFixtures.WithCatalog(
            $"/Names << /JavaScript << /Names [({Latin} init) 6 0 R] >> /Templates << /Names [({Latin} form) 7 0 R] >> " +
            $"/AP << /Names [({Latin} icon) 8 0 R] >> >>",
            extra: new[]
            {
                "<< /S /JavaScript /JS (var ready = true;) >>",
                "<< /Type /Template /Marker (form value) >>",
                "<< /Marker (icon value) >>",
            });
        using var document = PdfDocument.Open(pdf);

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        var saved = document.SaveToBytes();
        SavedPdfLeakScanner.FindTerm(saved, Latin).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        var names = (PdfDictionary)reopened.Resolve(reopened.Catalog.GetOptional("Names")!);
        foreach (var (tree, key, value) in new[] { ("JavaScript", "init", "var ready"), ("Templates", "form", "form value"), ("AP", "icon", "icon value") })
        {
            var (k, v) = PdfNameTree.Enumerate(reopened, names.GetOptional(tree)).Single();
            ((PdfString)reopened.Resolve(k)).Value.Should().Be(key);
            SavedPdfLeakScanner.AllCarriersText(saved).Should().Contain(value, $"/{tree}: the value survives its key's rename");
            reopened.Resolve(v).Should().BeOfType<PdfDictionary>();
        }
    }

    [Fact]
    public void AKeyTheCutWouldReform_BecomesThePlaceholder()
    {
        using var document = PdfDocument.Open(Referenced("(KESKESTRELTREL)"));

        PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin });

        SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Latin).Should().BeEmpty();
        document.GetNamedDestinations().Keys.Should().Equal(Redacted);
    }

    [Fact]
    public void EmbeddedFilesKey_RemovesTheAttachment_AndNoLimitsBoundKeepsIt()
    {
        // The key names the attachment; /F, /Desc and the bytes do not hold the term.
        var byKey = CarrierTrapFixtures.WithCatalog($"/Names << /EmbeddedFiles << /Names [({Latin}.txt) 6 0 R] >> >>",
            extra: new[] { "<< /Type /Filespec /F (note.txt) /EF << /F 7 0 R >> >>", Stream("hello") });
        // A two-level tree: the pair goes with its attachment, and the leaf's /Limits must not keep the key.
        var byLimits = CarrierTrapFixtures.WithCatalog("/Names << /EmbeddedFiles 6 0 R >>", extra: new[]
        {
            "<< /Kids [7 0 R] >>",
            $"<< /Limits [({Latin}.txt) ({Latin}.txt)] /Names [({Latin}.txt) 8 0 R] >>",
            $"<< /Type /Filespec /F (note.txt) /Desc (about {Latin}) /EF << /F 9 0 R >> >>",
            Stream("hello"),
        });

        foreach (var pdf in new[] { byKey, byLimits })
        {
            using var document = PdfDocument.Open(pdf);
            var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin }, caseSensitive: false,
                RedactionCarriers.All, CarrierScrubPolicy.Default);

            outcome.For(RedactionCarriers.EmbeddedFiles)!.Modified.Should().BeTrue();
            SavedPdfLeakScanner.FindTerm(document.SaveToBytes(), Latin).Should().BeEmpty();
            document.GetEmbeddedFiles().Should().BeEmpty();
        }
    }

    [Fact]
    public void ReportOnly_LeavesTheKeyAndSaysItHoldsTheTerm()
    {
        using var document = PdfDocument.Open(Referenced($"({Latin} chapter)"));

        var outcome = PdfDocumentSanitizer.ScrubTerms(document, new[] { Latin }, caseSensitive: false,
            RedactionCarriers.All,
            CarrierScrubPolicy.Default.With(RedactionCarriers.NameTreeKeys, CarrierScrubMode.ReportOnly));

        outcome.NeedingAttention.Should().ContainSingle(r => r.Carrier == RedactionCarriers.NameTreeKeys && r.TermFound);
        document.GetNamedDestinations().Keys.Should().Equal($"{Latin} chapter");
        NamedReferences(document).Should().OnlyContain(r => r == $"{Latin} chapter");
    }

    /// <summary>
    /// The planted failure: with the carrier switched off the key keeps the term,
    /// and both audits, which do not share the scrub's code, say so.
    /// </summary>
    [Fact]
    public void Audits_SeeAKeyTheScrubLeft_AndNotOneItMasked()
    {
        var (key, term, _) = Key("utf16-octal");

        using (var leaking = PdfDocument.Open(Referenced(key)))
        {
            leaking.RedactText(term,
                    RedactionOptions.Default with { Carriers = RedactionCarriers.All & ~RedactionCarriers.NameTreeKeys })
                .Carriers.Single(c => c.Carrier == "/Names and /Dests keys").Scrubbed.Should().BeFalse();
            SavedPdfLeakScanner.FindTerm(leaking.SaveToBytes(), term).Should().NotBeEmpty("the planted failure");

            RedactionCarrierAudit.Inspect(leaking, new[] { term }).NameTreeKeyCount.Should().Be(1);
            CarrierTextRecovery.Scan(leaking).Should().Contain(f =>
                f.Carrier == "name-tree key /Dests" && f.Text.Contains(term, StringComparison.Ordinal));
        }

        using var scrubbed = PdfDocument.Open(Referenced(key));
        scrubbed.RedactText(term, RedactionOptions.Default);
        RedactionCarrierAudit.Inspect(scrubbed, new[] { term }).NameTreeKeyCount.Should().Be(0);
        CarrierTextRecovery.Scan(scrubbed).Should().NotContain(f => f.Text.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>The safe-copy pass the GUI runs warns about a key its own scrub was told to skip.</summary>
    [Fact]
    public void SafeCopy_WarnsAboutAKeyItsScrubSkipped_AndNotOneItMasked()
    {
        var pdf = Referenced($"({Latin} chapter)");
        static IReadOnlyList<string> Warnings(byte[] pdf, RedactionCarriers carriers)
        {
            using var document = PdfDocument.Open(pdf);
            return RedactedCopySafetyPolicy.Evaluate(document, RedactedCopySafetyRequest.ForTerms(
                new[] { Latin }, RedactionOptions.Default with { Carriers = carriers })).Warnings;
        }

        Warnings(pdf, RedactionCarriers.All & ~RedactionCarriers.NameTreeKeys)
            .Should().Contain(w => w.Contains("name-tree key", StringComparison.Ordinal));
        Warnings(pdf, RedactionCarriers.All)
            .Should().NotContain(w => w.Contains("name-tree key", StringComparison.Ordinal));
    }

    [Fact]
    public void AreaAudit_DoesNotCountKeys_WithoutATerm()
    {
        using var document = PdfDocument.Open(Referenced("(page.1)"));

        RedactionCarrierAudit.Inspect(document).NameTreeKeyCount.Should().Be(0,
            "keys are identifiers; with no term there is nothing to test them against");
    }

    /// <summary>Every destination NAME in a /Dest, /OpenAction or GoTo /D slot, read at the known locations.</summary>
    private static IEnumerable<string> NamedReferences(PdfDocument document)
    {
        static string? Named(PdfDocument d, PdfObject? slot) => d.Resolve(slot ?? PdfNull.Instance) switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value,
            _ => null,
        };
        PdfDictionary? Dict(PdfObject? o) => document.Resolve(o ?? PdfNull.Instance) as PdfDictionary;

        var found = new List<string?>
        {
            Named(document, Dict(document.Catalog.GetOptional("OpenAction"))?.GetOptional("D")),
            Named(document, Dict(Dict(document.GetPage(1).Dictionary.GetOptional("AA"))?.GetOptional("O"))?.GetOptional("D")),
            Named(document, Dict(Dict(document.Catalog.GetOptional("Outlines"))?.GetOptional("First"))?.GetOptional("Dest")),
        };
        if (document.Resolve(document.GetPage(1).Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
            foreach (var annot in annots.Select(Dict))
                found.Add(Named(document, annot?.GetOptional("Dest")) ?? Named(document, Dict(annot?.GetOptional("A"))?.GetOptional("D")));
        return found.OfType<string>();
    }

    private static string Stream(string data) => $"<< /Type /EmbeddedFile /Length {data.Length} >>\nstream\n{data}\nendstream";
}
