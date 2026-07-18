using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Concurrency stress coverage for #376: a single <see cref="PdfDocument"/>
/// resolves objects through one shared lexer with a mutable stream position.
/// Concurrent readers (e.g. the GUI's background search-indexer parsing pages
/// while the UI thread reads links/renders) used to corrupt each other's seeks,
/// surfacing as spurious "Unexpected keyword 'obj'" parse errors; the fix
/// serializes resolution behind a reentrant lock.
///
/// NOTE: the underlying data race is timing-dependent and does not reproduce
/// deterministically on a small in-memory PDF (objects parse too fast to
/// interleave) — it was verified against a large real-world document where it
/// reliably produced 700+ errors before the fix and zero after. This test is a
/// best-effort stress check: it exercises heavy concurrent resolution and also
/// guards against a regression making the lock non-reentrant (which would
/// deadlock recursive ObjStm/length resolution).
/// </summary>
public class ConcurrentAccessTests
{
    [Fact]
    public async Task GetPage_And_GetObject_AreSafeUnderConcurrentAccess()
    {
        // Many objects so the *uncached* window (where concurrent seeks collide)
        // is large — with too few objects every thread hits the cache after the
        // first pass and the race never fires.
        var bytes = CreateMultiPagePdf(pageCount: 120);
        using var doc = PdfDocument.Open(bytes);
        doc.PageCount.Should().Be(120);

        var errors = new ConcurrentQueue<Exception>();
        const int threadCount = 8;
        using var startGate = new System.Threading.Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(tid => Task.Run(() =>
        {
            try
            {
                // Release all threads at once onto the freshly-opened (fully
                // uncached) document to maximise seek interleaving. Half the
                // threads walk pages forward, half backward.
                startGate.SignalAndWait();
                bool forward = tid % 2 == 0;
                for (int p = forward ? 1 : doc.PageCount;
                     forward ? p <= doc.PageCount : p >= 1;
                     p += forward ? 1 : -1)
                {
                    var page = doc.GetPage(p);
                    _ = page.GetContentStreamBytes();
                    _ = page.GetLinks();
                    // Letters extraction resolves the font (and ToUnicode etc.)
                    // via more GetObject calls — this is the heavy concurrent
                    // resolution that surfaced the race on real documents.
                    _ = page.Letters?.Count();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        errors.Should().BeEmpty(
            "concurrent object resolution must not corrupt the shared lexer (#376); " +
            "first error: " + (errors.FirstOrDefault()?.Message ?? "none"));
    }

    private static byte[] CreateMultiPagePdf(int pageCount)
    {
        // One catalog, one Pages node, then per page: a Page dict + a Contents
        // stream. A shared Helvetica font closes the set. Classic xref table.
        var bodies = new System.Collections.Generic.List<string>();
        // obj1 catalog, obj2 pages (kids filled below)
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add("__PAGES__"); // placeholder, patched once we know kid numbers

        int fontObj = 2 + pageCount * 2 + 1; // after all page+content objects
        var kids = new System.Collections.Generic.List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 2;
            int contentObj = pageObj + 1;
            kids.Add($"{pageObj} 0 R");
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       $"/Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            var op = new StringBuilder();
            for (int line = 0; line < 24; line++)
                op.Append($"BT /F1 12 Tf 72 {700 - (line % 40) * 16} Td (Page {i + 1} line {line}) Tj ET\n");
            var content = op.ToString();
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageCount} >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
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
}
