namespace Excise.Avalonia.Controls;

/// <summary>
/// The mode-switch handoff (#693, #1842, <c>docs/architecture/pdf-viewer-control-architecture.md</c>
/// §3.2): the outgoing view reports how far into the current page the reader is, and the
/// incoming view shows the current page at that fraction.
/// </summary>
/// <remarks>
/// Only the fraction crosses, not a page: the incoming view reads the viewer's
/// <c>CurrentPage</c> when it actually scrolls, which is later than the switch, so a
/// navigation issued in between is not overwritten by a page captured at the switch.
/// That is why neither <c>ReadingAnchor</c> nor a (page, fraction) record is used here.
/// </remarks>
internal interface IReadingPositionSource
{
    /// <summary>The fraction of the current page above the viewport top, in [0, 0.99].</summary>
    double CaptureIntraPageFraction();

    /// <summary>Show the current page with <paramref name="intraPageFraction"/> of it above the viewport top.</summary>
    void ShowAt(double intraPageFraction);
}
