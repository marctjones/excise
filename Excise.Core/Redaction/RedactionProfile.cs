namespace Excise.Core.Text.Segmentation;

/// <summary>
/// How much of a document's non-page machinery a redaction removes (#1586).
/// </summary>
/// <remarks>
/// <para><b>Why a profile and not just more flags.</b> The individual removals
/// are flags on <see cref="RedactionOptions"/>, and the engine reads ONLY those
/// flags — this enum never reaches a branch in the engine. It exists so a front
/// end can offer the two settings a person actually chooses between, and so the
/// report can say which one was asked for. Build the options with
/// <see cref="RedactionOptions.ForProfile"/>; change a flag afterwards and the
/// flags win, which is why the report also lists what was removed rather than
/// naming the profile alone.</para>
/// </remarks>
public enum RedactionProfile
{
    /// <summary>
    /// The default on every path (GUI, CLI, batch, scripting, library).
    /// Removes the hidden machinery that has no accessibility or navigation
    /// value and can restate the page: all JavaScript and every external-effect
    /// action (Launch, SubmitForm, ImportData, GoToR, GoToE — internal GoTo and
    /// Named navigation stay), <c>/PieceInfo</c>, page <c>/Thumb</c> images,
    /// content in optional-content layers that are OFF by default, appearance
    /// streams of hidden annotations, attachments (#1572), the XFA packet
    /// (#1574), and the document <c>/Info</c> and XMP packet — keeping only the
    /// PDF/A and PDF/UA identifications (#1507/#1586).
    ///
    /// <para>Accessibility and navigation carriers are KEPT and term-scrubbed:
    /// <c>/TU</c>, <c>/Alt</c>, <c>/ActualText</c>, <c>/E</c>,
    /// structure-element <c>/T</c>, field names, bookmark titles, link
    /// targets.</para>
    /// </summary>
    Standard,

    /// <summary>
    /// Everything <see cref="Standard"/> removes, plus every KEPT carrier
    /// removed whole rather than term-scrubbed, bookmarks, link annotations,
    /// markup/comment annotations and field names stripped, and forms and
    /// annotations flattened into the page.
    ///
    /// <para>⚠️ The output is no longer accessible or interactive, and
    /// <see cref="RedactionReport.AccessibilityAndInteractivityRemoved"/> says
    /// so. An explicit choice, never a default.</para>
    /// </summary>
    Maximum,
}
