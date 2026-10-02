using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A test that enumerates a corpus holding a password-protected fixture must get that fixture's key
/// from <see cref="CorpusPasswords"/>, or say in its own source why it does not.
///
/// <para><b>The failure this closes.</b> <see cref="EncryptedCorpusPasswordCoverageTests"/> proves every
/// encrypted fixture has a key on file. It cannot prove a test <i>uses</i> it. A harness that opens
/// every file in <c>pdfium/</c> with no password reads r2 and r3 as "cannot open", baselines them as
/// unopenable, and goes green while the key sat in <c>tests/corpus-passwords.tsv</c>; six documents were
/// hidden that way in <see cref="RedactionCollateralHarness"/> until it was wired (#1787's ratchet then
/// surfaced them). A harness that swallows an open failure with <c>catch { continue; }</c> hides it
/// without even a baseline row.</para>
///
/// <para><b>What triggers it, mechanically.</b> A test or tool source file that (1) enumerates
/// <c>*.pdf</c>, (2) names a corpus directory in which the registry has a password-protected fixture,
/// and (3) opens documents. The set of such directories is read from the registry and the corpora, not
/// kept by hand, so a new password in <c>tests/corpus-passwords.tsv</c> widens the gate by itself.</para>
///
/// <para><b>How a file satisfies it.</b> Reference <c>CorpusPasswords</c>, or carry a line
/// <c>// corpus-passwords: &lt;why this enumerator may leave those fixtures unopened&gt;</c> of at least
/// 20 characters. The second form is for a measurement whose population is pinned by a baseline that
/// would have to be re-recorded, which is a decision to make deliberately and not as a side effect.</para>
/// </summary>
public class CorpusPasswordUsageGateTests
{
    private static readonly Regex EnumeratesPdfs = new(
        @"\.(EnumerateFiles|GetFiles)\([^;]{0,240}?pdf", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex OpensDocuments = new(
        @"PdfDocument\.(Open|Load)\(|Mutool\w*(Renderer|Extractor)|Ghostscript\w*Renderer|Pdftocairo\w*Renderer",
        RegexOptions.Compiled);

    private static readonly Regex Marker = new(
        @"//\s*corpus-passwords:\s*\S[^\r\n]{19,}", RegexOptions.Compiled);

    [Fact]
    public void EveryCorpusEnumerator_UsesTheRegistry_OrSaysWhyNot()
    {
        var checkout = TestRepoLayout.LocalCheckoutRoot;
        Assert.SkipWhen(checkout == null, "could not locate the checkout being tested");
        var corpora = TestRepoLayout.MainCheckoutRoot is { } main ? Path.Combine(main, "test-pdfs") : null;
        Assert.SkipWhen(corpora == null || !Directory.Exists(corpora),
            TestRepoLayout.AbsenceReason("test-pdfs", "test-pdfs"));

        var passwordDirs = PasswordCorpusDirs(corpora!);
        Assert.SkipWhen(passwordDirs.Count == 0,
            "no corpus directory holding a password-protected fixture is present, so there is nothing to enumerate into");

        var violations = new List<string>();
        foreach (var file in SourceFiles(checkout!))
        {
            var text = File.ReadAllText(file);
            if (!EnumeratesPdfs.IsMatch(text) || !OpensDocuments.IsMatch(text))
                continue;
            var named = passwordDirs.Where(d => NamesDirectory(text, d)).ToList();
            if (named.Count == 0)
                continue;
            if (text.Contains("CorpusPasswords", StringComparison.Ordinal) || Marker.IsMatch(text))
                continue;

            violations.Add($"{Path.GetRelativePath(checkout!, file)} (reads {string.Join(", ", named)})");
        }

        violations.Should().BeEmpty(
            "these files enumerate a corpus that holds a password-protected fixture and open documents, yet neither " +
            "use CorpusPasswords nor declare '// corpus-passwords: <why>'. An enumerator that opens without the key on " +
            "file reads those fixtures as unopenable, or drops them in a catch, and reports a smaller corpus as if it " +
            "were the whole one. Wire it (CorpusPasswords.Open / OpenOptionsFor, and the oracle's password overload) " +
            "or declare why not. Offenders: " + string.Join("; ", violations));
    }

    /// <summary>The directories under <c>test-pdfs/</c> that hold at least one fixture the registry has a password for.</summary>
    private static HashSet<string> PasswordCorpusDirs(string corporaRoot)
    {
        var wanted = CorpusPasswords.Entries
            .Select(e => e.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in Directory.EnumerateDirectories(corporaRoot))
        {
            var name = Path.GetFileName(top);
            if (name is "archives" or "manifests" or "rendering-contracts" or "baselines")
                continue;
            if (Directory.EnumerateFiles(top, "*.pdf", SearchOption.AllDirectories)
                .Any(p => wanted.Contains(Path.GetFileName(p)) && CorpusPasswords.Has(p)))
            {
                dirs.Add(name);
            }
        }

        return dirs;
    }

    private static bool NamesDirectory(string text, string dir) =>
        text.Contains($"\"{dir}\"", StringComparison.Ordinal) ||
        text.Contains($"test-pdfs/{dir}", StringComparison.Ordinal) ||
        text.Contains($"test-pdfs\\{dir}", StringComparison.Ordinal) ||
        text.Contains($"\"test-pdfs\", \"{dir}\"", StringComparison.Ordinal);

    private static IEnumerable<string> SourceFiles(string checkout)
    {
        var roots = Directory.EnumerateDirectories(checkout, "Excise.*.Tests")
            .Concat(new[] { Path.Combine(checkout, "tools") })
            .Where(Directory.Exists);
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var norm = file.Replace('\\', '/');
                if (norm.Contains("/bin/") || norm.Contains("/obj/"))
                    continue;
                if (Path.GetFileName(file) is "CorpusPasswords.cs" or "CorpusPasswordUsageGateTests.cs")
                    continue;
                yield return file;
            }
        }
    }
}
