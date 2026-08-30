using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Excise.App.Tests.Unit;

public sealed class DocumentSearchSessionTests
{
    private static readonly DocumentSearchQuery Query = new(
        "needle",
        CaseSensitive: false,
        WholeWords: false,
        UseRegex: false);

    [Fact]
    public void Begin_ReplacesAndCancelsPreviousRequest()
    {
        using var session = CreateSession();

        var first = session.Begin(Query);
        var second = session.Begin(Query with { Text = "replacement" });

        first.CancellationToken.IsCancellationRequested.Should().BeTrue();
        session.IsCurrent(first).Should().BeFalse();
        second.CancellationToken.IsCancellationRequested.Should().BeFalse();
        session.IsCurrent(second).Should().BeTrue();
    }

    [Fact]
    public void Cancel_InvalidatesCurrentRequest()
    {
        using var session = CreateSession();
        var request = session.Begin(Query);

        session.Cancel();

        request.CancellationToken.IsCancellationRequested.Should().BeTrue();
        session.IsCurrent(request).Should().BeFalse();
    }

    [Fact]
    public void Execute_StaleRequestThrowsBeforeSearching()
    {
        using var session = CreateSession();
        var stale = session.Begin(Query);
        session.Begin(Query with { Text = "replacement" });

        var act = () => session.Execute(stale, null, null, null);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Execute_WithoutAnySourceReturnsNoSource()
    {
        using var session = CreateSession();
        var request = session.Begin(Query);

        var result = session.Execute(request, null, null, null);

        result.HasSource.Should().BeFalse();
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_PrefersReadyIndexOverDifferentOpenDocument()
    {
        using var indexedDocument = CreateTextDocument("needle only in the index");
        using var openDocument = CreateTextDocument("different live document");
        var index = new DocumentTextIndex(
            indexedDocument,
            NullLogger<DocumentTextIndex>.Instance);
        await index.BuildAsync();
        using var session = CreateSession();
        var request = session.Begin(Query);

        var result = session.Execute(request, index, openDocument, null);

        result.HasSource.Should().BeTrue();
        result.Matches.Should().NotBeEmpty(
            "a ready index must take precedence over the live-document fallback");
    }

    [Fact]
    public void Dispose_CancelsCurrentAndRejectsNewRequests()
    {
        var session = CreateSession();
        var request = session.Begin(Query);

        session.Dispose();

        request.CancellationToken.IsCancellationRequested.Should().BeTrue();
        session.IsCurrent(request).Should().BeFalse();
        var act = () => session.Begin(Query);
        act.Should().Throw<ObjectDisposedException>();
    }

    private static DocumentSearchSession CreateSession() =>
        new(new PdfSearchService(NullLogger<PdfSearchService>.Instance));

    private static PdfDocument CreateTextDocument(string text)
    {
        var document = PdfDocument.CreateNew();
        var page = document.Pages.AddBlank();
        using var graphics = page.GetGraphics();
        graphics.DrawString(text, PdfFont.Helvetica(18), PdfBrush.Black, 72, 600);
        graphics.Flush();
        return document;
    }
}
