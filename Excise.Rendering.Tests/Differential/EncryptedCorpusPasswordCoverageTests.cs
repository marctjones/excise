using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Every encrypted corpus fixture must either open with an empty user password
/// or have a credential in tests/corpus-passwords.tsv — and if neither is true,
/// that must be recorded deliberately rather than discovered by accident.
///
/// WHY
/// ---
/// The corpus scan passes its password manifest to excise AND to every oracle
/// (mutool, pdftocairo, ghostscript, pdfbox, pdfium each take a userPassword).
/// That plumbing is correct. The failure mode is upstream of it: an encrypted
/// fixture with no manifest entry is opened password-less by everyone, and the
/// result is indistinguishable from a genuine rendering failure. It shows up as
/// "the oracles refused" when the truth is "nobody was given the key".
///
/// Before this test, 8 of the 11 password-requiring fixtures in the corpora had
/// no manifest entry, and nothing said so.
///
/// Testing decryption by withholding a password we already have measures
/// nothing — so the gap has to be visible, and shrinking it has to be the only
/// way to make this test pass.
/// </summary>
public class EncryptedCorpusPasswordCoverageTests
{
    /// <summary>
    /// Fixtures that genuinely need a credential we do NOT have. Listed so they
    /// are a known, bounded debt rather than silence. Each was probed against a
    /// set of candidate passwords and none verified — a guess in the manifest
    /// would be worse than an honest gap, because it would look like coverage.
    /// </summary>
    private static readonly Dictionary<string, string> UnknownCredential = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gday garçon - open.pdf"] =
            "poppler fixture; upstream publishes no password and none of the obvious candidates verified",
        ["encrypted-256.pdf"] =
            "poppler AES-256 (V=5 R=6) fixture; upstream publishes no password",
        ["encrypted_hello_world_r5.pdf"] =
            "pdfium AES-256 fixture; only the OWNER password ('âge') is recoverable and excise " +
            "opens user-password-only (#324). Additionally V=5 R=5, a transitional Adobe " +
            "extension excise does not implement",
        ["encrypted_hello_world_r6.pdf"] =
            "pdfium AES-256 (V=5 R=6) fixture; only the OWNER password ('âge') is recoverable. " +
            "qpdf prints an empty user password, which for AES-256 means 'not recoverable' " +
            "rather than 'empty' — the empty password does not open it in mutool either",

