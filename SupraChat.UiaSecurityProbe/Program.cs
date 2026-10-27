using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SupraChat;

namespace SupraChat.UiaSecurityProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // This is an isolated local Windows test process. It has no WebView,
        // credentials, browser profiles, account access, capture or networking.
        if (!OperatingSystem.IsWindows() || args.Length != 0) return 64;
        var synthetic = Environment.GetEnvironmentVariable("SUPRACHAT_SYNTHETIC_UIA_SECRET");
        if (synthetic is null || synthetic.Length < 24 || synthetic.Length > 80 ||
            !synthetic.StartsWith("SyntheticOnly", StringComparison.Ordinal))
            return 64;

        try
        {
            AppBuilder.Configure<ProbeApplication>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch
        {
            // Never print the generated test value or raw exception messages.
            return 1;
        }
    }
}

internal sealed class ProbeApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var secret = Environment.GetEnvironmentVariable("SUPRACHAT_SYNTHETIC_UIA_SECRET")
                ?? throw new InvalidOperationException("Synthetic-only fixture not admitted");

            var passphrase = new SecurePassphraseTextBox { Width = 310 };
            AutomationProperties.SetAutomationId(passphrase, "Synthetic.Probe.Passphrase");
            AutomationProperties.SetName(passphrase, "Synthetic passphrase");

            var status = new TextBlock { Text = "EMPTY" };
            AutomationProperties.SetAutomationId(status, "Synthetic.Probe.State");

            var fill = new Button { Content = "Fill test field" };
            AutomationProperties.SetAutomationId(fill, "Synthetic.Probe.Fill");
            fill.Click += (_, _) =>
            {
                // The only place the synthetic value enters the UI, after a
                // real independent UIA Invoke. No plaintext receipt or logger.
                passphrase.Text = secret;
                status.Text = passphrase.Text == secret ? "FILLED" : "REFUSED";
                fill.IsEnabled = false;
            };

            var window = new Window
            {
                Title = "SupraChat local synthetic UIA secret probe",
                Width = 480,
                Height = 270,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 12,
                    Children = { new TextBlock { Text = "Disposable secret-provider qualification" }, passphrase, fill, status }
                }
            };
            AutomationProperties.SetAutomationId(window, "SupraChat.SyntheticSecurityProbe");
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
