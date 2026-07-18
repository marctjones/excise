using Avalonia;
using ReactiveUI.Avalonia;
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
            .UseReactiveUI(b => b.WithAvalonia())
            .LogToTrace();
}
