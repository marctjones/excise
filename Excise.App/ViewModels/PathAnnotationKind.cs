namespace Excise.App.ViewModels;

/// <summary>
/// Which annotation a path drawn on the page becomes (#934 D, E).
///
/// The capture, the DIP -> content conversion and the viewer event are shared
/// across all of these; only this selection and the termination rule differ.
/// That is why Ink, Line and Arrow are one interaction mode rather than three.
/// </summary>
public enum PathAnnotationKind
{
    /// <summary>Freehand stroke -> Ink.</summary>
    Ink,

    /// <summary>Two-point drag -> Line.</summary>
    Line,

    /// <summary>
    /// Two-point drag -> Line carrying /LE [None ClosedArrow].
    /// NOT a distinct /Subtype: an Arrow IS a Line with an arrowhead, so
    /// anything distinguishing the two must compare line endings.
    /// </summary>
    Arrow,
}
