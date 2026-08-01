using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// A /Parent chain is attacker-controlled and is not guaranteed to be acyclic.
/// Every inherited-attribute lookup must terminate on one that is not (#881).
///
/// WHY THIS IS A DoS AND NOT A CURIOSITY
/// -------------------------------------
/// pdfium's bug_517126568.pdf is 577 bytes, has one page, and its whole content
/// stream draws a single blue 10x10 rectangle. Its /Pages node carries
/// `/Parent 2 0 R` pointing at itself, with a comment saying so. Before the fix
/// excise spent over 120 SECONDS on it — and only stopped because the corpus
/// scan's per-PDF budget killed it. pdftocairo and Ghostscript both render it
/// immediately.
///
/// excise is a redaction tool, so every document it opens came from somewhere
/// else. 577 bytes costing unbounded CPU, from a file that does not even look
/// malformed, is a denial-of-service primitive.
///
/// WHY THE EXISTING CONFORMANCE GATE MISSED IT
/// -------------------------------------------
/// #648's sweep opens every corpus file and touches Width/Height. This file
/// carries /MediaBox on the PAGE, so that lookup returns without ever walking
/// /Parent. It is /Rotate — and /Resources, and anything genuinely inherited —
/// that loops. A gate that reads only the non-inherited properties certifies
/// "parses without hanging" while an infinite loop sits one property away,
/// which is why this test reads the inherited ones explicitly.
/// </summary>
public class CircularParentChainTests
{
    /// <summary>
    /// Generous by design: the point is terminating at all. Before the fix this
    /// did not finish in 120 seconds.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="work"/> on a worker and FAILS if it does not finish
    /// inside <see cref="Budget"/>.
    ///
    /// The obvious way to write these tests — do the work inline, then assert on
    /// a Stopwatch — cannot fail. If the guard regresses the lookup never
    /// returns, so the assertion after it is never reached and the whole test
    /// run hangs instead of reporting. A regression must produce a red test, not
    /// a wedged suite, so the bound has to be outside the call.
    ///
    /// The worker is abandoned rather than cancelled on timeout: it is spinning
    /// in a tight loop with no cancellation point, which is precisely the defect
    /// being pinned. It dies with the test host.
    /// </summary>
    private static void MustCompleteQuickly(Action work, string because)
    {
        var task = System.Threading.Tasks.Task.Run(work);
        task.Wait(Budget).Should().BeTrue(because);
        task.GetAwaiter().GetResult();   // surface any exception the work threw
    }

    [Fact]
    public void SelfReferencingParent_DoesNotHangInheritedAttributeLookup()
    {
        var path = FindFixture("bug_517126568.pdf");
        Assert.SkipWhen(path == null,
            "PDFium corpus not present — run scripts/download-pdfium-corpus.sh");

        MustCompleteQuickly(() =>
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
            var page = doc.GetPage(1);

            // Every inherited-attribute path, not just the ones that happen to
            // be present on the page dictionary itself.
            _ = page.Rotation;    // walks /Parent — this is the one that looped
            _ = page.Resources;   // walks /Parent
            _ = page.MediaBox;    // present on the page, so it should not walk
            _ = page.CropBox;
        },
        "the /Pages node's /Parent points at itself, so an unguarded walk never terminates. " +
        "577 bytes of input must not cost unbounded CPU in a tool whose whole input is " +
        "other people's documents");
    }

    /// <summary>
    /// The same property, built by hand, so the guard is pinned even if the
    /// corpus is absent — and so the failure names the mechanism rather than a
    /// fixture.
    /// </summary>
    [Fact]
    public void HandBuiltSelfReferencingParent_Terminates()
    {
        var pdf = BuildSelfReferencingParentPdf();

        int rotation = -1;
        MustCompleteQuickly(() =>
        {
            using var doc = PdfDocument.Open(pdf);
            var page = doc.GetPage(1);
            rotation = page.Rotation;
            _ = page.Resources;
        },
        "an inherited-attribute lookup must stop when the /Parent chain revisits a node");

        rotation.Should().Be(0,
            "no /Rotate exists anywhere in the chain, so the default applies — a cycle " +
            "must yield the same answer an absent key would, not a different one");
    }

    /// <summary>
    /// A page tree with a self-referencing /Pages node, mirroring the shape of
    /// the pdfium fixture. Written out longhand so the offsets are real and the
    /// document opens through the normal parser rather than a repair path.
    /// </summary>
    private static byte[] BuildSelfReferencingParentPdf()
    {
        var objects = new[]
        {
            "1 0 obj <<\n  /Type /Catalog\n  /Pages 2 0 R\n>>\nendobj\n",
            // The trap: /Parent points at this same object.
            "2 0 obj <<\n  /Type /Pages\n  /Parent 2 0 R\n  /Count 1\n  /Kids [3 0 R]\n>>\nendobj\n",
            "3 0 obj <<\n  /Type /Page\n  /Parent 2 0 R\n  /MediaBox [0 0 200 200]\n  /Contents 4 0 R\n>>\nendobj\n",
            "4 0 obj <<\n  /Length 29\n>>\nstream\nq\n0 0 1 rg\n50 60 10 10 re f\nQ\nendstream\nendobj\n",
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Length + 1];
        foreach (var (obj, i) in objects.Select((o, i) => (o, i)))
        {
            offsets[i + 1] = sb.Length;
            sb.Append(obj);
        }

        var xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <<\n  /Root 1 0 R\n  /Size ").Append(objects.Length + 1).Append("\n>>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF\n");

        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string? FindFixture(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "pdfium", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
