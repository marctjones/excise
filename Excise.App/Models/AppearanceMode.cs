namespace Excise.App.Models;

/// <summary>
/// Light or dark chrome (#2002, Preferences ▸ Appearance). Document pages are
/// never recoloured: the page stays as the PDF draws it in every mode.
/// </summary>
internal enum AppearanceMode
{
    /// <summary>Follow the operating system's light/dark setting, and its changes.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}
