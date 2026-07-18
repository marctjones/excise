using AwesomeAssertions;
using Excise.App;
using Xunit;

namespace Excise.App.Tests.Unit;

public class StartupDocumentResolverTests
{
    [Fact]
    public void Resolve_UsesLifetimePdfArgFirst()
    {
        using var lifetimePdf = TestPdfFile();
        using var processPdf = TestPdfFile();

        var result = StartupDocumentResolver.Resolve(
            new[] { lifetimePdf.Path },
            new[] { processPdf.Path });

        result.Should().Be(Path.GetFullPath(lifetimePdf.Path));
    }

    [Fact]
    public void Resolve_FallsBackToProcessPdfArg()
    {
        using var processPdf = TestPdfFile();

        var result = StartupDocumentResolver.Resolve(
            Array.Empty<string>(),
            new[] { processPdf.Path });

        result.Should().Be(Path.GetFullPath(processPdf.Path));
    }

    [Fact]
    public void Resolve_IgnoresMacProcessSerialNumberAndOptions()
    {
        using var pdf = TestPdfFile();

        var result = StartupDocumentResolver.Resolve(
            null,
            new[] { "-psn_0_12345", "--ignored", pdf.Path });

        result.Should().Be(Path.GetFullPath(pdf.Path));
    }

    [Fact]
    public void Resolve_AcceptsFileUri()
    {
        using var pdf = TestPdfFile();
        var uri = new Uri(pdf.Path).AbsoluteUri;

        var result = StartupDocumentResolver.Resolve(null, new[] { uri });

        result.Should().Be(Path.GetFullPath(pdf.Path));
    }

    [Fact]
    public void Resolve_IgnoresMissingAndNonPdfFiles()
    {
        using var text = TempFile(".txt");

        var result = StartupDocumentResolver.Resolve(
            new[] { "/tmp/does-not-exist.pdf" },
            new[] { text.Path });

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveResponsivenessReportPath_AcceptsSeparatedOrEqualsOption()
    {
        var separated = Path.Combine(Path.GetTempPath(), $"excise-report-{Guid.NewGuid():N}.json");
        var equals = Path.Combine(Path.GetTempPath(), $"excise-report-{Guid.NewGuid():N}.json");

        StartupDocumentResolver.ResolveResponsivenessReportPath(
                new[] { "--responsiveness-report", separated },
                new[] { $"--responsiveness-report={equals}" })
            .Should().Be(Path.GetFullPath(separated));

        StartupDocumentResolver.ResolveResponsivenessReportPath(
                Array.Empty<string>(),
                new[] { $"--responsiveness-report={equals}" })
            .Should().Be(Path.GetFullPath(equals));
    }

    private static TempPath TestPdfFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-startup-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, "%PDF-1.4\n%%EOF\n"u8.ToArray());
        return new TempPath(path);
    }

    private static TempPath TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-startup-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "not a pdf");
        return new TempPath(path);
    }

    private sealed class TempPath : IDisposable
    {
        public TempPath(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
