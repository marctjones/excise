using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Security;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// CONSERVATION GATES (#945): for every mutating operation, assert what must
/// NOT change — not only what must.
///
/// The pattern behind the worst 2026-08 near-misses was one-directional
/// verification: every gate checked the property the operation was supposed
/// to CREATE (term gone, annotation added, page rotated) and nothing checked
/// the properties it might DESTROY (the rest of the text). #919's reverted
/// fix removed the term from every page — and ~2,200 characters of
/// neighbouring text — through 5,600+ green tests. #942 shipped for months.
/// A suite that only checks creation actively selects for changes that
/// achieve the goal by destroying the surroundings.
///
/// So each test here runs one mutating operation on real corpus fixtures and
/// asserts, via a NON-excise oracle (mutool for text, qpdf for annotation
/// inventory), that output = input − exactly the requested delta:
///
///   save round-trip        → per-page letter multiset identical, page count
///                            and annotation inventory identical
///   annotation add/remove  → page TEXT untouched; other annotations survive
///   page rotate            → per-page letter multiset identical
///   page move / remove     → pages are a pure permutation / exact subset
///   form fill / flatten    → no pre-existing letter disappears
///   encrypt round-trip     → per-page letter multiset identical
///   merge / split          → exact concatenation / partition of the sources
///
/// Redaction's conservation gate lives in RedactionCollateralHarness and
/// RedactionRemoteCollateralTests (#944) — not duplicated here.
///
/// Letter MULTISETS, not strings: the writer may legitimately reorder
/// operators or re-encode, and mutool's spacing differs across versions
/// (see mutool-bidi note in RtlOracleText). What no operation may do is make
/// a letter someone wrote disappear — that is exactly the #919 failure shape.
///
/// These are EXACT gates, no ratchet baseline: unlike redaction collateral
/// (where current behaviour has known cost), a rotate or an annotation add
/// that loses a letter is a defect with no legitimate version.
/// </summary>
public class ConservationGateTests
{
    /// <summary>
    /// Defects these gates found on their FIRST run, pinned so the suite stays
    /// green while the fixes are open. Each pinned row runs the real gate and
    /// asserts the defect still REPRODUCES — so the moment a fix lands, the
    /// pin fails loudly and the row must be deleted to arm the real gate.
    /// Never add a row here without an issue reference; a pin without an issue
    /// is a silent defect budget.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDefects = new()
    {
        // #961 — PageCollection.Move/RemoveAt index the ROOT /Kids by global
        // page number; on nested page trees RemoveAt silently deletes a whole
        // subtree (then mutool refuses the file) and Move throws.
        ["PageMove|scotus-trump-v-anderson.pdf"] = "#961",
        ["PageMove|irs-pub509-2026.pdf"] = "#961",
        ["PageRemove|scotus-trump-v-anderson.pdf"] = "#961",
        ["PageRemove|irs-pub509-2026.pdf"] = "#961",
        // #962 — flatten removes pushbutton widgets without stamping their
        // appearance, so the visible "Clear Form" label is destroyed.
        ["FormFlatten|state-ds11-passport.pdf"] = "#962",
        ["FormFlatten|state-ds82-passport-renewal.pdf"] = "#962",
    };

