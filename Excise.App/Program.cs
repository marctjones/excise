using Avalonia;
using ReactiveUI;
using System;

namespace Excise.App;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

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
