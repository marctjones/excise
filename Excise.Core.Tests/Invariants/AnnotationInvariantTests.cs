using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Invariants;

/// <summary>
/// #933 — judge annotations by STRUCTURE, because for many of them the picture
/// has no right answer.
///
/// ISO 32000-1 §12.5.5 says a reader SHOULD generate an appearance for an
/// annotation with no <c>/AP</c> — not that it must — and says nothing about
/// what a Sound or FileAttachment icon looks like. The oracles prove the
/// latitude is real: mutool draws Redact/Sound/FileAttachment and not
/// Line/Ink/PolyLine/Link, and pdftocairo does the reverse (#889). So a pixel
/// comparison has nothing to compare against, and the corpus gate reporting
/// those pages as excise defects is a scoring artefact (#932).
///
/// That does not make an annotation unverifiable. It moves the question from
/// "does it look the same?" to "is it actually there, correct, and preserved?",
/// and NOTHING IN THE SUITE ASKED THAT before this file. The existing
/// annotation tests — including the ones I wrote for #912 — assert the subtype
/// on the saved document and little else, so excise could author an inverted
/// /Rect, quadpoints outside their rect, or lose /Contents entirely and every
/// test would still pass.
///
/// These need no oracle: they are properties an annotation must satisfy to be
/// conformant, checkable from excise's own output.
/// </summary>
public class AnnotationInvariantTests
{
    /// <summary>
    /// Every subtype the authoring API can produce, with the arguments it
    /// needs. Adding a subtype to Core without adding it here should be
    /// noticed — see <see cref="EverySubtypeCoreCanAuthor_IsCoveredHere"/>.
    /// </summary>
    public static TheoryData<string> AuthorableSubtypes() => new()
    {
        "Highlight", "Underline", "StrikeOut", "Squiggly",
        "Square", "Circle", "FreeText", "Text", "Ink",
    };

    private static PdfAnnotation Author(PdfDocument doc, string subtype, PdfRectangle rect, string contents) =>
        subtype switch
        {
            "Highlight" => doc.AddHighlightAnnotation(1, rect, contents),
            "Underline" => doc.AddUnderlineAnnotation(1, rect, contents),
            "StrikeOut" => doc.AddStrikeOutAnnotation(1, rect, contents),
            "Squiggly" => doc.AddSquigglyAnnotation(1, rect, contents),
            "Square" => doc.AddSquareAnnotation(1, rect, contents),
            "Circle" => doc.AddCircleAnnotation(1, rect, contents),
            "FreeText" => doc.AddFreeTextAnnotation(1, rect, contents),
            "Text" => doc.AddTextAnnotation(1, rect, contents),
            "Ink" => doc.AddInkAnnotation(1, InkStrokesIn(rect), contents),
            _ => throw new ArgumentOutOfRangeException(nameof(subtype), subtype, "not wired into this test"),
        };

