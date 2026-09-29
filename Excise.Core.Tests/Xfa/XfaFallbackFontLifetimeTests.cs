using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Excise.Core.Xfa;
using Xunit;

namespace Excise.Core.Tests.Xfa;

/// <summary>
/// #1921: the XFA fallback font (a system font read for CJK/Cyrillic/etc. text base-14 cannot
/// draw, e.g. Arial Unicode at ~23 MB) used to sit behind a <c>Lazy&lt;byte[]&gt;</c> and was
/// never released for the life of the process, regardless of whether any open document still
/// needed it. It is now cached behind a <see cref="WeakReference{T}"/>: reused across documents
/// opened back-to-back as long as something keeps the GC from reclaiming it, but collectible
/// once nothing does. These tests reach the private cache via reflection rather than adding
/// test-only surface to <see cref="XfaFallbackFont"/> (the same approach this codebase already
/// uses for other private statics, e.g. CffStandardStringsTests).
/// </summary>
public class XfaFallbackFontLifetimeTests
{
    private static readonly MethodInfo GetOrLoadMethod = typeof(XfaFallbackFont).GetMethod(
        "GetOrLoad", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("XfaFallbackFont.GetOrLoad must still exist for this test (#1921)");

    private static bool HasSystemFont => XfaFallbackFont.ForDocument(1) != null;

    private static object? GetOrLoad() => GetOrLoadMethod.Invoke(null, null);

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public void SameCachedInstance_IsReused_AcrossBackToBackRequests()
    {
        Assert.SkipUnless(HasSystemFont, "no wide-coverage Unicode font from XfaFallbackFont's list is installed");

        var first = GetOrLoad();
        var second = GetOrLoad();

        first.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeTrue(
            "nothing collected the cache between these two requests, so the second call must reuse " +
            "the same bytes/metrics instead of re-reading the system font from disk (#1921)");
    }

    [Fact]
    public void CachedFont_IsCollectible_OnceNothingElseReferencesIt()
    {
        Assert.SkipUnless(HasSystemFont, "no wide-coverage Unicode font from XfaFallbackFont's list is installed");

        var weak = CaptureWeakReferenceToCurrentCache();

        Collect();
        Collect(); // a second full GC proves it is not merely unswept, mirroring the idiom in
                   // Excise.App.Tests/UI/WindowCloseReleaseTests.cs (Collect()).

        weak.TryGetTarget(out _).Should().BeFalse(
            "the fallback font cache must not be held for the life of the process once nothing else " +
            "references it (#1921) — it is backed by a WeakReference<T>, not a static strong field");
    }

    /// <summary>
    /// Isolated in its own non-inlined frame so no local in the caller (or the JIT's view of
    /// this method's own locals, once it returns) keeps the cached instance reachable — the
    /// same precaution <c>OpenAndCloseSecondWindowAsync</c> documents in WindowCloseReleaseTests.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CaptureWeakReferenceToCurrentCache()
    {
        var cached = GetOrLoad();
        cached.Should().NotBeNull();
        return new WeakReference<object>(cached!);
    }
}
