using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bdeyes.Services;
using Bdeyes.ViewModels;
using Bdeyes.Views;

namespace Bdeyes;

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
            var initialWorkspace = ParseWorkspaceArgument(desktop.Args);
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(
                    new BdClient(),
                    new UserSettingsStore(),
                    initialWorkspace),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ParseWorkspaceArgument(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--workspace=", StringComparison.OrdinalIgnoreCase))
            {
                return argument[12..];
            }

            if (string.Equals(argument, "--workspace", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}