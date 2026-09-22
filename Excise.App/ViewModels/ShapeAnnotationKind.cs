namespace Excise.App.ViewModels;

/// <summary>
/// Which annotation a rectangle dragged in <c>InteractionMode.ShapeAnnotation</c>
/// becomes. Mirrors <see cref="PathAnnotationKind"/>'s role for the path
/// family: the capture, the DIP -> content conversion and the viewer event
/// are shared across all five; only this selection (and, for
/// <see cref="Stamp"/>, which stamp) differs.
/// </summary>
public enum ShapeAnnotationKind
{
    /// <summary>The drag rect -> Square.</summary>
    Square,

    /// <summary>The drag rect -> Circle.</summary>
    Circle,

    /// <summary>The drag rect -> a FreeText box, then a text prompt.</summary>
    FreeText,

    /// <summary>The drag rect -> a named rubber stamp (<see cref="MainWindowViewModel.StagedStampName"/>).</summary>
    Stamp,

    /// <summary>The drag rect -> an image stamp, then an image-file prompt.</summary>
    ImageStamp,
}
