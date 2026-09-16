using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Excise.App.Services.Host;

namespace Excise.App.Tests.Utilities.Fakes;

/// <summary>
/// In-memory <see cref="IWindowHost"/> (#1500 step 1).
/// </summary>
/// <remarks>
/// <see cref="ShutdownRequests"/> is the point: Avalonia allows
/// <c>ApplicationLifetime</c> to be set exactly once, so a headless test could
/// never observe whether <c>ExitCommand</c> asked the app to quit — the
/// lifetime lookup simply returned null. Counting the request here makes the
/// unsaved-changes decision on Exit assertable without a desktop lifetime.
/// </remarks>
internal sealed class FakeWindowHost : IWindowHost
{
    /// <inheritdoc />
    public IStorageProvider? StorageProviderOverride { get; set; }

    /// <inheritdoc />
    public Func<Window?> MainWindowResolver { get; set; } = static () => null;

    /// <inheritdoc />
    public Window? MainWindow => MainWindowResolver();

    /// <inheritdoc />
    public IStorageProvider? StorageProvider => StorageProviderOverride;

    /// <summary>How many times <see cref="RequestShutdown"/> was called.</summary>
    internal int ShutdownRequests { get; private set; }

    /// <inheritdoc />
    public void RequestShutdown() => ShutdownRequests++;
}

/// <summary>
/// In-memory <see cref="ITextClipboard"/> (#1500 step 1): records the copied text
/// instead of touching the OS clipboard.
/// </summary>
internal sealed class FakeTextClipboard : ITextClipboard
{
    /// <summary>
    /// Whether the clipboard reports itself available. False models the
    /// headless "no clipboard" case the view model logs as "text recorded in
    /// history only".
    /// </summary>
    internal bool IsAvailable { get; set; } = true;

    /// <summary>The last text handed to <see cref="SetTextAsync"/>, or null.</summary>
    internal string? Text { get; private set; }

    /// <summary>How many times <see cref="SetTextAsync"/> was called.</summary>
    internal int SetCount { get; private set; }

    /// <inheritdoc />
    public Task<bool> SetTextAsync(string text)
    {
        SetCount++;
        if (!IsAvailable)
            return Task.FromResult(false);

        Text = text;
        return Task.FromResult(true);
    }
}
