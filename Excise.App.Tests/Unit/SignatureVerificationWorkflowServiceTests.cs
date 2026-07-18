using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Excise.App.Tests.Unit;

public class SignatureVerificationWorkflowServiceTests
{
    [Fact]
    public async Task VerifyAsync_WhenNoDocumentLoaded_ShowsOpenDocumentMessage()
    {
        var dialog = new RecordingDialogService();
        var workflow = CreateWorkflow(dialog);

        await workflow.VerifyAsync(isDocumentLoaded: false, currentFilePath: null);

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].Title.Should().Be("Verify Signatures");
        dialog.Messages[0].Message.Should().Be("Open a PDF before verifying signatures.");
    }

    [Fact]
    public async Task VerifyAsync_WhenDocumentHasNoSignatures_ShowsNoSignaturesMessage()
    {
        var dialog = new RecordingDialogService();
        var workflow = CreateWorkflow(dialog);
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.pdf");

        try
        {
            TestPdfGenerator.CreateSimpleTextPdf(path, "Unsigned PDF");

            await workflow.VerifyAsync(isDocumentLoaded: true, currentFilePath: path);

            dialog.Messages.Should().ContainSingle();
            dialog.Messages[0].Title.Should().Be("Verify Signatures");
            dialog.Messages[0].Message.Should().Be("No digital signatures were found in this document.");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task VerifyAsync_WhenVerificationReturnsError_ShowsFormattedFailureSummary()
    {
        var dialog = new RecordingDialogService();
        var workflow = CreateWorkflow(dialog);

        await workflow.VerifyAsync(isDocumentLoaded: true, currentFilePath: "/definitely/not/a/document.pdf");

        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].Title.Should().Be("Verify Signatures");
        dialog.Messages[0].Message.Should().Contain("Signature: unknown");
        dialog.Messages[0].Message.Should().Contain("CMS signature check: failed");
        dialog.Messages[0].Message.Should().Contain("Certificate trust chain: not evaluated");
    }

    private static SignatureVerificationWorkflowService CreateWorkflow(IUserDialogService dialog) =>
        new(
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new SignatureVerificationSummaryFormatter(),
            dialog,
            NullLogger<SignatureVerificationWorkflowService>.Instance);

    private sealed class RecordingDialogService : IUserDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
