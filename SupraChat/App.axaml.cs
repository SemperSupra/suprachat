using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SupraChat.Core;

namespace SupraChat;

public partial class App : Application
{
    public override void Initialize()
    {
        StartupLifecycleObservation.WriteStage(StartupStage.XamlLoading);
        AvaloniaXamlLoader.Load(this);
        StartupLifecycleObservation.WriteStage(StartupStage.XamlLoaded);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupLifecycleObservation.WriteStage(StartupStage.FrameworkInitialization);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupLifecycleObservation.WriteStage(StartupStage.MainWindowCreating);
            var window = new MainWindow();
            window.InstallW0WorkbenchTab();
            desktop.MainWindow = window;
            StartupLifecycleObservation.WriteStage(StartupStage.MainWindowAssigned);
            window.Opened += async (_, _) =>
            {
                StartupLifecycleObservation.WriteStage(StartupStage.StartupArgumentsEntering);
                await window.HandleStartupArgumentsAsync(desktop.Args ?? Array.Empty<string>());
                StartupLifecycleObservation.WriteStage(StartupStage.StartupArgumentsReturned);
            };
        }

        base.OnFrameworkInitializationCompleted();
        StartupLifecycleObservation.WriteStage(StartupStage.FrameworkInitialized);
    }
}
