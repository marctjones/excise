using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #933 — annotations verified by a parser that is NOT excise.
///
/// Every other annotation test in this repo asks excise what excise wrote. That
/// establishes the writer and the reader agree, which they would even if both
/// were wrong in the same way — they share a codebase, a coordinate convention
/// and an author's assumptions. It is the identical shape of blindness
/// CLAUDE.md records three redaction leaks for:
///
///   "A tool must not be its own oracle for the property it exists to
///    guarantee. excise confirming that excise removed the text proves only
///    that its bugs are self-consistent."
///
/// The visual half of that rule is already in place (mutool, pdftocairo,
/// Ghostscript). This is the STRUCTURAL half, and it is the half annotations
/// actually need: §12.5.5 lets a viewer synthesise any appearance it likes for
/// an annotation without /AP, so for much of this surface there is no correct
/// picture to compare — but there is always a correct OBJECT.
///
/// qpdf parses the bytes with its own implementation and reports the object
/// graph (<c>--json=1 --json-key=objects</c>). Agreement here is evidence about
/// the FILE.
/// </summary>
public class AnnotationStructuralOracleTests
{
    /// <summary>
    /// Every subtype excise's GUI can now author (#934), authored, saved, and
    /// then read back by qpdf rather than by excise.
    /// </summary>
    public static TheoryData<string> Subtypes() => new()
    {
        "Highlight", "Underline", "StrikeOut", "Squiggly",
        "Square", "Circle", "FreeText", "Text",
        "Ink", "Line", "Polygon", "PolyLine", "Stamp",
    };

    [Theory]
    [MemberData(nameof(Subtypes))]
    public void AuthoredAnnotation_IsSeenByQpdfsOwnParser(string subtype)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var tmp = NewPath();
        try
        {
            // The expectation is what the CALLER ASKED FOR, computed here — NOT
            // read back from the annotation excise returned.
            //
            // This is the whole difference between an independent check and a
            // decorated self-check, and I got it wrong first: comparing against
            // `annot.Rect` passed happily with a mutation that shifted every
            // written /Rect by 50pt, because excise's in-memory view and the
            // bytes it wrote moved together. An oracle fed excise's own answer
            // is not an oracle.
            var expected = RequestedBounds(subtype);
            using (var doc = NewDocument())
            {
                Author(doc, subtype);
                doc.Save(tmp);
            }

            var seen = QpdfReferenceTool.ListAnnotations(tmp);
            seen.Should().NotBeNull("qpdf is available, so it must have produced a readable answer");

            // Stamp and ImageStamp are both /Stamp; everything else maps 1:1.
            var match = seen!.Where(a => a.Subtype == subtype).ToList();
            match.Should().HaveCount(1,
                $"an INDEPENDENT parser must find exactly one {subtype} — excise saying it wrote one " +
                $"is not evidence about the file. qpdf saw: [{string.Join(", ", seen.Select(a => a.Subtype))}]");

            // /Rect must CONTAIN what was asked for (Core pads geometry-derived
            // rects by half the stroke width so round caps stay inside) and must
            // not wander far beyond it. Stated as a band rather than an equality
            // so the assertion survives a legitimate padding rule while still
            // failing on a shifted or mis-scaled rect.
            const double slack = 4.0;
            var q = match[0];
            q.Left.Should().BeInRange(expected.Left - slack, expected.Left + 0.5,
                $"{subtype}: qpdf sees a /Rect left that is not where the caller asked");
            q.Bottom.Should().BeInRange(expected.Bottom - slack, expected.Bottom + 0.5,
                $"{subtype}: qpdf sees a /Rect bottom that is not where the caller asked");
            q.Right.Should().BeInRange(expected.Right - 0.5, expected.Right + slack,
                $"{subtype}: qpdf sees a /Rect right that is not where the caller asked");
            q.Top.Should().BeInRange(expected.Top - 0.5, expected.Top + slack,
                $"{subtype}: qpdf sees a /Rect top that is not where the caller asked");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// The geometry-bearing subtypes, counted by qpdf.
    ///
    /// A count is a weak assertion in isolation and a strong one here: it is
    /// made by a parser with no access to the list excise built, so a stroke
    /// excise thinks it wrote and did not serialise cannot pass.
    /// </summary>
    [Fact]
    public void GeometryBearingAnnotations_HaveTheirGeometrySeenByQpdf()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                doc.AddInkAnnotation(1, new[]
                {
                    new[] { (100.0, 600.0), (140.0, 655.0), (150.0, 610.0) },
                    new[] { (200.0, 600.0), (240.0, 640.0) },
                }, "two strokes");
                doc.AddPolygonAnnotation(1, new[]
                {
                    (100.0, 400.0), (200.0, 500.0), (300.0, 400.0), (240.0, 370.0),
                }, "four vertices");
                doc.AddArrowAnnotation(1, 100, 300, 300, 340, "arrow");
                doc.Save(tmp);
            }

