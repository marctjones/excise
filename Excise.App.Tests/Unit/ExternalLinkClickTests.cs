using AwesomeAssertions;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #625: external link clicks must confirm before navigating (PDFs are a
/// phishing vector) and must never reach the confirm dialog — let alone a
/// real browser-open call — for a disallowed URI scheme. These tests cover
/// exactly the security-relevant branches (scheme gating, confirmation
/// gating, dangerous-action refusal messaging) without ever exercising the
/// real "open the browser" side effect: the "user confirms an allowlisted
/// link" path calls <see cref="Excise.App.Services.UrlOpener"/> directly with
/// no injection seam (matching this codebase's existing convention — e.g.
/// AboutWindow's OpenUrl has no direct test either), so it's out of scope
/// for an automated unit test and is a manual/integration concern instead.
/// </summary>
public class ExternalLinkClickTests
{
    /// <summary>Records ShowConfirmAsync/ShowMessageAsync calls; ShowConfirmAsync's result is settable.</summary>
    private sealed class FakeUserDialogService : IUserDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public string? LastConfirmMessage { get; private set; }
        public bool ConfirmResult { get; set; }

        public int MessageCallCount { get; private set; }
        public string? LastMessageTitle { get; private set; }
        public string? LastMessageBody { get; private set; }

        public Task ShowMessageAsync(string title, string message)
        {
            MessageCallCount++;
            LastMessageTitle = title;
            LastMessageBody = message;
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }
    }

    private static (MainWindowViewModel vm, FakeUserDialogService dialog) CreateViewModel()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dialog = new FakeUserDialogService();
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            documentService,
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
        return (vm, dialog);
    }

    [Theory]
    [InlineData("https://example.com/path?query=1")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    public async Task OpenExternalLinkCommand_AllowlistedScheme_AsksForConfirmationWithTheUrlVisible(string uri)
    {
        var (vm, dialog) = CreateViewModel();
        dialog.ConfirmResult = false; // decline — never reaches the real browser-open call

        await vm.OpenExternalLinkCommand.Execute(uri).FirstAsync();

        dialog.ConfirmCallCount.Should().Be(1,
            "an allowlisted scheme must always confirm before navigating — PDFs are a phishing vector");
        dialog.LastConfirmMessage.Should().Contain(uri,
            "the confirmation must show the actual target URL, not just \"open this link?\"");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url at all")]
    public async Task OpenExternalLinkCommand_DisallowedScheme_NeverConfirms_BlocksWithMessage(string uri)
    {
        var (vm, dialog) = CreateViewModel();
        dialog.ConfirmResult = true; // even if it *would* confirm, this path must never reach that dialog

        await vm.OpenExternalLinkCommand.Execute(uri).FirstAsync();

        dialog.ConfirmCallCount.Should().Be(0,
            "a disallowed/malformed scheme must be blocked before ever asking to confirm — " +
            "defense in depth in case the PdfLinkParser-level filter is ever bypassed or changed");
        dialog.MessageCallCount.Should().Be(1);
        dialog.LastMessageTitle.Should().Be("Link Blocked");
    }

    [Fact]
    public async Task OpenExternalLinkCommand_UserDeclines_DoesNotThrow()
    {
        var (vm, dialog) = CreateViewModel();
        dialog.ConfirmResult = false;

        // Must complete cleanly without attempting to open anything —
        // the real assertion is "no exception, and UrlOpener.Open is
        // unreachable from this branch by construction" (see the code:
        // it returns immediately after an unconfirmed ShowConfirmAsync).
        var act = async () => await vm.OpenExternalLinkCommand.Execute("https://example.com").FirstAsync();
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("Launch", "launches an external application or file")]
    [InlineData("GoToE", "navigates into an embedded file")]
    [InlineData("GoToR", "navigates into a remote file")]
    [InlineData("URI:file", "'file'")]
    public async Task ShowDangerousLinkRefusalCommand_ExplainsWhyEachActionTypeWasBlocked(
        string actionType, string expectedReasonFragment)
    {
        var (vm, dialog) = CreateViewModel();

        await vm.ShowDangerousLinkRefusalCommand.Execute(actionType).FirstAsync();

        dialog.MessageCallCount.Should().Be(1);
        dialog.LastMessageTitle.Should().Be("Link Blocked");
        dialog.LastMessageBody.Should().Contain(expectedReasonFragment,
            "the refusal message should explain *why* this specific action type was blocked, " +
            "not just that something was blocked");
    }
}
