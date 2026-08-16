using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Hand-built hostile document structures (#960, part 1 of 3).
///
/// The corpora are other projects' historical crashes and the existing
/// fuzzers mutate bytes; neither constructs the *structural* attack a
/// hostile author would write on purpose — a reference cycle, a tree deeper
/// than the defense, a count that lies. Each test here builds one such
/// structure in a few hundred bytes and pins what excise does with it, under
/// a hard wall-clock budget so a missing loop bound fails in seconds rather
/// than hanging the suite.
///
/// <para><b>Tier: t0.</b> Every case is an in-memory parse of a sub-kilobyte
/// document; the whole class runs in well under a second, so it belongs in
/// the tier that runs before every push rather than waiting on a nightly
/// runner. Issue #960 asked for a nightly tier, but
/// <c>tests/format-compatibility-suite.json</c>'s <c>nightly-corpus</c> is
/// still <c>status: planned</c> with <c>primaryCommand: null</c> — there is
/// no nightly runner to schedule against, and a suite that runs in an
/// existing tier finds bugs while one waiting on a planned tier finds
/// none.</para>
///
/// <para><b>Pinning, not blessing.</b> Several of these record behaviour that
/// is merely *bounded* rather than ideal (a lying /Count is reported as-is;
/// a self-referential /Contents yields an empty stream). The contract being
/// gated is graceful failure — typed exception or survivable result, never a
/// raw CLR crash, an unbounded loop, or a process-killing stack
/// overflow.</para>
/// </summary>
public class HostileStructureTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------
    // #969 — the defect this suite found. Both cases killed the PROCESS
    // (SIGABRT, exit 134) before the fix, so they were bisected in a
    // throwaway out-of-process harness: a StackOverflowException cannot be
    // caught, and a test that reproduced it would have taken the test host
    // down with it rather than failing.
    // ---------------------------------------------------------------

    /// <summary>
    /// A stream whose <c>/Length</c> is an indirect reference to its OWN
    /// object. Resolving the length re-enters the parse of the object being
    /// parsed; <c>_objectCache</c> cannot break the cycle because an object
    /// is cached only after its parse completes (#969).
    /// </summary>
    [Fact]
    public async Task StreamLengthReferencingItsOwnObject_IsBounded()
    {
        var bytes = SelfReferentialLength();

        await AdversarialInputContract.WithinBudget("self-referential /Length (#969)", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.PageCount.Should().Be(1);

            // Not merely "does not crash": the in-flight guard returns null,
            // which drops ParseStream onto its scan-to-endstream fallback —
            // the same path any unresolvable /Length already took — so the
            // real content survives. If this ever regresses to an empty
            // stream, the guard has been turned into a data-loss bug.
            var content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentStreamBytes());
            content.Should().Contain("BT", "the endstream-scan fallback must still recover the content");
        });
    }

    /// <summary>
    /// Two streams whose <c>/Length</c> entries reference each other — the
    /// transitive form of the same cycle, which a guard keyed on "am I
    /// resolving myself?" alone would miss (#969).
    /// </summary>
    [Fact]
    public async Task StreamLengthCycleBetweenTwoObjects_IsBounded()
    {
        var bytes = MutualLengthCycle();

        await AdversarialInputContract.WithinBudget("mutual /Length cycle (#969)", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.PageCount.Should().Be(1);
            Encoding.ASCII.GetString(doc.GetPage(1).GetContentStreamBytes())
                .Should().Contain("BT");
        });
    }

    // ---------------------------------------------------------------
    // Defenses that already existed. These pin WHAT the defense does, which
    // was undocumented: PageCollection's visited-set and MaxPageTreeDepth
    // were readable in the source but nothing asserted whether they threw or
    // silently truncated.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PageTreeKidsCycle_ThrowsTypedParseException()
    {
        var bytes = KidsCycle();

        await AdversarialInputContract.WithinBudget("/Kids cycle", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);

            // The cycle is reached through the SECOND kid; the first resolves
            // normally, so the defense must be per-traversal, not per-open.
            doc.GetPage(1).Should().NotBeNull();

            var ex = Record.Exception(() => doc.GetPage(2));
            ex.Should().BeOfType<PdfParseException>(
                "PageCollection's reference-equality visited set must convert the cycle " +
                "into a typed failure, not an unbounded walk");
            ex!.Message.Should().Contain("circular");
        });
    }

    [Fact]
    public async Task PageTreeDeeperThanMaxDepth_ThrowsTypedParseException()
    {
        // MaxPageTreeDepth is 32; 200 nested /Pages nodes clears it with room
        // to spare without being expensive to build.
        var bytes = DeepPageTree(depth: 200);

        await AdversarialInputContract.WithinBudget("200-deep page tree", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            var ex = Record.Exception(() => doc.GetPage(1));
            ex.Should().BeOfType<PdfParseException>();
            ex!.Message.Should().Contain("depth");
        });
    }

    [Fact]
    public async Task PageParentSelfReference_IsBounded()
    {
        var bytes = SelfReferentialParent();

        await AdversarialInputContract.WithinBudget("self-referential /Parent", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            // Inherited-attribute lookup walks /Parent upward; a self-loop
            // must terminate. It does, and the page stays usable.
            doc.GetPage(1).GetContentStreamBytes().Should().NotBeNull();
        });
    }

    [Fact]
    public async Task PageTreeCountOfIntMaxValue_DoesNotAllocateOrHang()
    {
        var bytes = LyingPageCount(count: int.MaxValue);

        await AdversarialInputContract.WithinBudget("/Count = int.MaxValue", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);

            // PINNED, NOT BLESSED: /Count is reported as declared rather than
            // verified against the tree. That is survivable (nothing
            // pre-allocates per page), but a caller sizing a buffer or a
            // progress bar off PageCount is trusting the document. The gate
            // here is that the lie stays cheap and the real page still
            // resolves.
            doc.PageCount.Should().Be(int.MaxValue);
            doc.GetPage(1).GetContentStreamBytes().Should().NotBeNull();

            Record.Exception(() => doc.GetPage(2))
                .Should().BeOfType<PdfParseException>("a page beyond the real tree must fail typed");
        });
    }

    [Fact]
    public async Task XRefPrevCycle_TerminatesAndParses()
    {
        var bytes = XRefPrevCycle();

        await AdversarialInputContract.WithinBudget("cyclic xref /Prev chain", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.PageCount.Should().Be(1, "the visited-offset set must end the /Prev walk, not loop it");
        });
    }

    [Fact]
    public async Task PageContentsReferencingItsOwnPageDictionary_IsBounded()
    {
        var bytes = ContentsPointingAtOwnPage();

        await AdversarialInputContract.WithinBudget("/Contents -> own page dict", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            // A dictionary is not a stream, so there is nothing to read: the
            // page yields empty content rather than recursing or throwing.
            doc.GetPage(1).GetContentStreamBytes().Should().BeEmpty();
        });
    }

    // ---------------------------------------------------------------
    // Cycles in the DOCUMENT-LEVEL carriers that redaction walks. These
    // matter beyond parsing: #608/#636 made the redactor traverse outlines
    // and the structure tree, so a cycle there is a hang in the security
    // path, not just the viewer.
    // ---------------------------------------------------------------

    [Fact]
    public async Task OutlineCycle_SurvivesRedactAndSave()
    {
        var bytes = OutlineSelfLoop();

        await AdversarialInputContract.WithinBudget("outline /Next+/First self-loop", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.RedactText("secret");           // walks outline titles (#608)
            using var ms = new System.IO.MemoryStream();
            doc.Save(ms);
            ms.Length.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public async Task StructureTreeCycle_SurvivesRedactAndSave()
    {
        var bytes = StructTreeSelfLoop();

        await AdversarialInputContract.WithinBudget("struct-tree /K self-loop", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.RedactText("secret");           // walks /ActualText, /Alt (#636)
            using var ms = new System.IO.MemoryStream();
            doc.Save(ms);
            ms.Length.Should().BeGreaterThan(0);
        });
    }

    // ---------------------------------------------------------------
    // Recursion bounds. Both parsers recurse on nesting, so both need a
    // depth cap — without one these are stack overflows, i.e. the #969
    // failure mode again on a different path.
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeeplyNestedArrayInObject_ThrowsTypedParseException()
    {
        var bytes = NestedArrayInCatalog(depth: 5000);   // PdfParser.MaxNestingDepth = 512

        await AdversarialInputContract.WithinBudget("5000-deep array in an object", Budget, () =>
        {
            var ex = Record.Exception(() => PdfDocument.Open(bytes));
            ex.Should().BeOfType<PdfParseException>();
            ex!.Message.Should().Contain("nesting depth");
        });
    }

    [Fact]
    public async Task DeeplyNestedArrayInContentStream_ThrowsTypedParseException()
    {
        var content = Encoding.ASCII.GetBytes(
            new string('[', 5000) + "1" + new string(']', 5000) + " TJ"); // MaxNestingDepth = 256

        await AdversarialInputContract.WithinBudget("5000-deep array in a content stream", Budget, () =>
        {
            var ex = Record.Exception(() => new ContentStreamParser(content).Parse());
            ex.Should().BeOfType<PdfParseException>();
            ex!.Message.Should().Contain("nesting depth");
        });
    }

    /// <summary>
    /// The SECOND defect this suite found (#971). Text extraction is the third
    /// parser of content-stream bytes, and it was the only one with no nesting
    /// bound at all: <c>ParseArray -> ParseToken -> ParseArray</c> recursed
    /// once per '[' until the stack ran out, killing the process.
    ///
    /// <para>The depth here is deliberate. 5,000 survives on the 8 MB main
    /// thread and dies on a ~1 MB thread-pool thread — and the thread pool is
    /// where the GUI's background indexing, every <c>Task.Run</c>, and every
    /// xunit body run. Running this assertion through
    /// <see cref="AdversarialInputContract.WithinBudget"/> is therefore not
    /// only about the timeout: it puts the work on the thread whose stack is
    /// small enough to expose the bug. A main-thread probe reports this exact
    /// document as fine, which is how it stayed hidden.</para>
    /// </summary>
    [Fact]
    public async Task PageWithDeeplyNestedContentStream_ThrowsTypedParseException()
    {
        var bytes = PageWithContent(new string('[', 5000) + "1" + new string(']', 5000) + " TJ");

        await AdversarialInputContract.WithinBudget("page over a 5000-deep content stream (#971)", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            var ex = Record.Exception(() => doc.GetPage(1).Letters);
            ex.Should().BeOfType<PdfParseException>(
                "extraction must bound its own recursion like the other two parsers do — " +
                "a StackOverflowException cannot be caught and takes the process with it");
            ex!.Message.Should().Contain("nesting depth");
        });
    }

    [Fact]
    public async Task RecursiveFormXObject_IsBounded()
    {
        var bytes = SelfInvokingFormXObject();

        await AdversarialInputContract.WithinBudget("form XObject invoking itself", Budget, () =>
        {
            using var doc = PdfDocument.Open(bytes);
            doc.GetPage(1).Letters.Should().NotBeNull();
        });
    }

    // ---------------------------------------------------------------
    // Builders. Kept literal rather than parameterised: a hostile fixture
    // whose construction needs decoding is one nobody will re-derive when it
    // fails.
    // ---------------------------------------------------------------

    private static byte[] Assemble(IList<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string ContentObject(string content) =>
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] SelfReferentialLength() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        "<< /Length 4 0 R >>\nstream\nBT ET\nendstream",   // /Length -> this object
    });

    private static byte[] MutualLengthCycle() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        "<< /Length 5 0 R >>\nstream\nBT ET\nendstream",
        "<< /Length 4 0 R >>\nstream\nBT ET\nendstream",
    });

    private static byte[] KidsCycle() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 2 >>",  // first kid is this node
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject("BT ET"),
    });

    private static byte[] DeepPageTree(int depth)
    {
        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        for (int i = 0; i < depth; i++)
            bodies.Add($"<< /Type /Pages /Kids [{i + 3} 0 R] /Count 1 /Parent {i + 1} 0 R >>");
        int leaf = depth + 2;
        bodies.Add($"<< /Type /Page /Parent {leaf - 1} 0 R /MediaBox [0 0 612 792] /Contents {leaf + 1} 0 R >>");
        bodies.Add(ContentObject("BT ET"));
        return Assemble(bodies);
    }

    private static byte[] SelfReferentialParent() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /Parent 2 0 R >>",
        "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject("BT ET"),
    });

    private static byte[] LyingPageCount(int count) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        $"<< /Type /Pages /Kids [3 0 R] /Count {count} >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject("BT ET"),
    });

    private static byte[] ContentsPointingAtOwnPage() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R >>",
        ContentObject("BT ET"),
    });

    private static byte[] OutlineSelfLoop() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject("BT ET"),
        "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
        "<< /Title (secret) /Parent 5 0 R /First 6 0 R /Next 6 0 R >>",
    });

    private static byte[] StructTreeSelfLoop() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject("BT ET"),
        "<< /Type /StructTreeRoot /K 6 0 R >>",
        "<< /Type /StructElem /S /P /P 5 0 R /K 6 0 R /ActualText (secret) >>",
    });

    private static byte[] NestedArrayInCatalog(int depth)
    {
        var nest = new string('[', depth) + "1" + new string(']', depth);
        return Assemble(new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /Junk {nest} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            ContentObject("BT ET"),
        });
    }

    private static byte[] PageWithContent(string content) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        ContentObject(content),
    });

    private static byte[] SelfInvokingFormXObject()
    {
        const string inner = "q /X1 Do Q";
        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /X1 5 0 R >> >> >>",
            ContentObject("q /X1 Do Q"),
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Length {inner.Length} " +
                "/Resources << /XObject << /X1 5 0 R >> >> >>\nstream\n" + inner + "\nendstream",
        });
    }

    private static byte[] XRefPrevCycle()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            ContentObject("BT ET"),
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        // Two xref sections whose /Prev entries point at each other. Both
        // offsets are D10-padded so patching the first one's value cannot
        // move the second one.
        long xrefA = sb.Length;
        var table = new StringBuilder();
        table.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) table.Append($"{offsets[i]:D10} 00000 n \n");

        string TrailerWithPrev(long prev) =>
            $"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} /Prev {prev:D10} >>\n";

        sb.Append(table).Append(TrailerWithPrev(0));
        long xrefB = sb.Length;
        sb.Length = (int)xrefA;
        sb.Append(table).Append(TrailerWithPrev(xrefB));   // A -> B
        sb.Append(table).Append(TrailerWithPrev(xrefA));   // B -> A
        sb.Append($"startxref\n{xrefB}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