            var seen = QpdfReferenceTool.ListAnnotations(tmp);
            seen.Should().NotBeNull();

            seen!.Single(a => a.Subtype == "Ink").InkStrokeCount.Should().Be(2,
                "qpdf must count both pen-down..pen-up strokes in /InkList");
            seen.Single(a => a.Subtype == "Polygon").VertexCount.Should().Be(4,
                "qpdf must count every vertex — a dropped one is a different shape");
            seen.Single(a => a.Subtype == "Line").EndLineEnding.Should().Be("ClosedArrow",
                "an arrow is a Line whose /LE an independent parser can see");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// GUARDS THE ORACLE ITSELF. A helper that silently returns nothing would
    /// make every assertion above vacuous — the classic way an oracle-backed
    /// suite goes green while checking nothing.
    ///
    /// So: a file with no annotations must yield an EMPTY list (not null, not a
    /// phantom), and a file with annotations must not.
    /// </summary>
    [Fact]
    public void TheOracleDistinguishesNoAnnotationsFromNotBeingAsked()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var bare = NewPath();
        var annotated = NewPath();
        try
        {
            using (var doc = NewDocument()) doc.Save(bare);
            using (var doc = NewDocument())
            {
                doc.AddSquareAnnotation(1, new PdfRectangle(72, 600, 300, 660), "one");
                doc.Save(annotated);
            }

            var none = QpdfReferenceTool.ListAnnotations(bare);
            none.Should().NotBeNull("qpdf is available — 'could not ask' must not be confused with 'none found'");
            none!.Should().BeEmpty("this file genuinely has no annotations");

            QpdfReferenceTool.ListAnnotations(annotated)
                .Should().ContainSingle(a => a.Subtype == "Square",
                    "and the same call must find one when there is one — otherwise every " +
                    "assertion in this file passes by finding nothing");
        }
        finally { TryDelete(bare); TryDelete(annotated); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly PdfRectangle Box = new(72, 600, 300, 660);

    private static PdfAnnotation Author(PdfDocument doc, string subtype) => subtype switch
    {
        "Highlight" => doc.AddHighlightAnnotation(1, Box, "x"),
        "Underline" => doc.AddUnderlineAnnotation(1, Box, "x"),
        "StrikeOut" => doc.AddStrikeOutAnnotation(1, Box, "x"),
        "Squiggly" => doc.AddSquigglyAnnotation(1, Box, "x"),
        "Square" => doc.AddSquareAnnotation(1, Box, "x"),
        "Circle" => doc.AddCircleAnnotation(1, Box, "x"),
        "FreeText" => doc.AddFreeTextAnnotation(1, Box, "x"),
        "Text" => doc.AddTextAnnotation(1, Box, "x"),
        "Stamp" => doc.AddStampAnnotation(1, Box, "Confidential"),
        "Ink" => doc.AddInkAnnotation(1, new[] { new[] { (80.0, 610.0), (290.0, 650.0) } }, "x"),
        "Line" => doc.AddLineAnnotation(1, 80, 610, 290, 650, "x"),
        "Polygon" => doc.AddPolygonAnnotation(1, new[] { (80.0, 610.0), (200.0, 655.0), (290.0, 610.0) }, "x"),
        "PolyLine" => doc.AddPolyLineAnnotation(1, new[] { (80.0, 610.0), (200.0, 655.0), (290.0, 610.0) }, "x"),
        _ => throw new ArgumentOutOfRangeException(nameof(subtype), subtype, "not wired into this test"),
    };

    /// <summary>
    /// The bounds the caller REQUESTED for each subtype, restated here as
    /// literals. Deliberately not derived from excise's output — see the note
    /// in the test above.
    /// </summary>
    private static PdfRectangle RequestedBounds(string subtype) => subtype switch
    {
        // Geometry-derived rects: the bounding box of the points passed to Author.
        "Ink" or "Line" => new PdfRectangle(80, 610, 290, 650),
        "Polygon" or "PolyLine" => new PdfRectangle(80, 610, 290, 655),
        // Everything else takes the rect it is given.
        _ => Box.Normalize(),
    };

    private static PdfDocument NewDocument()
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        return doc;
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"excise-qpdf-annot-{Guid.NewGuid():N}.pdf");

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}
