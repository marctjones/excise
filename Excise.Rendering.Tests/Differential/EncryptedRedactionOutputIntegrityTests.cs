using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1095 — redacting an encrypted document must produce a file that is
/// STRUCTURALLY VALID and ACTUALLY REDACTED, judged by two tools that are not
/// excise.
///
/// <para><b>The sibling defect, not the crash.</b> #1048 is a
/// <c>CryptographicException</c> on redacting an encrypted document — loud, and
/// therefore survivable. The same defect NOT throwing, and instead writing a
/// corrupt or partially-encrypted file, produces a document that looks fine and
/// is not. Nothing gated redacted encrypted output before this.</para>
///
/// <para><b>Why "the term is gone" is not enough here.</b> On a corrupt output
/// every extractor reads nothing, so a term-absence assertion passes for the
/// worst possible reason. This asserts the surviving text too: qpdf must accept
/// the structure, and mutool must still read back essentially everything except
/// the term. A file that fails to decrypt, fails to parse, or comes back empty
/// fails here — which is the whole point.</para>
///
/// <para>Fixtures and passwords come from <c>tests/corpus-passwords.tsv</c>,
/// whose values are published by mozilla/pdf.js and were verified against
/// excise when that file was written.</para>
/// </summary>
public class EncryptedRedactionOutputIntegrityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static readonly string[] Corpora =
    {
        "test-pdfs/pdfjs", "test-pdfs/pdfium", "test-pdfs/poppler",
        "test-pdfs/poppler/unittestcases", "test-pdfs/pdf20", "test-pdfs/smoke",
    };

    private static string? Resolve(string fileName)
    {
        var root = RepoRoot();
        foreach (var c in Corpora)
        {
            var p = Path.Combine(root, c, fileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Every (fixture, password) pair the manifest documents.</summary>
    public static TheoryData<string, string> EncryptedFixtures()
    {
        var data = new TheoryData<string, string>();
        var manifest = Path.Combine(RepoRoot(), "tests/corpus-passwords.tsv");
        if (!File.Exists(manifest)) return data;

        foreach (var line in File.ReadAllLines(manifest))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim().Length == 0) continue;
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            data.Add(parts[0].Trim(), parts[1].Trim());
        }
        return data;
    }

    /// <summary>
    /// A term the ORACLE can see, taken from the document rather than chosen by
    /// someone who knows where the bug is. Null when there is nothing usable —
    /// some manifest fixtures decrypt and are then blocked by their own /P
    /// permissions, which is documented behaviour, not a redaction failure.
    /// </summary>
    private static string? SampleTerm(string oracleText) =>
        oracleText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length >= 5)
            .GroupBy(w => w, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

    private static int Alnum(string s) => s.Count(char.IsLetterOrDigit);

    /// <summary>
    /// Fixtures this gate FAILS on today, each with the issue that owns the
    /// defect. Recorded rather than skipped: a skip says "not checked", and
    /// these are checked and known-bad, which is a different fact.
    ///
    /// <para>The check runs BOTH ways. An unlisted fixture must pass, and a
    /// listed one must still fail — so fixing the defect fails this gate until
    /// the entry is deleted, and a stale entry cannot quietly keep a fixed
    /// document out of the gate.</para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownDefects = new(StringComparer.Ordinal)
    {
        // #1100's two entries were DELETED here once the standard-14 metrics
        // landed — the reverse check demanded it, which is the whole reason it
        // exists: a fixed defect must not keep its document out of the gate.
        ["encrypted.pdf"] =
            "#1048 — CryptographicException 'input data is not a complete block' from " +
            "AesCbcDecrypt while redacting. This gate is what first reproduced it outside " +
            "a hand-written fixture.",
    };

    [Theory]
    [MemberData(nameof(EncryptedFixtures))]
    public void RedactedEncryptedOutput_IsValidAndActuallyRedacted(string fixture, string password)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "needs qpdf [requires: tool:qpdf]");
        var path = Resolve(fixture);
        Assert.SkipWhen(path == null, $"fixture not present: {fixture} [requires: corpus:pdfjs]");

        var before = MutoolTextExtractor.ExtractPage(path!, 1, password);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(before),
            "mutool reads no text from this fixture, so it cannot referee the result " +
            "[requires: tool:mutool]");

        var term = SampleTerm(before!);
        Assert.SkipWhen(term == null, "no term of usable length in the oracle's text");

        var failure = RunGate(path!, password, term!, before!);

        if (KnownDefects.TryGetValue(fixture, out var owner))
        {
            failure.Should().NotBeNull(
                $"{fixture} is recorded as a known defect ({owner}). It now passes — delete " +
                "its KnownDefects entry so the gate starts protecting it.");
            return;
        }

        failure.Should().BeNull($"{fixture}: {failure}");
    }

    /// <summary>
    /// The gate proper. Returns null when the fixture passes, or the reason it
    /// did not — including a throw, which is #1048's shape.
    /// </summary>
    private static string? RunGate(string path, string password, string term, string before)
    {
        var output = Path.Combine(Path.GetTempPath(), $"excise-encredact-{Guid.NewGuid():N}.pdf");
        try
        {
            RedactionReport report;
            try
            {
                using var doc = PdfDocument.Open(path, password);
                report = doc.RedactText(term);
                doc.Save(output, doc.GetReEncryptionOptions(password));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // #1048's shape. A crash while redacting is a failure of this
                // gate, not something to swallow.
                return $"redaction threw {ex.GetType().Name}: {ex.Message}";
            }

            // ── 1. STRUCTURE, per qpdf ────────────────────────────────────
            var check = QpdfReferenceTool.Check(output, password);
            if (check == null) return "qpdf could not run on the redacted output";
            if (!check.Value.Success)
                return $"qpdf rejected the redacted output:\n{check.Value.Output}";

            // ── 2. TEXT, per mutool ───────────────────────────────────────
            var after = MutoolTextExtractor.ExtractPage(output, 1, password);
            if (after == null)
                return "mutool could not read the redacted output at all — a file that no " +
                       "longer decrypts or parses is the silent half of #1048, and it would " +
                       "pass every term-absence assertion for the worst possible reason";

            if (after.Contains(term, StringComparison.Ordinal))
                return $"'{term}' is still present per mutool";

            // ── 3. ANTI-VACUITY: the rest of the document must survive ────
            // Without this, "term absent" is satisfied by an empty file.
            var expected = Alnum(before) - report.VerifiedRemovals * Alnum(term);
            if (Alnum(after) <= (int)(expected * 0.9))
                return $"only {Alnum(after)} alphanumerics survived; expected about " +
                       $"{expected} (mutool saw {Alnum(before)} before, excise removed " +
                       $"{report.VerifiedRemovals} occurrence(s) of '{term}'). Far less means " +
                       "the redaction took the document with it, or the output no longer " +
                       "decrypts properly";

            return null;
        }
        finally { try { File.Delete(output); } catch { /* best effort */ } }
    }
}
