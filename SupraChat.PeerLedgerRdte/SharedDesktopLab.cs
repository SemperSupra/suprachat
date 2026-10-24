using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SupraChat.PeerLedgerRdte;

[SupportedOSPlatform("windows")]
internal static class SharedDesktopLab
{
    internal static SharedDesktopHost? Host;
    internal static object? Details;
    internal static string Phase = "STARTUP";
    internal static int Result = 1;

    internal static int Run(string receiptPath, bool requireConsent = false)
    {
        var path = Path.GetFullPath(receiptPath);
        if (File.Exists(path)) return 64;
        try
        {
            Host = new SharedDesktopHost(requireConsent);
            AppBuilder.Configure<SharedDesktopApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch { Result = 1; Phase = "INCOMPLETE"; }
        finally
        {
            try { Host?.Dispose(); } catch { Result = 1; Phase = "CLEANUP_INCOMPLETE"; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new
            {
                schema = requireConsent ? "suprachat-metadata-consent-rdte/v1" : "suprachat-shared-desktop-rdte/v1", passed = Result == 0, phase = Phase, details = Details,
                scope = "DISPOSABLE_SHARED_METADATA_WORKBENCH", provider_actions = false, first_party_capture = false,
                human_identity_qualified = false, production_shared_host_qualified = false,
                production_issuer_custody_qualified = false, payload_exported = false
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        return Result;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SharedDesktopApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = SharedDesktopLab.Host!;
            var window = new Window { Title = "SupraChat shared-session research", Width = model.ConsentEnabled ? 1180 : 850, Height = 800 };
            AutomationProperties.SetAutomationId(window, "Shared.Window");
            var sessions = Named(new ComboBox { ItemsSource = new[] { "Session A", "Session B" }, SelectedIndex = 0 }, "Shared.Sessions", "Session");
            var consentPane = model.ConsentEnabled ? new MetadataConsentPane(model, () => sessions.SelectedIndex) : null;
            var title = Named(new TextBox { MaxLength = 80 }, "Shared.Title", "Session title");
            var snapshot = Named(new TextBox { IsReadOnly = true, Height = 120, AcceptsReturn = true }, "Shared.Snapshot", "Shared session snapshot");
            var status = Named(new TextBox { IsReadOnly = true, Text = "Ready" }, "Shared.Status", "Workbench status");
            var save = Named(new Button { Content = "Save title" }, "Shared.Save", "Save session title");
            var refresh = Named(new Button { Content = "Refresh" }, "Shared.Refresh", "Refresh shared session");
            var clients = Named(new Button { Content = "Run automation and agent fixtures" }, "Shared.Clients", "Run enrolled automation and agent clients");
            var revoke = Named(new Button { Content = "Test agent self-revocation" }, "Shared.Revoke", "Test agent self-revocation");
            var verify = Named(new Button { Content = "Verify recovery and finish" }, "Shared.Verify", "Verify shared session recovery");
            var buttons = new[] { save, refresh, clients, revoke, verify };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "Disposable shared-session workbench", FontSize = 24 });
            panel.Children.Add(new TextBlock { Text = "Synthetic metadata only. No ChatGPT sign-in, provider actions or production identity." });
            foreach (var control in new Control[] { sessions, title, save, refresh, clients, revoke, verify, status, snapshot }) panel.Children.Add(control);
            if (consentPane is not null)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
                grid.Children.Add(panel); Grid.SetColumn(consentPane, 1); grid.Children.Add(consentPane); window.Content = grid;
            }
            else window.Content = panel;
            long displayedVersion = 0;
            var busy = false;
            void Render(bool replaceTitle = true)
            {
                var view = model.Read(sessions.SelectedIndex);
                displayedVersion = view.Version;
                if (replaceTitle) title.Text = view.Title;
                snapshot.Text = JsonSerializer.Serialize(view, SupraChat.Core.TraceNormalizer.JsonOptions);
            }
            async Task Action(Func<Task> action)
            {
                if (busy) return;
                busy = true; sessions.IsEnabled = false; title.IsEnabled = false;
                if (consentPane is not null) consentPane.IsEnabled = false;
                foreach (var button in buttons) button.IsEnabled = false;
                try { await action(); }
                catch { status.Text = "Fixture incomplete"; SharedDesktopLab.Phase = "ACTION_INCOMPLETE"; }
                finally
                {
                    busy = false; sessions.IsEnabled = true; title.IsEnabled = true;
                    if (consentPane is not null) consentPane.IsEnabled = true;
                    foreach (var button in buttons) button.IsEnabled = true;
                    clients.IsEnabled = !model.ClientsComplete;
                    revoke.IsEnabled = model.ClientsComplete && !model.AgentRevoked;
                    verify.IsEnabled = model.AgentRevoked;
                }
            }
            sessions.SelectionChanged += (_, _) => { if (!busy) { consentPane?.SelectionChanged(); Render(); } };
            refresh.Click += async (_, _) => await Action(() => { Render(); status.Text = "Refreshed"; return Task.CompletedTask; });
            save.Click += async (_, _) => await Action(() =>
            {
                var result = model.Rename(sessions.SelectedIndex, displayedVersion, title.Text ?? "");
                status.Text = result == "APPLIED" ? "Title saved" : result == "VERSION_CONFLICT" ? "Session changed; refresh and try again" : "Change refused";
                Render(); return Task.CompletedTask;
            });
            clients.Click += async (_, _) => await Action(async () => { await model.RunClientsAsync(); Render(); status.Text = "Both clients completed"; });
            revoke.Click += async (_, _) => await Action(async () => { await model.RevokeAgentAsync(); Render(); status.Text = "Agent revoked; later read refused"; });
            verify.Click += async (_, _) => await Action(() =>
            {
                SharedDesktopLab.Details = model.VerifyAndRecover(); SharedDesktopLab.Result = 0; SharedDesktopLab.Phase = "VERIFIED";
                desktop.Shutdown(0); return Task.CompletedTask;
            });
            revoke.IsEnabled = false; verify.IsEnabled = false;
            desktop.MainWindow = window;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(75) };
            timer.Tick += (_, _) => { timer.Stop(); SharedDesktopLab.Phase = "DEADLINE"; desktop.Shutdown(1); };
            window.Opened += (_, _) => { Render(); SharedDesktopLab.Phase = "READY"; timer.Start(); };
            window.Closed += (_, _) => timer.Stop();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static T Named<T>(T control, string id, string name) where T : Control
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
}
