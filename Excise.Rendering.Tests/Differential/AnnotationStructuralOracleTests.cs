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
    public static TheoryData<string> Subtypes()
    {
        var data = new TheoryData<string>();
        foreach (var subtype in SubtypeNames) data.Add(subtype);
        return data;
    }

    /// <summary>
    /// Backing list for <see cref="Subtypes"/>, kept as a plain array so
    /// <see cref="EverySubtypeCoreCanAuthor_IsAlsoJudgedByTheIndependentOracle"/>
    /// can compare it against the authoring API without reaching into xunit's
    /// theory-row representation.
    /// </summary>
    private static readonly string[] SubtypeNames =
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

    /// <summary>
    /// TEXT-MARKUP QUADPOINTS, READ BY QPDF (§12.5.6.10).
    ///
    /// <c>AnnotationInvariantTests.TextMarkup_QuadPointsLieWithinTheRect</c>
    /// already asserts containment — but it reads the quads back through
    /// excise's own annotation reader, so a writer that emits them in the wrong
    /// units, in the wrong order, or not at all is only caught if excise's
    /// reader disagrees with excise's writer. It does not; they share a
    /// codebase. Here the quads are the ones QPDF FOUND IN THE BYTES, compared
    /// against the region the CALLER ASKED FOR, restated as a literal.
    ///
    /// Bounds, not corner sequence: producers legitimately differ on quad-point
    /// corner ORDER (Acrobat's ordering famously departs from the spec's), so
    /// asserting the sequence would pin an excise implementation detail rather
    /// than a property of a correct annotation.
    /// </summary>
    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    [InlineData("Squiggly")]
    public void TextMarkup_QuadPointsAreSeenByQpdf_CoveringTheRequestedRegion(string subtype)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var expected = Box.Normalize();
        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                Author(doc, subtype, "quadpoints");
                doc.Save(tmp);
            }

            var q = Seen(tmp, subtype);

            q.QuadPoints.Should().NotBeNull(
                $"{subtype} is a text-markup annotation; without /QuadPoints a conforming viewer " +
                "has no marked region to draw (§12.5.6.10) — and excise's own reader agreeing that " +
                "they are there is not evidence that they were written");
            q.QuadPoints!.Count.Should().Be(8,
                $"{subtype}: one quadrilateral is 8 numbers — qpdf counted {q.QuadPoints.Count}");

            var xs = q.QuadPoints.Where((_, i) => i % 2 == 0).ToList();
            var ys = q.QuadPoints.Where((_, i) => i % 2 == 1).ToList();

            // Inside the requested box…
            xs.Min().Should().BeGreaterThanOrEqualTo(expected.Left - Tolerance, $"{subtype}: quad X left of the requested region");
            xs.Max().Should().BeLessThanOrEqualTo(expected.Right + Tolerance, $"{subtype}: quad X right of the requested region");
            ys.Min().Should().BeGreaterThanOrEqualTo(expected.Bottom - Tolerance, $"{subtype}: quad Y below the requested region");
            ys.Max().Should().BeLessThanOrEqualTo(expected.Top + Tolerance, $"{subtype}: quad Y above the requested region");

            // …and actually COVERING it. Containment alone is satisfied by a
            // collapsed quad, which marks nothing while passing every bound.
            (xs.Max() - xs.Min()).Should().BeApproximately(expected.Width, Tolerance,
                $"{subtype}: the quad must span the requested width, not merely fit inside it");
            (ys.Max() - ys.Min()).Should().BeApproximately(expected.Height, Tolerance,
                $"{subtype}: the quad must span the requested height");

            // And inside the annotation's own /Rect, as qpdf read both.
            xs.Min().Should().BeGreaterThanOrEqualTo(q.Left - Tolerance, $"{subtype}: quad escapes its own /Rect");
            xs.Max().Should().BeLessThanOrEqualTo(q.Right + Tolerance, $"{subtype}: quad escapes its own /Rect");
            ys.Min().Should().BeGreaterThanOrEqualTo(q.Bottom - Tolerance, $"{subtype}: quad escapes its own /Rect");
            ys.Max().Should().BeLessThanOrEqualTo(q.Top + Tolerance, $"{subtype}: quad escapes its own /Rect");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// THE CALLER'S TEXT, READ BACK BY QPDF.
    ///
    /// /Contents loss is the defect with no visual signature at all: a note
    /// whose text was dropped renders exactly like one whose text is intact,
    /// on every renderer, at every zoom.
    ///
    /// The marker is deliberately ASCII. excise writes non-ASCII /Contents as
    /// UTF-16BE with a BOM and qpdf 12 decodes that correctly (verified while
    /// writing this), but pinning the assertion to a decoding behaviour of the
    /// oracle's own string handling would make the test report an excise defect
    /// when qpdf changes how it renders strings in JSON. The property under
    /// test is "the text survived", not "qpdf transcodes the way I expect".
    /// </summary>
    [Theory]
    [MemberData(nameof(Subtypes))]
    public void AuthoredAnnotation_ContentsAreSeenByQpdf(string subtype)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        const string text = "structural oracle - keep me";
        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                Author(doc, subtype, text);
                doc.Save(tmp);
            }

            (Seen(tmp, subtype).Contents ?? "").Should().Contain("keep me",
                $"{subtype}: a parser that is not excise must find the caller's text in /Contents");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// THE ROUND TRIP, WITH EXCISE NEVER VOUCHING FOR IT.
    ///
    /// author → save A → excise opens A → save B, then qpdf reads BOTH files
    /// and the two readings must agree. The existing round-trip test
    /// (<c>AnnotationInvariantTests.AuthoredAnnotation_SurvivesASaveAndReloadUnchanged</c>)
    /// reloads through excise's own reader, so a reader that drops an entry AND
    /// a writer that never wrote it are indistinguishable to it. Here the
    /// before/after judgement is made entirely outside excise: excise's only
    /// role is to be the thing that opened and re-saved the file.
    ///
    /// This is the "judge the delta" shape (#944/#945) applied to annotations —
    /// what the operation DESTROYS, measured by an independent oracle on both
    /// sides, rather than what it creates.
    /// </summary>
    [Theory]
    [MemberData(nameof(Subtypes))]
    public void AuthoredAnnotation_SurvivesAnExciseReopenAndResave_AsQpdfReadsBothFiles(string subtype)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var first = NewPath();
        var second = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                Author(doc, subtype, "round trip - keep me");
                doc.Save(first);
            }

            using (var reopened = PdfDocument.Open(first))
            {
                reopened.Save(second);
            }

            var before = Seen(first, subtype);
            var after = Seen(second, subtype);

            after.Subtype.Should().Be(before.Subtype, $"{subtype}: the subtype changed across an excise reopen+resave");
            after.Left.Should().BeApproximately(before.Left, Tolerance, $"{subtype}: /Rect left moved across a reopen+resave");
            after.Bottom.Should().BeApproximately(before.Bottom, Tolerance, $"{subtype}: /Rect bottom moved");
            after.Right.Should().BeApproximately(before.Right, Tolerance, $"{subtype}: /Rect right moved");
            after.Top.Should().BeApproximately(before.Top, Tolerance, $"{subtype}: /Rect top moved");

            (after.Contents ?? "").Should().Be(before.Contents ?? "",
                $"{subtype}: /Contents changed across an excise reopen+resave");
            after.EndLineEnding.Should().Be(before.EndLineEnding, $"{subtype}: /LE changed across a reopen+resave");

            SameNumbers(after.QuadPoints, before.QuadPoints, $"{subtype}: /QuadPoints");
            SameNumbers(after.Vertices, before.Vertices, $"{subtype}: /Vertices");
            SameNumbers(after.LineEndpoints, before.LineEndpoints, $"{subtype}: /L");

            if (before.InkStrokes == null)
            {
                after.InkStrokes.Should().BeNull($"{subtype}: an /InkList appeared out of nowhere");
            }
            else
            {
                after.InkStrokes.Should().NotBeNull($"{subtype}: /InkList was dropped by a reopen+resave");
                after.InkStrokes!.Count.Should().Be(before.InkStrokes.Count, $"{subtype}: a stroke was lost");
                for (var i = 0; i < before.InkStrokes.Count; i++)
                    SameNumbers(after.InkStrokes[i], before.InkStrokes[i], $"{subtype}: /InkList stroke {i}");
            }

            if (before.NormalAppearance == null)
            {
                after.NormalAppearance.Should().BeNull(
                    $"{subtype}: an /AP appeared across a reopen+resave, which qpdf could not read before");
            }
            else
            {
                after.NormalAppearance.Should().NotBeNull(
                    $"{subtype}: the baked /AP /N was lost across a reopen+resave — the annotation would " +
                    "fall back to whatever each viewer invents, which is the interoperability #626 removed");
                after.NormalAppearance!.BBoxWidth.Should().BeApproximately(
                    before.NormalAppearance.BBoxWidth, Tolerance, $"{subtype}: /AP /N /BBox width changed");
                after.NormalAppearance.BBoxHeight.Should().BeApproximately(
                    before.NormalAppearance.BBoxHeight, Tolerance, $"{subtype}: /AP /N /BBox height changed");
            }
        }
        finally { TryDelete(first); TryDelete(second); }
    }

    /// <summary>
    /// THE BAKED APPEARANCE IS SIZED TO THE ANNOTATION — the issue's last open
    /// checkbox, and the one assertion here that needs a caveat.
    ///
    /// "The /AP bbox must lie within /Rect" is NOT a conformance property:
    /// §12.5.5 has the viewer map the transformed BBox ONTO /Rect, so an
    /// oversized box is legal and merely scaled. What IS checkable is excise's
    /// own construction promise (#626): the form is authored in a local space
    /// of <c>[0 0 w h]</c> with no /Matrix, so that mapping is the identity and
    /// every viewer draws the pixels excise drew. A BBox of the wrong size
    /// still renders — stretched or squashed — which is exactly the kind of
    /// defect a "does an annotation appear?" test cannot see.
    ///
    /// So: this verifies a excise-specific construction, read out of the file by
    /// qpdf. It is not a claim about what the spec requires.
    /// </summary>
    [Theory]
    [MemberData(nameof(SubtypesWithBakedAppearance))]
    public void SynthesizedAppearance_HasABBoxSizedToItsRect_AsQpdfReadsIt(string subtype)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                Author(doc, subtype, "appearance");
                doc.Save(tmp);
            }

            var q = Seen(tmp, subtype);

            q.NormalAppearance.Should().NotBeNull(
                $"{subtype} is authored with a baked /AP /N (#626) and qpdf must be able to follow " +
                "the indirect reference to a Form XObject carrying a /BBox");

            var ap = q.NormalAppearance!;
            ap.BBoxWidth.Should().BeGreaterThan(0, $"{subtype}: a zero-width /BBox clips the whole appearance away");
            ap.BBoxHeight.Should().BeGreaterThan(0, $"{subtype}: a zero-height /BBox clips the whole appearance away");

            ap.BBoxWidth.Should().BeApproximately(q.Right - q.Left, Tolerance,
                $"{subtype}: the form's box is a different WIDTH from the /Rect it is mapped onto, so " +
                "§12.5.5 scales it — the drawing lands stretched or squashed, and still 'appears'");
            ap.BBoxHeight.Should().BeApproximately(q.Top - q.Bottom, Tolerance,
                $"{subtype}: the form's box is a different HEIGHT from the /Rect it is mapped onto");

            (ap.Matrix ?? Identity).Should().Equal(Identity,
                $"{subtype}: a non-identity /Matrix re-maps the appearance inside the /Rect, which is " +
                "legal but is not what excise authors — an unexpected one means the box above is " +
                "being compared against the wrong space");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// GEOMETRY BY COORDINATE, NOT BY COUNT.
    ///
    /// <see cref="GeometryBearingAnnotations_HaveTheirGeometrySeenByQpdf"/>
    /// counts strokes and vertices; a count survives a stroke that was reversed,
    /// scaled, rounded to integers or written in the wrong coordinate space.
    /// These are the actual numbers qpdf read, against the literals the caller
    /// passed. The fixtures are asymmetric and non-monotonic on purpose: a
    /// symmetric stroke reads identically reversed, and would pass the mutation
    /// this exists to catch.
    /// </summary>
    [Fact]
    public void GeometryBearingAnnotations_HaveTheirEveryPointSeenByQpdf()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                doc.AddInkAnnotation(1, new[]
                {
                    new[] { (100.0, 600.0), (140.0, 655.0), (150.0, 610.0), (210.0, 648.0) },
                }, "ink");
                doc.AddPolygonAnnotation(1, new[]
                {
                    (100.0, 400.0), (200.0, 500.0), (240.0, 400.0), (180.0, 370.0), (150.0, 420.0),
                }, "polygon");
                doc.AddArrowAnnotation(1, 110, 300, 290, 340, "arrow");
                doc.Save(tmp);
            }

            var seen = QpdfReferenceTool.ListAnnotations(tmp);
            seen.Should().NotBeNull();

            var ink = seen!.Single(a => a.Subtype == "Ink");
            ink.InkStrokes.Should().NotBeNull("an Ink annotation without /InkList draws nothing");
            ink.InkStrokes!.Should().HaveCount(1);
            SameNumbers(
                ink.InkStrokes[0],
                new double[] { 100, 600, 140, 655, 150, 610, 210, 648 },
                "/InkList: every point, in the order the caller drew it");

            var polygon = seen.Single(a => a.Subtype == "Polygon");
            SameNumbers(
                polygon.Vertices,
                new double[] { 100, 400, 200, 500, 240, 400, 180, 370, 150, 420 },
                "/Vertices: a reordered outline is a different (and still plausible) shape");

            var line = seen.Single(a => a.Subtype == "Line");
            SameNumbers(
                line.LineEndpoints,
                new double[] { 110, 300, 290, 340 },
                "/L: swapped endpoints point the arrowhead at the wrong end while /Rect and /LE stay correct");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// IMAGE STAMPS, WHICH THE SHARED THEORY CANNOT REACH.
    ///
    /// An ImageStamp is a /Stamp, so a document holding one alongside a plain
    /// Stamp breaks the exactly-one-per-subtype lookup every theory above is
    /// built on. That is a reason to give it its OWN document — not a reason to
    /// leave it structurally unverified, which is how a subtype ends up
    /// checked by nothing but a picture.
    /// </summary>
    [Fact]
    public void ImageStamp_IsSeenByQpdf_WithItsAppearanceSizedToTheRect()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        // 2x2 RGB, one byte per component — the smallest buffer the authoring
        // API accepts. What is under test is the annotation structure, not the
        // image.
        var pixels = new byte[]
        {
            255, 0, 0,   0, 255, 0,
            0, 0, 255,   255, 255, 255,
        };

        var expected = Box.Normalize();
        var tmp = NewPath();
        try
        {
            using (var doc = NewDocument())
            {
                doc.AddImageStampAnnotation(1, Box, pixels, 2, 2, "image stamp - keep me");
                doc.Save(tmp);
            }

            var all = QpdfReferenceTool.ListAnnotations(tmp);
            all.Should().NotBeNull("qpdf is available, so it must have produced a readable answer");
            all!.Where(a => a.Subtype == "Stamp").Should().HaveCount(1,
                "an INDEPENDENT parser must find exactly one /Stamp — the embedded image XObject " +
                "must not be mistaken for a second annotation");

            var q = all.Single(a => a.Subtype == "Stamp");
            q.Left.Should().BeApproximately(expected.Left, Tolerance, "ImageStamp: /Rect left is not where the caller asked");
            q.Bottom.Should().BeApproximately(expected.Bottom, Tolerance, "ImageStamp: /Rect bottom is not where the caller asked");
            q.Right.Should().BeApproximately(expected.Right, Tolerance, "ImageStamp: /Rect right is not where the caller asked");
            q.Top.Should().BeApproximately(expected.Top, Tolerance, "ImageStamp: /Rect top is not where the caller asked");

            (q.Contents ?? "").Should().Contain("keep me", "ImageStamp: /Contents must carry the caller's text");

            q.NormalAppearance.Should().NotBeNull(
                "an ImageStamp is nothing BUT its appearance — excise has no icon artwork to fall " +
                "back on, so a lost /AP /N leaves an annotation that draws the caller's image nowhere");
            q.NormalAppearance!.BBoxWidth.Should().BeApproximately(expected.Width, Tolerance,
                "ImageStamp: a /BBox of the wrong width means §12.5.5 scales the image into the /Rect distorted");
            q.NormalAppearance.BBoxHeight.Should().BeApproximately(expected.Height, Tolerance,
                "ImageStamp: a /BBox of the wrong height means the image lands stretched");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// GUARDS THE THEORY DATA. A subtype added to the authoring API but not to
    /// <see cref="Subtypes"/> ships with no independent structural verification
    /// at all — and nothing would say so. Needs no qpdf: it is a question about
    /// this file, not about a PDF.
    /// </summary>
    [Fact]
    public void EverySubtypeCoreCanAuthor_IsAlsoJudgedByTheIndependentOracle()
    {
        var covered = new HashSet<string>(SubtypeNames, StringComparer.Ordinal)
        {
            // Not a distinct /Subtype: an Arrow is a Line carrying
            // /LE [None ClosedArrow] (§12.5.6.7), so the by-subtype helpers here
            // cannot address it separately. Covered by the /LE assertion in
            // GeometryBearingAnnotations_HaveTheirGeometrySeenByQpdf.
            "Arrow",
            // Also /Stamp, so it cannot share a document with the plain Stamp
            // row without breaking the exactly-one-per-subtype lookup these
            // theories are built on. It gets its own file instead — see
            // ImageStamp_IsSeenByQpdf_WithItsAppearanceSizedToTheRect.
            "ImageStamp",
        };

        var authorable = typeof(PdfAnnotationAuthoring)
            .GetMethods()
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Add", StringComparison.Ordinal) && n.EndsWith("Annotation", StringComparison.Ordinal))
            .Select(n => n["Add".Length..^"Annotation".Length])
            .Distinct()
            .ToList();

        authorable.Should().NotBeEmpty(
            "reflection must actually find the authoring methods — if this ever returns nothing, " +
            "the check below passes by iterating an empty list, which is the vacuity failure this guard exists for");

        authorable.Where(s => !covered.Contains(s)).Should().BeEmpty(
            "every subtype excise can author must be checked by a parser that is not excise (#933)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private const double Tolerance = 0.5;

    private static readonly double[] Identity = { 1, 0, 0, 1, 0, 0 };

    private static readonly PdfRectangle Box = new(72, 600, 300, 660);

    /// <summary>
    /// The subtypes whose authoring path bakes an <c>/AP /N</c> Form XObject.
    /// Highlight and Text deliberately do NOT — they are left to the viewer's
    /// §12.5.5 synthesis — so listing them here would assert a promise excise
    /// never made.
    /// </summary>
    public static TheoryData<string> SubtypesWithBakedAppearance() => new()
    {
        "Underline", "StrikeOut", "Squiggly",
        "Square", "Circle", "FreeText",
        "Ink", "Line", "Polygon", "PolyLine", "Stamp",
    };

    private static PdfAnnotation Author(PdfDocument doc, string subtype, string contents = "x") => subtype switch
    {
        "Highlight" => doc.AddHighlightAnnotation(1, Box, contents),
        "Underline" => doc.AddUnderlineAnnotation(1, Box, contents),
        "StrikeOut" => doc.AddStrikeOutAnnotation(1, Box, contents),
        "Squiggly" => doc.AddSquigglyAnnotation(1, Box, contents),
        "Square" => doc.AddSquareAnnotation(1, Box, contents),
        "Circle" => doc.AddCircleAnnotation(1, Box, contents),
        "FreeText" => doc.AddFreeTextAnnotation(1, Box, contents),
        "Text" => doc.AddTextAnnotation(1, Box, contents),
        "Stamp" => doc.AddStampAnnotation(1, Box, "Confidential", contents),
        "Ink" => doc.AddInkAnnotation(1, new[] { new[] { (80.0, 610.0), (290.0, 650.0) } }, contents),
        "Line" => doc.AddLineAnnotation(1, 80, 610, 290, 650, contents),
        "Polygon" => doc.AddPolygonAnnotation(1, new[] { (80.0, 610.0), (200.0, 655.0), (290.0, 610.0) }, contents),
        "PolyLine" => doc.AddPolyLineAnnotation(1, new[] { (80.0, 610.0), (200.0, 655.0), (290.0, 610.0) }, contents),
        _ => throw new ArgumentOutOfRangeException(nameof(subtype), subtype, "not wired into this test"),
    };

    /// <summary>
    /// The single annotation of <paramref name="subtype"/> qpdf found in the
    /// file. Fails loudly rather than returning null when qpdf could not be
    /// asked, so a broken oracle can never read as "nothing to assert".
    /// </summary>
    private static QpdfAnnotation Seen(string path, string subtype)
    {
        var all = QpdfReferenceTool.ListAnnotations(path);
        all.Should().NotBeNull("qpdf is available, so it must have produced a readable answer");
        return all!.Single(a => a.Subtype == subtype);
    }

    /// <summary>
    /// Two number arrays agree element-wise, with "both absent" allowed and
    /// "one absent" reported as the loss it is.
    /// </summary>
    private static void SameNumbers(IReadOnlyList<double>? actual, IReadOnlyList<double>? expected, string because)
    {
        if (expected == null)
        {
            actual.Should().BeNull($"{because}: appeared where there was none");
            return;
        }

        actual.Should().NotBeNull($"{because}: was dropped entirely");
        actual!.Count.Should().Be(expected.Count, $"{because}: a coordinate was added or lost");
        for (var i = 0; i < expected.Count; i++)
            actual[i].Should().BeApproximately(expected[i], Tolerance, $"{because}: number {i} moved");
    }

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
