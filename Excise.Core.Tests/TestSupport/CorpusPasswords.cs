using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Excise.Core.Document;

namespace Excise.TestSupport;

/// <summary>
/// THE one place a test learns the password of an encrypted fixture: <c>tests/corpus-passwords.tsv</c>,
/// read as UTF-8 (its own header explains why: <c>hôtel</c> read as Latin-1 silently stops decrypting).
///
/// <para><b>Why this exists.</b> A test that opens an encrypted corpus file without the key it already
/// has measures nothing: the refusal is indistinguishable from a parser failure, and a harness that
/// skips or baselines it as "cannot open" hides that the key was on file all along. Two parsers of
/// this manifest lived in separate test classes, and the corpus-scan scripts have their own; a third
/// in a harness would have drifted the same way. Tests that open corpus files call
/// <see cref="Open(string)"/> or <see cref="OpenOptionsFor(string)"/> and never carry a password of
/// their own.</para>
///
/// <para><b>Keying.</b> An entry is matched by file name, as the scan scripts do. An entry written with
/// a directory part (<c>pdfium/encrypted.pdf</c>) additionally has to match the end of the path, which
/// is how two corpora that both ship a generic name are told apart.
/// <c>EncryptedCorpusPasswordCoverageTests</c> fails when one entry would match two files.</para>
///
/// <para>What this does not know: owner passwords (excise opens with the user password only), and the
/// credentials nobody has — those are recorded with their reason in that test's
/// <c>UnknownCredential</c>.</para>
/// </summary>
public static class CorpusPasswords
{
    private const string ManifestRelativePath = "tests/corpus-passwords.tsv";

    private static readonly Lazy<IReadOnlyList<Entry>> Loaded = new(Load);

    /// <summary>One manifest row: the path as written (a bare name or a corpus-relative path) and its user password.</summary>
    public readonly record struct Entry(string Path, string Password)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>Every row of the manifest, in file order. Empty when the manifest is not found.</summary>
    public static IReadOnlyList<Entry> Entries => Loaded.Value;

    /// <summary>The manifest file this process read, or null when it could not be found.</summary>
    public static string? ManifestPath => TestRepoLayout.FindFile("tests", "corpus-passwords.tsv");

    /// <summary>The user password recorded for <paramref name="pdfPath"/>, or null when none is on file.</summary>
    public static string? For(string pdfPath)
    {
        var normalised = pdfPath.Replace('\\', '/');
        var name = System.IO.Path.GetFileName(normalised);
        foreach (var entry in Entries)
        {
            if (!string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            var written = entry.Path.Replace('\\', '/');
            if (written.Contains('/') &&
                !normalised.EndsWith(written, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return entry.Password;
        }

        return null;
    }

    /// <summary>True when the manifest holds a password for <paramref name="pdfPath"/>.</summary>
    public static bool Has(string pdfPath) => For(pdfPath) != null;

    /// <summary>
    /// Open options carrying the recorded password for <paramref name="pdfPath"/> when there is one,
    /// otherwise the defaults. Pass this wherever a test opens a corpus file from bytes.
    /// </summary>
    public static PdfOpenOptions OpenOptionsFor(string pdfPath)
    {
        var password = For(pdfPath);
        return password == null ? new PdfOpenOptions() : new PdfOpenOptions { UserPassword = password };
    }

    /// <summary>Opens <paramref name="pdfPath"/>, supplying its recorded password if it has one.</summary>
    public static PdfDocument Open(string pdfPath) =>
        PdfDocument.Open(pdfPath, OpenOptionsFor(pdfPath));

    private static IReadOnlyList<Entry> Load()
    {
        var path = ManifestPath;
        if (path == null || !File.Exists(path))
            return Array.Empty<Entry>();

        var entries = new List<Entry>();
        foreach (var raw in File.ReadLines(path, new UTF8Encoding(false)))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            var columns = line.Split('\t');
            if (columns.Length < 2 || columns[0].Trim().Length == 0)
                continue;
            entries.Add(new Entry(columns[0].Trim(), columns[1]));
        }

        return entries;
    }
}
