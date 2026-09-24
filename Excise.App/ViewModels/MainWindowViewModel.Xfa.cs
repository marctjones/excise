using System;
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
public partial class MainWindowViewModel
{
    internal const string DynamicXfaNoticeTitle = "This PDF is a dynamic XFA form.";
    internal const string DynamicXfaNoticeMessage =
        "excise can't display this kind of form yet. Open it in Adobe Acrobat Reader or Firefox to see and fill it.";
    internal const string LaidOutXfaNoticeTitle = "This PDF is a dynamic XFA form.";
    internal const string LaidOutXfaNoticeMessage =
        "excise shows the form's layout and runs its FormCalc calculations. JavaScript and button scripts don't run and its fields can't be filled here yet, so open it in Adobe Acrobat Reader or Firefox to fill it in.";
    internal const string StaticXfaNoticeTitle = "This form also contains XFA data.";
    internal const string StaticXfaNoticeMessage =
        "excise fills the standard form fields. Adobe Acrobat may show the XFA copy of the values instead.";

    private PdfXfaFormKind _xfaFormKind;
    private bool _isXfaFormLaidOut;
    private bool _isXfaNoticeOpen;

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

    public string XfaNoticeTitle => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut => LaidOutXfaNoticeTitle,
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeTitle,
        PdfXfaFormKind.Static => StaticXfaNoticeTitle,
        _ => string.Empty,
    };

    public string XfaNoticeMessage => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic when _isXfaFormLaidOut => LaidOutXfaNoticeMessage,
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeMessage,
        PdfXfaFormKind.Static => StaticXfaNoticeMessage,
        _ => string.Empty,
    };

    public FAInfoBarSeverity XfaNoticeSeverity => _xfaFormKind == PdfXfaFormKind.Dynamic && !_isXfaFormLaidOut
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
        XfaFormKind = kind;
        IsXfaNoticeOpen = kind != PdfXfaFormKind.None;
        if (kind != PdfXfaFormKind.None)
            _logger.LogInformation("Document is a {Kind} XFA form", kind);
    }

    private void ClearXfaNotice()
    {
        IsXfaFormLaidOut = false;
        XfaFormKind = PdfXfaFormKind.None;
        IsXfaNoticeOpen = false;
    }
}
