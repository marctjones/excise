using System.Reactive.Linq;
using AwesomeAssertions;
using Excise.App.Services.Printing;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1992 — Bates, Reduce File Size and the Linux print chooser close through
/// view-model commands (CloseRequested), not Click handlers. Confirm must record
/// the choice before the window closes, Cancel must close without it, and Print
/// must keep the window open while the entry is refused.
/// </summary>
public class DialogCloseCommandTests
{
    [Fact]
    public async Task Bates_Confirm_RecordsThenRequestsClose()
    {
        var vm = new BatesNumberingDialogViewModel();
        var confirmedWhenClosed = (bool?)null;
        vm.CloseRequested += (_, _) => confirmedWhenClosed = vm.Confirmed;

        await vm.ConfirmCommand.Execute();

        confirmedWhenClosed.Should().BeTrue("the caller reads Confirmed after the window closes");
    }

    [Fact]
    public async Task Bates_Cancel_ClosesWithoutConfirming()
    {
        var vm = new BatesNumberingDialogViewModel();
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.CancelCommand.Execute();

        closes.Should().Be(1);
        vm.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task ReduceFileSize_ConfirmAndCancel_CloseOnceEach()
    {
        var confirm = new ReduceFileSizeDialogViewModel();
        var confirmCloses = 0;
        confirm.CloseRequested += (_, _) => confirmCloses++;
        await confirm.ConfirmCommand.Execute();
        confirmCloses.Should().Be(1);
        confirm.Confirmed.Should().BeTrue();

        var cancel = new ReduceFileSizeDialogViewModel();
        var cancelCloses = 0;
        cancel.CloseRequested += (_, _) => cancelCloses++;
        await cancel.CancelCommand.Execute();
        cancelCloses.Should().Be(1);
        cancel.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task LinuxPrint_RefusedPageRange_KeepsTheWindowOpen()
    {
        var vm = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 3)
        {
            PageRangeText = "7-9",
        };
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.PrintCommand.Execute();

        closes.Should().Be(0, "a typo must be corrected, not turned into a different job (#1710)");
        vm.ValidationError.Should().NotBeNullOrEmpty();
        vm.Ticket.Should().BeNull();
    }

    [Fact]
    public async Task LinuxPrint_ValidEntry_ClosesWithATicket()
    {
        var vm = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 3);
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.PrintCommand.Execute();

        closes.Should().Be(1);
        vm.Ticket.Should().NotBeNull();
    }

    [Fact]
    public async Task LinuxPrint_Cancel_ClosesWithNoTicket()
    {
        var vm = new LinuxPrintDialogViewModel([new CupsPrintQueue("Q", "idle", true)], pageCount: 3);
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.CancelCommand.Execute();

        closes.Should().Be(1);
        vm.Ticket.Should().BeNull();
    }
}
