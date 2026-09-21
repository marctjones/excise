using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1710: <see cref="LinuxCupsDocumentPrinter"/> against a REAL CUPS
/// scheduler. Nothing here is faked but the chooser window: a real
/// <c>cupsd</c>, a real queue, real <c>lpstat</c> and <c>lp</c> subprocesses,
/// and the PDF the queue produces is counted by <b>qpdf or mutool</b> — never
/// by excise, which would be the tool vouching for itself.
/// </summary>
/// <remarks>
/// <para>
/// It runs wherever CUPS is installed with a working queue, and is inert
/// elsewhere with a declared skip reason (#1172) naming exactly what is
/// missing. In practice that means a container:
/// <c>scripts/run-linux-print-test.sh</c> builds one (ubuntu 24.04, cups +
/// cups-pdf, <c>cupsd</c> started WITHOUT systemd, a queue created with
/// <c>lpadmin</c> because 24.04 no longer creates one automatically) and runs
/// this class inside it.
/// </para>
/// <para>
/// <b>Two things the environment must tell the test, because it cannot know
/// them.</b> <c>EXCISE_CUPS_TEST_QUEUE</c> names the queue to print to, and
/// <c>EXCISE_CUPS_TEST_OUTPUT_DIR</c> says where that queue drops its PDFs —
/// cups-pdf writes to <c>${HOME}/PDF</c> by default, not the spool directory,
/// and the #1710 CI spike reported a false negative by looking in the wrong
/// place. The runner sets <c>Out</c> in <c>cups-pdf.conf</c> and passes the
/// same path here, so neither side guesses.
/// </para>
/// <para>
/// <b><c>lp</c> returning is not the job finishing.</b> Submission is
/// asynchronous: the filter chain runs afterwards, so every assertion below
/// waits for a file to appear and stop growing, with a bounded timeout.
/// </para>
/// </remarks>
public class LinuxCupsPrintIntegrationTests : IDisposable
{
    /// <summary>The queue this test prints to. Set by the container runner.</summary>
    internal const string QueueVariable = "EXCISE_CUPS_TEST_QUEUE";

    /// <summary>Where that queue drops finished PDFs. Set by the container runner.</summary>
    internal const string OutputDirectoryVariable = "EXCISE_CUPS_TEST_OUTPUT_DIR";

    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(90);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excise-1710-cups", Guid.NewGuid().ToString("N"));

    public LinuxCupsPrintIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string? Queue => Environment.GetEnvironmentVariable(QueueVariable);

    private static string? OutputDirectory => Environment.GetEnvironmentVariable(OutputDirectoryVariable);