    /// <summary>
    /// The requested subtype is what lands. Trivial-looking, and it is the one
    /// property the existing tests DO check — kept so this file is a complete
    /// statement of the contract rather than a supplement to be read alongside.
    /// </summary>
    [Theory]
    [MemberData(nameof(AuthorableSubtypes))]
    public void AuthoredAnnotation_HasTheRequestedSubtype(string subtype)
    {
        using var doc = NewDocument();
        Author(doc, subtype, Box, "structural check");

        doc.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype.ToString() == subtype);
    }

    /// <summary>
    /// A rectangle must be non-degenerate. An inverted or zero-area /Rect is a
    /// conformance defect that renders as nothing — invisible in a pixel test,
    /// which is exactly the gap this file exists for.
    /// </summary>
    [Theory]
    [MemberData(nameof(AuthorableSubtypes))]
    public void AuthoredAnnotation_HasANonDegenerateRect(string subtype)
    {
        using var doc = NewDocument();
        Author(doc, subtype, Box, "structural check");

        var a = Only(doc, subtype);
        var r = a.Rect.Normalize();
        r.Width.Should().BeGreaterThan(0, $"{subtype}: a zero-width /Rect renders as nothing");
        r.Height.Should().BeGreaterThan(0, $"{subtype}: a zero-height /Rect renders as nothing");
    }

    /// <summary>
    /// Supplied text must survive. An annotation whose /Contents is silently
    /// dropped looks identical in every visual comparison and is useless to the
    /// person who typed it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AuthorableSubtypes))]
    public void AuthoredAnnotation_PreservesItsContents(string subtype)
    {
        const string text = "structural check — keep me";
        using var doc = NewDocument();
        Author(doc, subtype, Box, text);

        (Only(doc, subtype).Contents ?? "").Should().Contain("keep me",
            $"{subtype}: /Contents must carry the text the caller supplied");
    }

    /// <summary>
    /// Text-markup quadpoints describe the marked region and must lie within
    /// the annotation's own /Rect (§12.5.6.10). Quadpoints outside it are a
    /// defect no renderer comparison surfaces, because viewers clip or ignore
    /// them differently.
    /// </summary>
    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    [InlineData("Squiggly")]
    public void TextMarkup_QuadPointsLieWithinTheRect(string subtype)
    {
        using var doc = NewDocument();
        Author(doc, subtype, Box, "structural check");

        var a = Only(doc, subtype);
        a.QuadPoints.Should().NotBeNull($"{subtype} is a text-markup annotation and must carry /QuadPoints");

        var rect = a.Rect.Normalize();
        foreach (var q in a.QuadPoints!)
        {
            var qn = q.Normalize();
            qn.Left.Should().BeGreaterThanOrEqualTo(rect.Left - Tolerance, $"{subtype}: quad left outside /Rect");
            qn.Right.Should().BeLessThanOrEqualTo(rect.Right + Tolerance, $"{subtype}: quad right outside /Rect");
            qn.Bottom.Should().BeGreaterThanOrEqualTo(rect.Bottom - Tolerance, $"{subtype}: quad bottom outside /Rect");
            qn.Top.Should().BeLessThanOrEqualTo(rect.Top + Tolerance, $"{subtype}: quad top outside /Rect");
        }
    }

    /// <summary>
    /// THE ROUND TRIP — the invariant most likely to catch a real regression,
    /// and the one #923's coming writer rewrite makes urgent.
    ///
    /// Whatever excise wrote, excise must read back with the same subtype, the
    /// same rectangle and the same text. A writer that drops an annotation
    /// dictionary entry is invisible to every test that only inspects the
    /// in-memory document.
    /// </summary>
    [Theory]
    [MemberData(nameof(AuthorableSubtypes))]
    public void AuthoredAnnotation_SurvivesASaveAndReloadUnchanged(string subtype)
    {
        const string text = "round trip — keep me";
        var tmp = Path.Combine(Path.GetTempPath(), $"excise-annot-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfRectangle before;
            using (var doc = NewDocument())
            {
                Author(doc, subtype, Box, text);
                before = Only(doc, subtype).Rect.Normalize();
                doc.Save(tmp);
            }

            using var reloaded = PdfDocument.Open(tmp);
            var after = Only(reloaded, subtype);

            after.Subtype.ToString().Should().Be(subtype, $"{subtype}: subtype must survive a save");
            (after.Contents ?? "").Should().Contain("keep me", $"{subtype}: /Contents must survive a save");

            var r = after.Rect.Normalize();
            r.Left.Should().BeApproximately(before.Left, Tolerance, $"{subtype}: /Rect moved across a save");
            r.Bottom.Should().BeApproximately(before.Bottom, Tolerance, $"{subtype}: /Rect moved across a save");
            r.Width.Should().BeApproximately(before.Width, Tolerance, $"{subtype}: /Rect resized across a save");
            r.Height.Should().BeApproximately(before.Height, Tolerance, $"{subtype}: /Rect resized across a save");
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>
    /// INK GEOMETRY SURVIVES A SAVE — point for point, in order.
    ///
    /// This is the Ink-shaped version of the trap row C fell into: an
    /// `/InkList` that is non-empty, bounded by a sane `/Rect` and of the right
    /// subtype can still have its points reversed, decimated or rounded into
    /// a different drawing. "An Ink annotation exists" cannot tell the
    /// difference; comparing the actual coordinates can.
    ///
    /// Deliberately an ASYMMETRIC, non-monotonic polyline — a symmetric or
    /// monotonic stroke reads the same reversed, so it would pass the very
    /// mutation this test exists to catch.
    /// </summary>
    [Fact]
    public void Ink_PreservesEveryPointInOrderAcrossASave()
    {
        var stroke = new List<(double X, double Y)>
        {
            (100, 600), (140, 655), (150, 610), (210, 648), (300, 604),
        };

        var tmp = Path.Combine(Path.GetTempPath(), $"excise-ink-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = NewDocument())
            {
                doc.AddInkAnnotation(1, new[] { stroke }, "ink geometry");
                doc.Save(tmp);
            }

            using var reloaded = PdfDocument.Open(tmp);
            var ink = Only(reloaded, "Ink");

            ink.InkStrokes.Should().NotBeNull("an Ink annotation without /InkList draws nothing");
            ink.InkStrokes!.Should().HaveCount(1, "one stroke in, one stroke out");

            var after = ink.InkStrokes[0];
            after.Should().HaveCount(stroke.Count,
                "every point must survive — a decimated stroke is still a plausible-looking drawing");

            for (var i = 0; i < stroke.Count; i++)
            {
                after[i].X.Should().BeApproximately(stroke[i].X, Tolerance,
                    $"point {i} X moved across a save (reordering or rounding)");
                after[i].Y.Should().BeApproximately(stroke[i].Y, Tolerance,
                    $"point {i} Y moved across a save (reordering or rounding)");
            }

            // /Rect must contain the ink it bounds. An /InkList outside its own
            // /Rect is clipped away by conforming viewers — invisible to a
            // subtype assertion, and invisible to a pixel test that only ever
            // renders through the same wrong rect.
            var r = ink.Rect.Normalize();
            foreach (var (x, y) in after)
            {
                x.Should().BeInRange(r.Left - Tolerance, r.Right + Tolerance, "ink X outside /Rect");
                y.Should().BeInRange(r.Bottom - Tolerance, r.Top + Tolerance, "ink Y outside /Rect");
            }
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Guards the theory data itself. A subtype added to the authoring API but
    /// not to <see cref="AuthorableSubtypes"/> is silently unverified — the
    /// vacuity failure mode that makes corpus-driven tests lie.
    /// </summary>
    [Fact]
    public void EverySubtypeCoreCanAuthor_IsCoveredHere()
    {
        var covered = new HashSet<string>(StringComparer.Ordinal)
        {
            "Highlight", "Underline", "StrikeOut", "Squiggly",
            "Square", "Circle", "FreeText", "Text", "Ink",
        };

        // Deliberately NOT covered, with reasons — each needs arguments this
        // fixture does not supply (endpoints, vertices, strokes, image data).
        var deferred = new HashSet<string>(StringComparer.Ordinal)
        {
            "Line", "Arrow", "Polygon", "PolyLine", "Stamp", "ImageStamp",
        };

        var authorable = typeof(PdfAnnotationAuthoring)
            .GetMethods()
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Add", StringComparison.Ordinal) && n.EndsWith("Annotation", StringComparison.Ordinal))
            .Select(n => n["Add".Length..^"Annotation".Length])
            .Distinct()
            .ToList();

        var uncovered = authorable.Where(s => !covered.Contains(s) && !deferred.Contains(s)).ToList();
        uncovered.Should().BeEmpty(
            "every subtype the authoring API can produce must be structurally verified here or " +
            "listed as deferred with a reason — otherwise a new subtype ships unverified (#933)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const double Tolerance = 0.5;
    private static PdfRectangle Box => new(72, 600, 300, 660);

    /// <summary>A two-point diagonal stroke inside <paramref name="rect"/>.</summary>
    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> InkStrokesIn(PdfRectangle rect)
    {
        var r = rect.Normalize();
        return new[]
        {
            new[] { (r.Left + 2, r.Bottom + 2), (r.Right - 2, r.Top - 2) },
        };
    }

    private static PdfDocument NewDocument()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        return doc;
    }

    private static PdfAnnotation Only(PdfDocument doc, string subtype) =>
        doc.GetPage(1).GetAnnotations().Single(a => a.Subtype.ToString() == subtype);
}
