using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SupraChat.Core;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal static class SharedCancellationLab
{
    internal static SharedCancellationHost? Host;
    internal static object? Details;
    internal static string Phase = "STARTUP";
    internal static int Result = 1;
    internal static int Run(string receiptPath)
    {
        var path = Path.GetFullPath(receiptPath); if (File.Exists(path)) return 64;
        try
        {
            Host = new();
            AppBuilder.Configure<SharedCancellationApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch { Result = 1; Phase = "INCOMPLETE"; }
        finally
        {
            try { Host?.Dispose(); } catch { Result = 1; Phase = "CLEANUP_INCOMPLETE"; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new { schema = "suprachat-shared-synthetic-cancellation/v1", passed = Result == 0,
                phase = Phase, details = Details, scope = "DISPOSABLE_SHARED_SYNTHETIC_CANCELLATION",
                real_provider_actions = false, first_party_capture = false, human_identity_qualified = false,
                production_host_qualified = false, production_issuer_custody_qualified = false,
                grants_exported = false, signing_key_exported = false, checkpoint_exported = false,
                peer_identity_values_exported = false, provider_stop_verified = false }, new JsonSerializerOptions { WriteIndented = true });
        }
        return Result;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SharedCancellationApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = SharedCancellationLab.Host!;
            var window = new Window { Title = "SupraChat synthetic shared cancellation", Width = 850, Height = 600 };
            AutomationProperties.SetAutomationId(window, "CancellationLab.Window");
            var status = Named(new TextBox { IsReadOnly = true, Text = "Ready" }, "CancellationLab.Status", "Cancellation workbench status");
            var snapshot = Named(new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 210 }, "CancellationLab.Snapshot", "Shared cancellation outcome projection");
            var preview = Named(new Button { Content = "Preview synthetic cancellation" }, "CancellationLab.Preview", "Preview synthetic stream cancellation");
            var confirm = Named(new Button { Content = "Confirm local cancellation", IsEnabled = false }, "CancellationLab.Confirm", "Confirm reviewed local synthetic cancellation");
            var clients = Named(new Button { Content = "Run enrolled automation and agent clients", IsEnabled = false }, "CancellationLab.Clients", "Run scoped cancellation clients");
            var verify = Named(new Button { Content = "Verify durable recovery and finish", IsEnabled = false }, "CancellationLab.Verify", "Verify cancellation recovery without redispatch");
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "Shared synthetic cancellation workbench", FontSize = 24 });
            panel.Children.Add(new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "Disposable synthetic streams only. Preview does not cancel. Confirmation signals the selected local fixture. No provider termination or human identity is claimed." });
            foreach (var control in new Control[] { preview, confirm, clients, verify, status, snapshot }) panel.Children.Add(control);
            window.Content = panel;
            var busy = false;
            async Task Action(Func<Task> action)
            {
                if (busy) return; busy = true;
                preview.IsEnabled = confirm.IsEnabled = clients.IsEnabled = verify.IsEnabled = false;
                try { await action(); }
                catch { status.Text = "Fixture incomplete"; SharedCancellationLab.Phase = "ACTION_INCOMPLETE"; }
                finally { busy = false; }
            }
            void Render() => snapshot.Text = JsonSerializer.Serialize(model.ReadProjection(), TraceNormalizer.JsonOptions);
            preview.Click += async (_, _) => await Action(() =>
            {
                var result = model.PreviewGui();
                if (result.Status != "CONFIRMATION_REQUIRED" || model.GuiApplied) throw new InvalidOperationException();
                status.Text = "Review ready"; confirm.IsEnabled = true; return Task.CompletedTask;
            });
            confirm.Click += async (_, _) => await Action(() =>
            {
                var result = model.ConfirmGui(); if (result.Status != "VERIFIED_LOCAL_SIGNAL" || !model.GuiApplied) throw new InvalidOperationException();
                Render(); status.Text = "Local synthetic cancellation verified"; clients.IsEnabled = true; return Task.CompletedTask;
            });
            clients.Click += async (_, _) => await Action(async () =>
            {
                await model.RunClientsAsync(); Render(); status.Text = "Scoped clients completed"; verify.IsEnabled = true;
            });
            verify.Click += async (_, _) => await Action(() =>
            {
                SharedCancellationLab.Details = model.VerifyAndRecover(); SharedCancellationLab.Result = 0;
                SharedCancellationLab.Phase = "VERIFIED"; desktop.Shutdown(0); return Task.CompletedTask;
            });
            desktop.MainWindow = window;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(75) };
            timer.Tick += (_, _) => { timer.Stop(); SharedCancellationLab.Phase = "DEADLINE"; desktop.Shutdown(1); };
            window.Opened += (_, _) => { Render(); SharedCancellationLab.Phase = "READY"; timer.Start(); };
            window.Closed += (_, _) => timer.Stop();
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static T Named<T>(T control, string id, string name) where T : Control
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
}