    /// <summary>
    /// Skip with a reason that names what is absent, and never claim the
    /// absence of something present (#1527).
    /// </summary>
    private static void RequireCups()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CUPS printing is a Linux path; this host is not Linux");
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(Queue) && !string.IsNullOrWhiteSpace(OutputDirectory),
            $"no CUPS test queue configured: {QueueVariable} and {OutputDirectoryVariable} are unset " +
            "(scripts/run-linux-print-test.sh sets both inside its container)");
        Assert.SkipUnless(
            Directory.Exists(OutputDirectory!),
            $"the configured CUPS output directory does not exist [excise-searched: {OutputDirectory}]");
    }

    /// <summary>A PDF whose page count is unmistakable and whose pages differ.</summary>
    private string CreateFixture(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        using var doc = PdfDocument.CreateNew();
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.Pages.AddBlank();
            using var g = page.GetGraphics();
            g.DrawString($"CUPS1710 PAGE {i} OF {pages}", PdfFont.Helvetica(24), PdfBrush.Black, 72, 640);
            g.Flush();
        }
        doc.Save(path);
        return path;
    }

    private static LinuxCupsDocumentPrinter Printer(ILinuxPrintDialog dialog) =>
        new(new CupsProcessRunner(), dialog, NullLogger.Instance);

    private async Task<DocumentPrintResult> PrintAsync(string pdfPath, LinuxPrintTicket ticket) =>
        await Printer(new ScriptedLinuxPrintDialog(ticket)).PrintAsync(
            new DocumentPrintRequest(pdfPath, Path.GetFileNameWithoutExtension(pdfPath), PrintScalingMode.ActualSize, null));

    // ── the queue really is there ──────────────────────────────────────

    [Fact]
    public void ListQueues_FindsTheConfiguredQueue()
    {
        RequireCups();

        var (queues, error) = Printer(new ScriptedLinuxPrintDialog(LinuxPrintTicket.Cancelled)).ListQueues();

        error.Should().BeNull("lpstat must reach a running cupsd");
        queues.Select(q => q.Name).Should().Contain(Queue!,
            "the queue the runner created with lpadmin must be enumerated by the printer under test");
    }

    // ── a real job, counted by a tool that is not excise ───────────────

    [Fact]
    public async Task Print_SevenPageDocument_ProducesASevenPagePdf()
    {
        RequireCups();

        var before = Snapshot();
        var result = await PrintAsync(CreateFixture("seven.pdf", 7), LinuxPrintTicket.Print(Queue!));

        result.Outcome.Should().Be(DocumentPrintOutcome.Printed, result.Error);
        var produced = await WaitForNewOutputAsync(before);
        PageCountByIndependentTool(produced).Should().Be(7,
            "seven pages in, seven pages out — counted by qpdf/mutool, never by excise");
    }

    [Fact]
    public async Task Print_PageRange_ProducesOnlyThosePages()
    {
        RequireCups();

        var before = Snapshot();
        var result = await PrintAsync(
            CreateFixture("range.pdf", 7),
            LinuxPrintTicket.Print(Queue!, [new PrintPageRange(2, 4)]));

        result.Outcome.Should().Be(DocumentPrintOutcome.Printed, result.Error);
        var produced = await WaitForNewOutputAsync(before);
        PageCountByIndependentTool(produced).Should().Be(3, "pages 2-4 of a 7-page document is 3 sheets");
    }

    [Fact]
    public async Task Print_TwoCopies_DoublesTheSheets()
    {
        RequireCups();

        var before = Snapshot();
        var result = await PrintAsync(
            CreateFixture("copies.pdf", 3), LinuxPrintTicket.Print(Queue!, copies: 2));

        result.Outcome.Should().Be(DocumentPrintOutcome.Printed, result.Error);
        var produced = await WaitForNewOutputAsync(before);
        // A file-backed queue has no hardware copies, so CUPS's own pdftopdf
        // filter duplicates the pages into the one output file. This asserts
        // what CUPS ACTUALLY does with -n, measured, not what a hardware
        // printer would do.
        PageCountByIndependentTool(produced).Should().Be(6, "two copies of three pages through -n");
    }

    // ── failing loudly against a real scheduler ────────────────────────

    [Fact]
    public async Task Print_ToAQueueThatDoesNotExist_FailsLoudly()
    {
        RequireCups();

        var before = Snapshot();
        var result = await PrintAsync(
            CreateFixture("missing-queue.pdf", 2),
            LinuxPrintTicket.Print("excise-no-such-queue-1710"));

        result.Outcome.Should().Be(DocumentPrintOutcome.Failed,
            "an unknown destination must never be reported as a successful print");
        result.Error.Should().StartWith("The printer did not accept the job:");
        result.Error.Should().Contain("excise-no-such-queue-1710", "the message must name the queue CUPS rejected");
        Snapshot().Except(before).Should().BeEmpty("a refused job produces no output");
    }

    // ── output-directory plumbing ──────────────────────────────────────

    private static HashSet<string> Snapshot() =>
        Directory.Exists(OutputDirectory)
            ? Directory.EnumerateFiles(OutputDirectory!, "*.pdf", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Wait for exactly one new PDF to appear in the queue's output directory
    /// and stop growing. <c>lp</c> returns as soon as the job is spooled, so
    /// polling is the only correct way to observe the result.
    /// </summary>
    private static async Task<string> WaitForNewOutputAsync(HashSet<string> before)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        string? candidate = null;
        long lastLength = -1;
        int stableReads = 0;

        while (DateTime.UtcNow < deadline)
        {
            var added = Snapshot().Except(before).ToArray();
            if (added.Length > 0)
            {
                candidate = added.OrderBy(File.GetLastWriteTimeUtc).Last();
                long length = new FileInfo(candidate).Length;
                stableReads = length > 0 && length == lastLength ? stableReads + 1 : 0;
                lastLength = length;
                if (stableReads >= 2)
                    return candidate;
            }
            await Task.Delay(250);
        }

        throw new TimeoutException(
            candidate == null
                ? $"No PDF appeared in {OutputDirectory} within {JobTimeout.TotalSeconds:0} s. " +
                  $"CUPS job state: {RunTool("lpstat", ["-W", "all", "-o"])}"
                : $"{candidate} never stopped growing within {JobTimeout.TotalSeconds:0} s.");
    }

    // ── the independent oracles ────────────────────────────────────────

    /// <summary>
    /// The page count of <paramref name="path"/> according to qpdf, or mutool
    /// when qpdf is absent. Deliberately NOT excise: a print path that
    /// verified its own output would prove only that its bugs agree with
    /// themselves.
    /// </summary>
    private static int PageCountByIndependentTool(string path)
    {
        var qpdf = RunTool("qpdf", ["--show-npages", path]);
        if (qpdf != null && int.TryParse(qpdf.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pages))
            return pages;

        // mutool info prints a line like "Pages: 7".
        var mutool = RunTool("mutool", ["info", path]);
        if (mutool != null)
        {
            foreach (var line in mutool.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(trimmed["Pages:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pages))
                    return pages;
            }
        }

        throw new InvalidOperationException(
            $"Neither qpdf nor mutool could count the pages of {path}; this test must not fall back on excise.");
    }

    private static string? RunTool(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["LC_ALL"] = "C";

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;
            var output = new StringBuilder();
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (SystemException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.ToString() : null;
        }
        catch (SystemException)
        {
            return null;
        }
    }
}