        // pdfium NEGATIVE fixtures: these are supposed to be unopenable. They
        // exist to prove a reader rejects them, so "no password works" is the
        // fixture working, not a gap. Verified 2026-07-31: neither qpdf nor
        // mutool opens them with empty / hôtel / âge / 1234 / 5678.
        ["encrypted_hello_world_r2_bad_okey.pdf"] =
            "pdfium negative fixture — the owner key is deliberately corrupt ('bad_okey'), so " +
            "no password authenticates in any reader. Refusing it is the correct behaviour",
        ["encrypted_hello_world_r3_bad_okey.pdf"] =
            "pdfium negative fixture — deliberately corrupt owner key, as _r2_bad_okey above",
        ["bug_644.pdf"] =
            "pdfium fixture that is both damaged (no xref, invalid /ID in trailer) and R=5 " +
            "encrypted; qpdf reconstructs the xref and still reports 'invalid password', and " +
            "mutool cannot open it either. No credential is recoverable",
    };

    /// <summary>
    /// Corpus directories to sweep, relative to the repo root.
    ///
    /// Must list EVERY corpus the rendering scan covers. When pdfium and
    /// verapdf-corpus were added to the scan but not here, the scan pinned 6
    /// password-blocked pdfium pages as expected outcomes while the test whose
    /// whole job is to notice missing credentials was not looking at that
    /// directory — the exact silence this class was written to end, recreated
    /// one corpus over.
    /// </summary>
    private static readonly string[] CorpusDirs =
    {
        "test-pdfs/pdfjs",
        "test-pdfs/poppler",
        "test-pdfs/isartor",
        "test-pdfs/pdfium",
        "test-pdfs/verapdf-corpus",
    };

    [Fact]
    public void EveryEncryptedFixture_EitherOpensFreely_OrHasAKnownPassword()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable,
            "qpdf not installed — needed to identify which fixtures are encrypted");

        var root = FindRepoRoot();
        Assert.SkipWhen(root == null, "could not locate repo root");

        var present = CorpusDirs
            .Select(d => Path.Combine(root!, d))
            .Where(Directory.Exists)
            .ToList();
        Assert.SkipWhen(present.Count == 0,
            "no corpora present (scripts/download-pdfjs-corpus.sh, download-poppler-corpus.sh, download-test-pdfs.sh)");

        var known = LoadPasswordManifest(Path.Combine(root!, "tests", "corpus-passwords.tsv"));
        known.Should().NotBeEmpty("tests/corpus-passwords.tsv should carry the credentials we do have");

        var uncovered = new List<string>();

        foreach (var pdf in present.SelectMany(d => Directory.EnumerateFiles(d, "*.pdf", SearchOption.AllDirectories)))
        {
            if (QpdfReferenceTool.IsEncrypted(pdf) != true)
                continue;

            var name = Path.GetFileName(pdf);
            if (known.ContainsKey(name) || UnknownCredential.ContainsKey(name))
                continue;

            // No manifest entry: acceptable only if it genuinely opens without
            // one. qpdf's own "requires password" verdict is NOT authoritative
            // here — it reports that for files excise opens fine, because qpdf
            // cannot derive their short V4/R4 keys (see issue19484_1/2). So ask
            // excise, which is the reader whose behaviour we are documenting.
            if (!OpensWithoutPassword(pdf))
                uncovered.Add(name);
        }

        uncovered.Should().BeEmpty(
            "an encrypted fixture with no credential is opened password-less by excise AND by every " +
            "oracle, so a missing key is indistinguishable from a rendering failure. Add the password " +
            "to tests/corpus-passwords.tsv, or record it in UnknownCredential with why it cannot be " +
            $"obtained. Uncovered: {string.Join(", ", uncovered)}");
    }

    /// <summary>
    /// The manifest is only useful if its entries actually work. A stale or
    /// mistyped password would otherwise sit there looking like coverage while
    /// the file silently failed to decrypt for both excise and the oracles.
    /// </summary>
    [Fact]
    public void EveryManifestPassword_ActuallyDecryptsItsFixture()
    {
        var root = FindRepoRoot();
        Assert.SkipWhen(root == null, "could not locate repo root");

        var known = LoadPasswordManifest(Path.Combine(root!, "tests", "corpus-passwords.tsv"));
        Assert.SkipWhen(known.Count == 0, "no password manifest entries");

        var checkedAny = false;
        var failures = new List<string>();

        foreach (var (name, password) in known)
        {
            var path = CorpusDirs
                .Select(d => Path.Combine(root!, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, name, SearchOption.AllDirectories))
                .FirstOrDefault();
            if (path == null)
                continue;   // corpus not downloaded, or fixture not mirrored

            checkedAny = true;
            if (!OpensWithPassword(path, password))
                failures.Add($"{name} (password '{password}')");
        }

        Assert.SkipWhen(!checkedAny, "none of the manifest's fixtures are present locally");
        failures.Should().BeEmpty(
            "every password in tests/corpus-passwords.tsv must decrypt its fixture — an entry that " +
            "does not work is worse than no entry, because it reads as coverage. Failed: " +
            string.Join(", ", failures));
    }

    /// <summary>
    /// No manifest entry may match more than one file across the corpora.
    ///
    /// WHY
    /// ---
    /// The manifest is keyed by BASENAME (`Path.GetFileName`), and the entries
    /// added for PDFium include `encrypted.pdf` — about as generic a filename as
    /// exists. Four corpora now sit side by side, so a second `encrypted.pdf`
    /// appearing in any of them would silently hand PDFium's password to an
    /// unrelated file. The failure is quiet in the worst way: the wrong password
    /// does not decrypt, the page is classified as if it were a rendering
    /// failure, and the credential looks like coverage.
    ///
    /// The corpora are downloaded mirrors that change when upstream changes, so
    /// this cannot be established once by inspection — it has to be re-checked.
    /// </summary>
    [Fact]
    public void NoManifestEntry_MatchesMoreThanOneCorpusFile()
    {
        var root = FindRepoRoot();
        Assert.SkipWhen(root == null, "could not locate repo root");

        var known = LoadPasswordManifest(Path.Combine(root!, "tests", "corpus-passwords.tsv"));
        Assert.SkipWhen(known.Count == 0, "no password manifest entries");

        var present = CorpusDirs
            .Select(d => Path.Combine(root!, d))
            .Where(Directory.Exists)
            .ToList();
        Assert.SkipWhen(present.Count == 0, "no corpora present");

        var ambiguous = new List<string>();
        foreach (var name in known.Keys)
        {
            var matches = present
                .SelectMany(d => Directory.EnumerateFiles(d, name, SearchOption.AllDirectories))
                .ToList();
            if (matches.Count > 1)
            {
                ambiguous.Add($"{name} matches {matches.Count}: " +
                              string.Join(", ", matches.Select(m => Path.GetRelativePath(root!, m))));
            }
        }

        ambiguous.Should().BeEmpty(
            "the password manifest is keyed by basename, so an entry matching two corpus files " +
            "applies one file's password to the other — which fails to decrypt and is then " +
            "indistinguishable from a rendering failure. Disambiguate by corpus-relative path. " +
            string.Join(" | ", ambiguous));
    }

    // ---------------------------------------------------------------- helpers --

    /// <summary>
    /// "Opens" means the document decrypts. A document that decrypts and is then
    /// blocked by its /P permission flags has still decrypted — that is #642
    /// enforcing permissions, a different thing entirely.
    /// </summary>
    private static bool OpensWithoutPassword(string path)
    {
        try
        {
            using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));
            return doc.PageCount >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool OpensWithPassword(string path, string password)
    {
        try
        {
            using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path), password);
            return doc.PageCount >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> LoadPasswordManifest(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            var name = Path.GetFileName(cols[0].Trim());
            if (name.Length > 0) map[name] = cols[1];
        }
        return map;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
