using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SupraChat.WebViewExtensionProbe;

/// <summary>
/// E1 only: an independent, disposable WinForms/WebView2 extension-support oracle.
/// This does NOT prove Avalonia ExplicitEnvironment or Violentmonkey compatibility.
/// No ChatGPT, user profile, credentials, Work task, proxy or provider API is used.
/// </summary>
internal static class Program
{
    private sealed class Receipt
    {
        public string Schema { get; init; } = "suprachat-browser-extension-e1/v1";
        public string Evidence { get; init; } = "SYNTHETIC_LOOPBACK_ONLY";
        public string Host { get; init; } = "WINFORMS_WEBVIEW2_NOT_AVALONIA";
        public string Phase { get; set; } = "startup";
        public string Classification { get; set; } = "UNKNOWN";
        public bool Passed { get; set; }
        public bool ExtensionEnabledOption { get; set; }
        public bool ExtensionInstalled { get; set; }
        public bool ContentScriptObserved { get; set; }
        public bool ProfileIsDisposable { get; init; } = true;
        public bool ProductionAdmitted { get; init; }
        public bool RawPayloadExported { get; init; }
        public string RuntimeVersion { get; set; } = "unknown";
        public string? FailureType { get; set; }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--receipt" || !Path.IsPathFullyQualified(args[1]))
            return 64;

        var receiptPath = Path.GetFullPath(args[1]);
        if (File.Exists(receiptPath)) return 64; // Never overwrite earlier evidence.
        var state = new Receipt();
        var root = Path.Combine(Path.GetTempPath(), "suprachat-extension-e1", Guid.NewGuid().ToString("N"));
        var overrideVariables = new[]
        {
            "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER",
            "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER"
        };
        if (overrideVariables.Any(variable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable))))
        {
            state.Phase = "environment-override-refused";
            state.Classification = "REFUSED";
            WriteReceipt(receiptPath, state);
            return 64;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            using var window = new Form
            {
                Text = "SupraChat disposable synthetic extension E1",
                Width = 640,
                Height = 480
            };
            using var view = new WebView2 { Dock = DockStyle.Fill };
            window.Controls.Add(view);
            window.Shown += async (_, _) =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    await RunSyntheticAsync(view, root, state, timeout.Token);
                    state.Classification = state.Passed ? "QUALIFIED_SYNTHETIC_E1" : "FAIL";
                }
                catch (OperationCanceledException)
                {
                    state.Phase = "timeout";
                    state.Classification = "ENVIRONMENT_OR_RUNTIME_UNKNOWN";
                }
                catch (Exception ex)
                {
                    // No raw exception text: it may contain user paths or machine state.
                    state.FailureType = ex.GetType().Name;
                    state.Classification = "FAIL_OR_ENVIRONMENT_UNKNOWN";
                }
                finally
                {
                    WriteReceipt(receiptPath, state);
                    window.Close();
                }
            };
            Application.Run(window);
        }
        catch (Exception ex)
        {
            state.Phase = "host-startup";
            state.FailureType = ex.GetType().Name;
            state.Classification = "ENVIRONMENT_OR_RUNTIME_UNKNOWN";
            WriteReceipt(receiptPath, state);
        }
        finally
        {
            // The CI runner is disposable; cleanup is best effort and is not an E1 acceptance claim.
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
        return state.Passed ? 0 : 1;
    }

    private static async Task RunSyntheticAsync(
        WebView2 view, string root, Receipt state, CancellationToken cancellationToken)
    {
        state.Phase = "generate-unprivileged-fixture";
        var extensionPath = Path.Combine(root, "fixture-extension");
        var profilePath = Path.Combine(root, "fixture-profile");
        Directory.CreateDirectory(extensionPath);
        Directory.CreateDirectory(profilePath);
        await File.WriteAllTextAsync(Path.Combine(extensionPath, "manifest.json"), """
            {
              "manifest_version": 3,
              "name": "SupraChat E1 Disposable Synthetic Probe",
              "version": "1.0.0",
              "content_scripts": [
                {
                  "matches": ["http://127.0.0.1/*"],
                  "js": ["probe.js"],
                  "run_at": "document_start"
                }
              ]
            }
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(extensionPath, "probe.js"), """
            'use strict';
            document.addEventListener('DOMContentLoaded', function () {
              document.documentElement.setAttribute('data-supra-extension-e1', 'synthetic-observed');
            }, { once: true });
            """, cancellationToken);

        state.Phase = "native-environment";
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = "--disable-background-networking"
        };
        var environment = await CoreWebView2Environment.CreateAsync(null, profilePath, options)
            .WaitAsync(cancellationToken);
        state.RuntimeVersion = environment.BrowserVersionString;
        state.ExtensionEnabledOption = true;
        await view.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);

        state.Phase = "extension-install";
        var profile = view.CoreWebView2.Profile;
        var added = await profile.AddBrowserExtensionAsync(extensionPath).WaitAsync(cancellationToken);
        var available = await profile.GetBrowserExtensionsAsync().WaitAsync(cancellationToken);
        state.ExtensionInstalled = available.Any(extension => extension.Id == added.Id);
        if (!state.ExtensionInstalled) throw new InvalidOperationException("Synthetic extension absent from inventory.");

        state.Phase = "local-content-script-observation";
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var fixture = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request.AsMemory(), cancellationToken);
            var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>synthetic-e1</title></head><body>fixture</body></html>");
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers.AsMemory(), cancellationToken);
            await stream.WriteAsync(body.AsMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }, cancellationToken);
        var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        view.CoreWebView2.NavigationCompleted += (_, args) => navigation.TrySetResult(args);
        view.CoreWebView2.Navigate($"http://127.0.0.1:{port}/");
        var result = await navigation.Task.WaitAsync(cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException("Synthetic navigation failed.");
        await fixture.WaitAsync(cancellationToken);

        var scriptResult = await view.CoreWebView2.ExecuteScriptAsync(
            "document.documentElement.getAttribute('data-supra-extension-e1')").WaitAsync(cancellationToken);
        state.ContentScriptObserved = JsonSerializer.Deserialize<string>(scriptResult) == "synthetic-observed";
        state.Passed = state.ExtensionEnabledOption && state.ExtensionInstalled && state.ContentScriptObserved;
        state.Phase = state.Passed ? "e1-synthetic-complete" : "e1-script-unobserved";
    }

    private static void WriteReceipt(string path, Receipt state)
    {
        var payload = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        if (payload.Contains("Bearer", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("Cookie", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Receipt content safety check failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(payload);
    }
}
