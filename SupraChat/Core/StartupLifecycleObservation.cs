using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SupraChat.Core;

public enum StartupStage
{
    DesktopLifetimeEntering, XamlLoading, XamlLoaded, FrameworkInitialization,
    MainWindowCreating, MainWindowAssigned, FrameworkInitialized,
    StartupArgumentsEntering, StartupArgumentsReturned
}

/// <summary>
/// Optional categorical diagnostics only. Never changes exception handling,
/// walks arbitrary object properties, exports messages/stacks, or admits effects.
/// The last observed stage and throw-frame assembly are clues, not causal proof.
/// </summary>
public static class StartupLifecycleObservation
{
    public static void WriteStage(StartupStage stage)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("SUPRACHAT_STARTUP_LOG");
            if (string.IsNullOrWhiteSpace(path) || !Enum.IsDefined(stage)) return;
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} lifecycle-stage={stage}{Environment.NewLine}");
        }
        catch { /* Diagnostics cannot become an application dependency. */ }
    }

    public static string DescribeException(Exception exception)
    {
        try
        {
            var inner = exception.InnerException;
            var deepest = exception;
            var depth = 0;
            while (deepest.InnerException is { } next && depth < 8)
            {
                deepest = next;
                depth++;
            }
            return $"fatal-components outer={Component(exception)} inner={(inner is null ? "NONE" : Component(deepest))} inner-kind={(inner is null ? "NONE" : Kind(deepest))} truncated={(deepest.InnerException is null ? "NO" : "YES")}";
        }
        catch
        {
            return "fatal-components outer=UNAVAILABLE inner=UNAVAILABLE inner-kind=UNAVAILABLE truncated=NO";
        }
    }

    private static string Component(Exception ex)
    {
        var assembly = new StackTrace(ex, false).GetFrame(0)?.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
        return assembly switch
        {
            null => "NO_STACK",
            "SupraChat" => "APP",
            "Avalonia.Base" => "AVALONIA_BASE",
            "Avalonia.Controls" => "AVALONIA_CONTROLS",
            "Avalonia.Win32" => "AVALONIA_WIN32",
            "Avalonia.Controls.WebView" or "Avalonia.Controls.WebView.Windows" => "AVALONIA_WEBVIEW",
            "Microsoft.Web.WebView2.Core" => "WEBVIEW2",
            "System.Private.CoreLib" or "System.Runtime" or "System.Runtime.InteropServices" => "DOTNET_RUNTIME",
            _ => "OTHER_MANAGED"
        };
    }

    private static string Kind(Exception ex)
    {
        var type = ex.GetType();
        if (type == typeof(COMException)) return "COM";
        if (type == typeof(InvalidOperationException)) return "INVALID_OPERATION";
        if (type == typeof(ObjectDisposedException)) return "OBJECT_DISPOSED";
        if (type == typeof(NullReferenceException)) return "NULL_REFERENCE";
        if (type == typeof(ArgumentException) || type == typeof(ArgumentNullException) ||
            type == typeof(ArgumentOutOfRangeException)) return "ARGUMENT";
        if (type == typeof(IOException) || type == typeof(FileNotFoundException) ||
            type == typeof(DirectoryNotFoundException) || type == typeof(FileLoadException)) return "IO";
        if (type == typeof(TypeInitializationException)) return "TYPE_INITIALIZATION";
        if (type == typeof(UnauthorizedAccessException)) return "ACCESS_DENIED";
        if (type == typeof(DllNotFoundException) || type == typeof(BadImageFormatException)) return "NATIVE_LIBRARY";
        return "OTHER";
    }
}
