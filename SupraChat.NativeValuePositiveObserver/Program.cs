using System.Diagnostics;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;
using FlaUI.UIA3.Patterns;

internal static class Program
{
    [MTAThread]
    public static int Main(string[] args)
    {
        UIA3Automation? automation = null;
        PropertyChangedEventHandlerBase? listener = null;
        var heard = 0; var late = 0; var unknown = 0; var removed = 0;
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length != 2 ||
                !int.TryParse(args[0], out var pid) || pid <= 0 ||
                !long.TryParse(args[1], out var hwnd) || hwnd <= 0)
                throw new InvalidOperationException();
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            if (process.HasExited || process.MainWindowHandle.ToInt64() != hwnd)
                throw new InvalidOperationException();
            automation = new UIA3Automation();
            var root = automation.FromHandle(new IntPtr(hwnd));
            if (root.Properties.ProcessId.Value != pid ||
                root.AutomationId != "SupraChat.NativeValuePositiveFixture")
                throw new InvalidOperationException();
            var elements = root.FindAllDescendants(x => x.ByAutomationId("Synthetic.NativeValue.Text"));
            if (elements.Length != 1 || elements[0].Properties.ProcessId.Value != pid)
                throw new InvalidOperationException();
            listener = root.RegisterPropertyChangedEvent(TreeScope.Subtree, (sender, property, value) =>
            {
                try
                {
                    if (sender.Properties.ProcessId.Value != pid ||
                        sender.AutomationId != "Synthetic.NativeValue.Text" ||
                        property.Id != ValuePattern.ValueProperty.Id)
                        { Interlocked.Exchange(ref unknown, 1); return; }
                    if (Volatile.Read(ref removed) != 0) Interlocked.Exchange(ref late, 1);
                    if (value is string s && s == "ARMED") Interlocked.Exchange(ref heard, 1);
                }
                catch { Interlocked.Exchange(ref unknown, 1); }
            }, ValuePattern.ValueProperty);
            Console.WriteLine("READY");
            Read("MUTATED");
            var watch = Stopwatch.StartNew();
            while (Volatile.Read(ref heard) == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(20);
            Console.WriteLine(Volatile.Read(ref heard) == 1 ? "VALUE_OBSERVED_1" : "VALUE_OBSERVED_0");
            listener.Dispose(); listener = null;
            Interlocked.Exchange(ref removed, 1);
            Console.WriteLine("REMOVED");
            Read("POST_REMOVAL");
            Thread.Sleep(300);
            var passed = Volatile.Read(ref heard) == 1 && Volatile.Read(ref late) == 0 &&
                Volatile.Read(ref unknown) == 0;
            Console.WriteLine(passed ? "POSITIVE_BRIDGE_PASS" : "POSITIVE_BRIDGE_NOT_VERIFIED");
            return passed ? 0 : 2;
        }
        catch { Console.WriteLine("POSITIVE_BRIDGE_NOT_VERIFIED"); return 2; }
        finally
        {
            try { listener?.Dispose(); } catch {}
            try { automation?.Dispose(); } catch {}
        }
    }
    static void Read(string target)
    {
        if (Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() != target)
            throw new InvalidOperationException();
    }
}
