using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Excise.App.ViewModels;

namespace Excise.App.Services.Printing;

/// <summary>
/// The production <see cref="ILinuxPrintDialog"/> (#1710): excise's own
/// chooser window, shown modal to the main window.
/// </summary>
/// <remarks>
/// This is the only piece of the Linux print path that needs a window, which
/// is why it is a seam: every test — including the one that prints through a
/// real CUPS queue in a container — supplies a scripted ticket instead, the
/// same split the Windows printer uses for <c>PrintDlgExW</c>.
/// </remarks>
internal sealed class LinuxPrintChooserDialog : ILinuxPrintDialog
{
    internal const string NeedsWindowMessage =
        "The printer chooser needs the excise window, and none was found.";

    public async Task<LinuxPrintTicket> ShowAsync(
        Window? owner,
        IReadOnlyList<CupsPrintQueue> queues,
        int pageCount)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (queues.Count == 0)
            return LinuxPrintTicket.Fail(LinuxCupsDocumentPrinter.NoQueuesMessage);
        if (pageCount < 1)
            return LinuxPrintTicket.Fail(LinuxCupsDocumentPrinter.NoPagesMessage);
        if (owner == null)
            return LinuxPrintTicket.Fail(NeedsWindowMessage);

        var viewModel = new LinuxPrintDialogViewModel(queues, pageCount);
        var window = new Views.LinuxPrintDialog { DataContext = viewModel };
        await window.ShowDialog(owner);
        return viewModel.Ticket ?? LinuxPrintTicket.Cancelled;
    }
}
