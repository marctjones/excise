using System;
using Excise.Core.Document;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// #1547 phase 1: tell the user when a document is an XFA form.
/// </summary>
/// <remarks>
/// A dynamic XFA form shows only its placeholder page ("Please wait...") in any
/// viewer without an XFA engine, excise included. Without a notice the user
/// sees a broken-looking document and no explanation. The notice is a
/// persistent, closable banner rather than a toast: the placeholder page stays
/// on screen, so the explanation must too.
/// </remarks>
public partial class MainWindowViewModel
{
    internal const string DynamicXfaNoticeTitle = "This PDF is a dynamic XFA form.";
    internal const string DynamicXfaNoticeMessage =
        "excise can't display this kind of form yet. Open it in Adobe Acrobat Reader or Firefox to see and fill it.";
    internal const string StaticXfaNoticeTitle = "This form also contains XFA data.";
    internal const string StaticXfaNoticeMessage =
        "excise fills the standard form fields. Adobe Acrobat may show the XFA copy of the values instead.";

    private PdfXfaFormKind _xfaFormKind;
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

    /// <summary>Whether the XFA banner is showing. The banner's close button clears it.</summary>
    public bool IsXfaNoticeOpen
    {
        get => _isXfaNoticeOpen;
        set => this.RaiseAndSetIfChanged(ref _isXfaNoticeOpen, value);
    }

    public string XfaNoticeTitle => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeTitle,
        PdfXfaFormKind.Static => StaticXfaNoticeTitle,
        _ => string.Empty,
    };

    public string XfaNoticeMessage => _xfaFormKind switch
    {
        PdfXfaFormKind.Dynamic => DynamicXfaNoticeMessage,
        PdfXfaFormKind.Static => StaticXfaNoticeMessage,
        _ => string.Empty,
    };

    public FAInfoBarSeverity XfaNoticeSeverity => _xfaFormKind == PdfXfaFormKind.Dynamic
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

        XfaFormKind = kind;
        IsXfaNoticeOpen = kind != PdfXfaFormKind.None;
        if (kind != PdfXfaFormKind.None)
            _logger.LogInformation("Document is a {Kind} XFA form", kind);
    }

    private void ClearXfaNotice()
    {
        XfaFormKind = PdfXfaFormKind.None;
        IsXfaNoticeOpen = false;
    }
}
