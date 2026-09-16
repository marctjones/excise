using System;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.Automation;

/// <summary>
/// What a scenario step can ask the application to do (#1497).
/// </summary>
/// <remarks>
/// The seam exists so <see cref="PerfScenarioRunner"/>'s sequencing — step
/// order, boundary journalling, timeouts, the metrics markers — is testable
/// without a compositor, a display, or a 122 MB fixture. The live
/// implementation is <c>AppPerfScenarioTarget</c>.
/// </remarks>
internal interface IPerfScenarioTarget
{
    /// <summary>Open a document by path. Awaits the open completing.</summary>
    Task OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Close the current document.
    /// </summary>
    /// <remarks>
    /// ⚠️ The real close path calls <c>ConfirmDiscardUnsavedChangesAsync</c>
    /// first, which shows a modal dialog when the document is dirty. A scenario
    /// that mutates (redact, rotate) and then closes would wait on a dialog no
    /// one is there to answer — which is why every step is bounded by a timeout
    /// and why the shipped scenarios never close a mutated document.
    /// </remarks>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Wait until the viewer is quiescent. Returns false on timeout.
    /// </summary>
    Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Advance the current page index by <paramref name="pages"/>.</summary>
    Task PageByAsync(int pages, CancellationToken cancellationToken);

    /// <summary>Scroll the viewport through <paramref name="pages"/> page heights.</summary>
    Task ScrollPagesAsync(int pages, CancellationToken cancellationToken);

    /// <summary>Set the zoom level.</summary>
    Task SetZoomAsync(double zoom, CancellationToken cancellationToken);

    /// <summary>Set the view mode: <c>single</c> or <c>continuous</c>.</summary>
    Task SetViewModeAsync(string mode, CancellationToken cancellationToken);

    /// <summary>Run a search and wait for it to settle.</summary>
    Task SearchAsync(string term, CancellationToken cancellationToken);

    /// <summary>Mark redactions for a term.</summary>
    Task RedactTextAsync(string term, CancellationToken cancellationToken);

    /// <summary>
    /// Request a viewer cache trim in process at <paramref name="level"/>
    /// (<c>background</c> | <c>warn</c> | <c>critical</c>).
    /// </summary>
    /// <returns>
    /// False when the request could not be made — most importantly when the
    /// user's own Preferences → Performance policy has pressure trims switched
    /// off, in which case the coordinator drops it silently. The runner records
    /// that as a step note instead of reporting a trim that never happened.
    /// </returns>
    Task<bool> TrimAsync(string level, CancellationToken cancellationToken);

    /// <summary>Take an in-process sample, including the viewer's counters.</summary>
    PerfSample Sample();
}
