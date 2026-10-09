using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// #1547: tell the user when a document is an XFA form, and what excise shows
/// of it.
/// </summary>
/// <remarks>
/// A dynamic XFA form holds only a placeholder page ("Please wait...") for
/// viewers without an XFA engine. Since phase 2 excise lays the form out on
/// open (<see cref="Services.PdfDocumentService.XfaLayout"/>); the banner then
/// says that only FormCalc calculations run. When the layout fails the placeholder stays
/// and so does the phase-1 warning. The notice is a persistent, closable
/// banner rather than a toast, because what it explains stays on screen.
/// </remarks>
internal partial class MainWindowViewModel
{
    internal const string DynamicXfaNoticeTitle = "This PDF is a dynamic XFA form.";
    internal const string DynamicXfaNoticeMessage =
        "excise can't display this kind of form yet. Open it in Adobe Acrobat Reader or Firefox to see and fill it.";
    internal const string LaidOutXfaNoticeTitle = "This PDF is a dynamic XFA form.";
    internal const string LaidOutXfaNoticeMessage =
        "excise shows the form's layout and runs its FormCalc calculations. Layout is experimental: content may be missing or moved between pages. JavaScript and button scripts don't run and its fields can't be filled here yet. Verify the complete form and fill it in using Adobe Acrobat Reader or Firefox.";
    internal const string IncompleteXfaNoticeTitle = "This XFA form may be incomplete.";
    internal const string IncompleteXfaNoticeMessage =
        "excise detected layout omissions or failed calculations. Content may be missing, clipped, or moved between pages. Do not rely on this view as the complete form; verify it in Adobe Acrobat Reader or Firefox. JavaScript and button scripts don't run and its fields can't be filled here yet.";
    /// <summary>#2024: appended when the laid-out form is certified; every save removes the certification.</summary>
    internal const string CertifiedXfaNoticeSuffix =
        " This form is certified by its author. A copy excise saves is a new file that can't keep that certification or the Adobe Reader features it enables, so saving removes them.";
    /// <summary>#2024: the toast after a save that removed a certification.</summary>
    internal const string CertificationRemovedToastTitle = "Certification removed from the saved copy";
    internal const string StaticXfaNoticeTitle = "This form also contains XFA data.";
    internal const string StaticXfaNoticeMessage =
        "excise fills the standard form fields. Adobe Acrobat may show the XFA copy of the values instead.";

    private PdfXfaFormKind _xfaFormKind;
    private bool _isXfaFormLaidOut;
    private bool _isXfaNoticeOpen;
    private bool _hasXfaLayoutWarnings;
    private bool _xfaCertificationRemovedOnSave;

    /// <summary>The open document's XFA classification.</summary>
    public PdfXfaFormKind XfaFormKind
    {
        get => _xfaFormKind;
        private set
        {
            this.RaiseAndSetIfChanged(ref _xfaFormKind, value);
            this.RaisePropertyChanged(nameof(XfaNoticeTitle));
            this.RaisePropertyChanged(nameof(XfaNoticeMessage));
            this.RaisePropertyChanged(nameof(XfaNoticeSeverity));
        }
    }

