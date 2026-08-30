using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excise.App.Tests.Unit;

public sealed class DocumentTextIndexSessionTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public async Task Start_WithNoDelay_BuildsAndExposesCurrentIndex()
    {
        using var document = OpenDocument(pageCount: 3);
        using var session = new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance, TimeSpan.Zero);

        session.Start(document);
        await session.BuildCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        session.Current.Should().NotBeNull();
        session.Current!.PageCount.Should().Be(3);
        session.Current.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Start_ReplacesCurrentAndCancelsPreviousDelayedBuild()
    {
        using var firstDocument = OpenDocument(pageCount: 2);
        using var secondDocument = OpenDocument(pageCount: 4);
        using var session = new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance, TimeSpan.FromHours(1));

        session.Start(firstDocument);
        var firstBuild = session.BuildCompletion;
        var firstIndex = session.Current;

        session.Start(secondDocument);
        await firstBuild.WaitAsync(TimeSpan.FromSeconds(2));

        session.Current.Should().NotBeSameAs(firstIndex);
        session.Current!.PageCount.Should().Be(4);
        session.Current.IsReady.Should().BeFalse("the replacement still has the deliberate build delay");
    }

    [Fact]
    public async Task Cancel_ClearsCurrentAndCompletesDelayedBuild()
    {
        using var document = OpenDocument(pageCount: 2);
        using var session = new DocumentTextIndexSession(
            NullLogger<DocumentTextIndexSession>.Instance, TimeSpan.FromHours(1));
        session.Start(document);
        var build = session.BuildCompletion;

        session.Cancel();
        await build.WaitAsync(TimeSpan.FromSeconds(2));

        session.Current.Should().BeNull();
    }

    private PdfDocument OpenDocument(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-index-session-{Guid.NewGuid():N}.pdf");
        _files.Add(path);
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        return PdfDocument.Open(path);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
