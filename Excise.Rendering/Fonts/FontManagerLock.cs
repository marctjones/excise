namespace Excise.Rendering.Fonts;

/// <summary>
/// The one process-wide lock for SkiaSharp font work.
///
/// <para>SkiaSharp's font subsystem is not safe under concurrent typeface
/// creation: <c>SKTypeface.FromData</c>, <c>SKTypeface.FromFamilyName</c> and
/// <c>SKFontManager.MatchCharacter</c> all reach into a process-wide native
/// font manager whose cache can corrupt or deadlock when two managed threads
/// call them at once. That crashed the test host under xUnit parallelism
/// (#363).</para>
///
/// <para>It lives here, rather than as a private field of the renderer, because
/// <see cref="AnnotationTypesetter"/> needs the SAME lock: it opens font data
/// through the shaper and queries the font manager for fallbacks (#1363). One
/// object, one lock, reachable from production code.</para>
/// </summary>
internal static class FontManagerLock
{
    internal static readonly object Instance = new();
}