    /// <summary>
    /// Whether the pages on screen are excise's layout of the dynamic XFA form
    /// rather than the document's placeholder pages.
    /// </summary>
    public bool IsXfaFormLaidOut
    {
        get => _isXfaFormLaidOut;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isXfaFormLaidOut, value);
            this.RaisePropertyChanged(nameof(XfaNoticeTitle));
            this.RaisePropertyChanged(nameof(XfaNoticeMessage));
            this.RaisePropertyChanged(nameof(XfaNoticeSeverity));
        }
    }

    /// <summary>Whether the XFA banner is showing. The banner's close button clears it.</summary>
    public bool IsXfaNoticeOpen
    {
        get => _isXfaNoticeOpen;
        set => this.RaiseAndSetIfChanged(ref _isXfaNoticeOpen, value);
    }

    /// <summary>#1824: layout success does not mean all form content is shown.</summary>
    public bool HasXfaLayoutWarnings
    {
        get => _hasXfaLayoutWarnings;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasXfaLayoutWarnings, value);
            this.RaisePropertyChanged(nameof(XfaNoticeTitle));
            this.RaisePropertyChanged(nameof(XfaNoticeMessage));
            this.RaisePropertyChanged(nameof(XfaNoticeSeverity));
        }
    }

    public string XfaNoticeTitle => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut && _hasXfaLayoutWarnings => IncompleteXfaNoticeTitle,
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut => LaidOutXfaNoticeTitle,
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeTitle,
        PdfXfaFormKind.Static => StaticXfaNoticeTitle,
        _ => string.Empty,
    };

    public string XfaNoticeMessage => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut && _hasXfaLayoutWarnings => IncompleteXfaNoticeMessage + CertifiedSuffix,
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut => LaidOutXfaNoticeMessage + CertifiedSuffix,
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeMessage,
        PdfXfaFormKind.Static => StaticXfaNoticeMessage,
        _ => string.Empty,
    };

    private string CertifiedSuffix => _xfaCertificationRemovedOnSave ? CertifiedXfaNoticeSuffix : string.Empty;

    public FAInfoBarSeverity XfaNoticeSeverity => _xfaFormKind == PdfXfaFormKind.Dynamic
        && (!_isXfaFormLaidOut || _hasXfaLayoutWarnings)
        ? FAInfoBarSeverity.Warning
        : FAInfoBarSeverity.Informational;

    /// <summary>Classify the open document and show or hide the banner.</summary>
    private void RefreshXfaNotice()
    {
        var kind = PdfXfaFormKind.None;
        if (PdfCoreDocument is { } document)
        {
            try
            {
                kind = document.DetectXfaForm();
            }
            catch (Exception ex)
            {
                // A notice must never stop a document opening.
                _logger.LogWarning(ex, "XFA detection failed");
            }
        }

        IsXfaFormLaidOut = kind == PdfXfaFormKind.Dynamic
            && _documentService.XfaLayout is { ShowsForm: true };
        HasXfaLayoutWarnings = IsXfaFormLaidOut && _documentService.XfaLayout is { } layout
            && (layout.Omissions.Count > 0 || layout.ScriptFailures.Count > 0);
        _xfaCertificationRemovedOnSave = IsXfaFormLaidOut
            && _documentService.XfaLayout is { CertificationRemovedOnSave: true };
        this.RaisePropertyChanged(nameof(XfaNoticeMessage));
        XfaFormKind = kind;
        IsXfaNoticeOpen = kind != PdfXfaFormKind.None;
        if (kind != PdfXfaFormKind.None)
            _logger.LogInformation("Document is a {Kind} XFA form", kind);
    }

    /// <summary>
    /// #2024: say what a save removed of the form's certification (the strip runs inside the save,
    /// see <see cref="Excise.Core.Signatures.CertificationStripper"/>). Nothing when nothing was removed.
    /// </summary>
    private void ReportCertificationRemovals(IReadOnlyList<string>? removals)
    {
        var lines = Excise.Core.Signatures.CertificationStripper.Describe(removals).ToList();
        if (lines.Count == 0)
            return;
        // The document on screen, reloaded or stripped in place, is no longer certified.
        _xfaCertificationRemovedOnSave = false;
        this.RaisePropertyChanged(nameof(XfaNoticeMessage));
        _logger.LogInformation("Save removed the certification: {Removals}", string.Join("; ", lines.Skip(1)));
        _toastService.ShowWarning(CertificationRemovedToastTitle, string.Join(Environment.NewLine, lines));
    }

    private void ClearXfaNotice()
    {
        _xfaCertificationRemovedOnSave = false;
        IsXfaFormLaidOut = false;
        HasXfaLayoutWarnings = false;
        XfaFormKind = PdfXfaFormKind.None;
        IsXfaNoticeOpen = false;
    }
}