    /// <summary>
    /// Run <paramref name="gate"/> normally, unless (op, fixture) is a pinned
    /// known defect — then require that the gate still FAILS.
    /// </summary>
    private static void RunGate(string op, string fixtureName, Action gate)
    {
        if (!KnownDefects.TryGetValue($"{op}|{fixtureName}", out var issue))
        {
            gate();
            return;
        }

        var reproduced = false;
        try { gate(); }
        catch (Exception e) when (!e.GetType().Name.Contains("Skip", StringComparison.Ordinal))
        {
            reproduced = true;
        }
        reproduced.Should().BeTrue(
            "{0} on {1} is pinned as known defect {2}; the gate now PASSES, so the defect appears " +
            "FIXED — delete this KnownDefects row to arm the real gate (and close {2} if this was its last pin)",
            op, fixtureName, issue);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// Smoke fixtures up to 20 pages. The two long ones (irs-1040-instructions,
    /// 126 pages; scotus-trump-v-us, ~100 pages) are excluded by the page cap —
    /// each test row costs two full mutool extractions per fixture and the long
    /// fixtures add minutes without adding an operator shape the short ones
    /// lack. This is a deliberate, named cap, not silent truncation; the
    /// redaction collateral harness covers the long fixtures for the operation
    /// where size genuinely matters.
    /// </summary>
    private const int MaxPages = 20;

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in EnumerateFixtures()) data.Add(Path.GetFileName(f));
        if (data.Count == 0) data.Add("(no corpus)");
        return data;
    }

    private static IEnumerable<string> EnumerateFixtures()
    {
        var dir = Resolve("test-pdfs/smoke");
        if (dir == null) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.pdf").OrderBy(x => x, StringComparer.Ordinal))
        {
            int pages;
            try
            {
                using var doc = PdfDocument.Open(File.ReadAllBytes(f));
                pages = doc.PageCount;
            }
            catch { continue; }
            if (pages is > 0 and <= MaxPages) yield return f;
        }
    }

    private static string? Resolve(string rel)
    {
        for (var up = 0; up < 6; up++)
        {
            var p = Path.GetFullPath(Path.Combine(Enumerable.Repeat("..", up).DefaultIfEmpty(".").Aggregate(Path.Combine), rel));
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    private static string RequireFixture(string fixtureName)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipWhen(fixtureName == "(no corpus)", "smoke corpus not present");
        var path = EnumerateFixtures().FirstOrDefault(f => Path.GetFileName(f) == fixtureName);
        Assert.SkipWhen(path == null, "fixture not found");
        return path!;
    }

    // ---------------------------------------------------------------- oracle

    private static string[] ExtractPages(string pdfPath, string? password = null)
    {
        int pageCount;
        using (var doc = PdfDocument.Open(File.ReadAllBytes(pdfPath), password, allowEncrypted: false))
            pageCount = doc.PageCount;

        if (password == null)
        {
            var pages = MutoolTextExtractor.ExtractAllPages(pdfPath, pageCount);
            pages.Should().NotBeNull("mutool must be able to read {0}", pdfPath);
            return pages!;
        }

        var result = new string[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            var text = MutoolTextExtractor.ExtractPage(pdfPath, i + 1, password);
            text.Should().NotBeNull("mutool must be able to read page {0} of {1}", i + 1, pdfPath);
            result[i] = text!;
        }
        return result;
    }

    private static Dictionary<char, int> LetterMultiset(string text)
    {
        var set = new Dictionary<char, int>();
        foreach (var c in text.Where(char.IsLetterOrDigit))
            set[c] = set.TryGetValue(c, out var n) ? n + 1 : 1;
        return set;
    }

    /// <summary>Human-readable multiset delta: what disappeared / appeared.</summary>
    private static string MultisetDelta(Dictionary<char, int> before, Dictionary<char, int> after)
    {
        var lost = new List<string>();
        var gained = new List<string>();
        foreach (var kv in before)
        {
            var have = after.GetValueOrDefault(kv.Key);
            if (have < kv.Value) lost.Add($"'{kv.Key}'×{kv.Value - have}");
        }
        foreach (var kv in after)
        {
            var had = before.GetValueOrDefault(kv.Key);
            if (kv.Value > had) gained.Add($"'{kv.Key}'×{kv.Value - had}");
        }
        return $"lost [{string.Join(", ", lost.Take(20))}{(lost.Count > 20 ? ", …" : "")}] " +
               $"gained [{string.Join(", ", gained.Take(20))}{(gained.Count > 20 ? ", …" : "")}]";
    }

    private static void AssertSameLetters(string[] before, string[] after, string operation)
    {
        after.Length.Should().Be(before.Length, "{0} must not change the page count", operation);
        for (var i = 0; i < before.Length; i++)
        {
            var b = LetterMultiset(before[i]);
            var a = LetterMultiset(after[i]);
            var same = b.Count == a.Count && b.All(kv => a.GetValueOrDefault(kv.Key) == kv.Value);
            same.Should().BeTrue(
                "{0} must not change any letter on page {1}, but the letter multiset changed: {2}",
                operation, i + 1, MultisetDelta(b, a));
        }
    }

    private static void AssertNoLetterLost(string[] before, string[] after, string operation)
    {
        after.Length.Should().Be(before.Length, "{0} must not change the page count", operation);
        for (var i = 0; i < before.Length; i++)
        {
            var b = LetterMultiset(before[i]);
            var a = LetterMultiset(after[i]);
            var lost = b.Where(kv => a.GetValueOrDefault(kv.Key) < kv.Value).ToList();
            lost.Should().BeEmpty(
                "{0} may add text but must not DESTROY any pre-existing letter on page {1}; {2}",
                operation, i + 1, MultisetDelta(b, a));
        }
    }

    private static string TempPdf() =>
        Path.Combine(Path.GetTempPath(), $"excise-conservation-{Guid.NewGuid():N}.pdf");

    // ---------------------------------------------------------------- gates

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SaveRoundTrip_ChangesNothing(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            var annotationsBefore = QpdfReferenceTool.IsAvailable
                ? QpdfReferenceTool.ListAnnotations(path) : null;

            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                doc.Save(output);

            AssertSameLetters(before, ExtractPages(output), "an idle open→save round-trip");

            if (annotationsBefore != null)
            {
                var annotationsAfter = QpdfReferenceTool.ListAnnotations(output);
                annotationsAfter.Should().NotBeNull("qpdf must be able to read the saved file");
                annotationsAfter!.Select(x => x.Subtype).OrderBy(x => x).Should().Equal(
                    annotationsBefore.Select(x => x.Subtype).OrderBy(x => x),
                    "an idle round-trip must not add or drop annotations");
            }
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AnnotationAdd_DestroysNoPageText(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            int countBefore;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                countBefore = doc.GetPage(1).GetAnnotations().Count;
                doc.AddTextAnnotation(1, new PdfRectangle(50, 50, 86, 86), "conservation gate note");
                doc.AddSquareAnnotation(1, new PdfRectangle(100, 100, 200, 160));
                doc.Save(output);
            }

            // Sticky-note /Contents lives in the annotation, not the page
            // content stream — page TEXT must be byte-for-byte conserved.
            // (FreeText, which paints into an appearance stream mutool can
            // extract, is deliberately not used here for that reason.)
            AssertSameLetters(before, ExtractPages(output), "adding annotations");

            using (var doc = PdfDocument.Open(File.ReadAllBytes(output)))
                doc.GetPage(1).GetAnnotations().Count.Should().Be(countBefore + 2,
                    "both added annotations must exist and no pre-existing one may vanish");
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AnnotationRemove_LeavesTextAndOtherAnnotations(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var withTwo = TempPdf();
        var output = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                doc.AddTextAnnotation(1, new PdfRectangle(50, 50, 86, 86), "the one to remove");
                doc.AddSquareAnnotation(1, new PdfRectangle(100, 100, 200, 160));
                doc.Save(withTwo);
            }

            var before = ExtractPages(withTwo);
            int countBefore;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(withTwo)))
            {
                var page = doc.GetPage(1);
                countBefore = page.GetAnnotations().Count;
                var victim = page.GetAnnotations().First(a => a.Subtype == PdfAnnotationSubtype.Text);
                doc.RemoveAnnotation(1, victim).Should().BeTrue("the annotation we just added must be removable");
                doc.Save(output);
            }

            AssertSameLetters(before, ExtractPages(output), "removing one annotation");

            using (var doc = PdfDocument.Open(File.ReadAllBytes(output)))
            {
                var page = doc.GetPage(1);
                page.GetAnnotations().Count.Should().Be(countBefore - 1,
                    "exactly the one requested annotation may disappear");
                page.GetAnnotations().Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Square,
                    "the annotation that was NOT removed must survive");
            }
        }
        finally { File.Delete(withTwo); File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void PageRotate_DestroysNoLetters(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                var page = doc.GetPage(1);
                page.Rotation = (page.Rotation + 90) % 360;
                doc.Save(output);
            }
            AssertSameLetters(before, ExtractPages(output), "rotating a page");
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void PageMove_IsAPurePermutation(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            Assert.SkipWhen(before.Length < 2, "single-page fixture — nothing to move");

            RunGate("PageMove", fixtureName, () =>
            {
                using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                {
                    doc.Pages.Move(0, doc.PageCount - 1);
                    doc.Save(output);
                }

                var expected = before.Skip(1).Append(before[0]).ToArray();
                AssertSameLetters(expected, ExtractPages(output), "moving a page (compared page-for-page against the expected permutation)");
            });
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void PageRemove_TakesExactlyThatPage(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            Assert.SkipWhen(before.Length < 2, "single-page fixture — removing it leaves nothing to check");

            RunGate("PageRemove", fixtureName, () =>
            {
                using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                {
                    doc.Pages.RemoveAt(0);
                    doc.Save(output);
                }

                AssertSameLetters(before.Skip(1).ToArray(), ExtractPages(output),
                    "removing page 1 (every OTHER page must survive untouched)");
            });
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FormFill_DestroysNoExistingText(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                var field = doc.GetAcroForm()?.GetTextFields().FirstOrDefault();
                Assert.SkipWhen(field == null, "fixture has no text field to fill");
                field!.SetValue("CONSERVATIONGATE");
                doc.Save(output);
            }
            // Filling ADDS the value's appearance; the gate is that nothing
            // pre-existing disappears. (Whether the value renders is the
            // create-property — other suites own it.)
            AssertNoLetterLost(before, ExtractPages(output), "filling one form field");
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FormFlatten_DestroysNoExistingText(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            using (var probe = PdfDocument.Open(File.ReadAllBytes(path)))
                Assert.SkipWhen(probe.GetAcroForm() == null, "fixture has no form to flatten");

            var before = ExtractPages(path);
            RunGate("FormFlatten", fixtureName, () =>
            {
                using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                {
                    AcroFormFlattener.Flatten(doc, doc.GetAcroForm()!);
                    doc.Save(output);
                }
                AssertNoLetterLost(before, ExtractPages(output), "flattening the form");
            });
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EncryptRoundTrip_ChangesNoText(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var output = TempPdf();
        try
        {
            var before = ExtractPages(path);
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                doc.Save(output, new PdfEncryptionOptions { UserPassword = "gate-pass" });

            AssertSameLetters(before, ExtractPages(output, password: "gate-pass"),
                "encrypting on save");
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Merge_IsExactConcatenation(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var others = EnumerateFixtures().Where(f => Path.GetFileName(f) != fixtureName).ToList();
        Assert.SkipWhen(others.Count == 0, "need a second fixture to merge with");
        var other = others[0];
        var output = TempPdf();
        try
        {
            var beforeA = ExtractPages(path);
            var beforeB = ExtractPages(other);

            using (var docA = PdfDocument.Open(File.ReadAllBytes(path)))
            using (var docB = PdfDocument.Open(File.ReadAllBytes(other)))
            using (var merged = PdfDocumentMerger.Merge(new (PdfDocument, IReadOnlyList<int>)[]
            {
                (docA, Enumerable.Range(0, docA.PageCount).ToList()),
                (docB, Enumerable.Range(0, docB.PageCount).ToList()),
            }))
            {
                merged.Save(output);
            }

            AssertSameLetters(beforeA.Concat(beforeB).ToArray(), ExtractPages(output),
                $"merging {fixtureName} + {Path.GetFileName(other)} (must be the exact concatenation)");
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Split_IsExactPartition(string fixtureName)
    {
        var path = RequireFixture(fixtureName);
        var outputs = new List<string>();
        try
        {
            var before = ExtractPages(path);
            Assert.SkipWhen(before.Length < 3, "too few pages for a meaningful split");

            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            {
                var chunks = PdfDocumentSplitter.SplitEveryNPages(doc, 2);
                foreach (var chunk in chunks)
                {
                    var p = TempPdf();
                    outputs.Add(p);
                    chunk.Save(p);
                    chunk.Dispose();
                }
            }

            var reassembled = outputs.SelectMany(p => ExtractPages(p)).ToArray();
            AssertSameLetters(before, reassembled,
                "splitting every 2 pages (chunks must reassemble to exactly the source)");
        }
        finally { foreach (var p in outputs) File.Delete(p); }
    }
}
