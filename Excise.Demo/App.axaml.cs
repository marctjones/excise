using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Excise.Demo;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Check command line args for demo mode
            var args = desktop.Args ?? System.Array.Empty<string>();
            if (args.Length > 0 && args[0] == "--basic")
            {
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                // Default to spec coverage demo
                desktop.MainWindow = new SpecCoverageWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}