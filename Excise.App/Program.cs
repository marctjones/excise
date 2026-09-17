using Avalonia;
using ReactiveUI;
using System;

namespace Excise.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // #1553: on Windows and Linux, "Open With" on a running excise hands
        // the documents to that process (new windows there) instead of
        // starting a second one. macOS routes documents through Launch
        // Services activation instead.
        if (Excise.App.Workspace.SingleInstanceChannel.TryForwardStartupDocuments(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Vendored scheduler wiring (Threading/AvaloniaDispatcherScheduler)
            // replaces ReactiveUI.Avalonia's UseReactiveUI(b => b.WithAvalonia())
            // — the only piece of that package the app used (#593). Must run
            // in AfterSetup, before any ReactiveCommand is created.
            .AfterSetup(_ =>
                RxSchedulers.MainThreadScheduler = Excise.App.Threading.AvaloniaDispatcherScheduler.Instance)
            .LogToTrace();
}
