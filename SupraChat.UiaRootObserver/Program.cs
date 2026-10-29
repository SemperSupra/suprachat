using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.UIA3;
using UIA = Interop.UIAutomationClient;

namespace SupraChat.UiaRootObserver;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [MTAThread]
    public static int Main(string[] args)
    {
        var result = new Dictionary<string, object?> {
            ["schema"]="suprachat-fresh-uia-root-witness/v2", ["process_alive"]=false,
            ["os_window_valid"]=null, ["os_pid_matches"]=null, ["uia_pid_matches"]=null,
            ["wm_null_responded"]=null, ["wm_null_error"]=null, ["wm_null_elapsed_ms"]=null,
            ["uia_root_id_matches"]=null, ["probe_completed"]=false,
            ["failure_stage"]="ARGUMENTS", ["failure_hresult"]=null, ["known_controls"]=null,
            ["scope"]="FIXED_NATIVE_IDS_ONLY", ["raw_values_exported"]=false };
        UIA3Automation? automation = null;
        var exit = 2;
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length is not (2 or 3) ||
                (args.Length == 3 && args[2] != "--synthetic-tab-probe") ||
                !int.TryParse(args[0], out var pid) || !long.TryParse(args[1], out var hwnd) || pid <= 0 || hwnd <= 0)
                throw new ArgumentException();
            var synthetic = args.Length == 3;
            var rootId = synthetic ? "SupraChat.SyntheticTabProbe" : "SupraChat.MainWindow";
            result["failure_stage"]="PROCESS";
            using var process = Process.GetProcessById(pid);
            process.Refresh(); result["process_alive"]=!process.HasExited;
            if (process.HasExited) throw new InvalidOperationException();
            var handle = new IntPtr(hwnd);
            result["failure_stage"]="OS_WINDOW";
            result["os_window_valid"]=IsWindow(handle);
            GetWindowThreadProcessId(handle, out var ownerPid);
            result["os_pid_matches"]=ownerPid == pid;
            if (!IsWindow(handle) || ownerPid != pid) throw new InvalidOperationException();
            // WM_NULL has no product action. A separate-process bounded send
            // distinguishes window responsiveness from UIA availability; neither
            // outcome is a qualification gate or a substitute for semantic peers.
            result["failure_stage"]="OS_RESPONSIVENESS";
            var clock = Stopwatch.StartNew();
            Marshal.SetLastPInvokeError(0);
            var responded = SendMessageTimeout(handle, 0, UIntPtr.Zero, IntPtr.Zero,
                0x22 /* ABORTIFHUNG | ERRORONEXIT */, 1000, out _) != IntPtr.Zero;
            var messageError = Marshal.GetLastWin32Error();
            result["wm_null_responded"]=responded;
            result["wm_null_error"]=responded ? null : messageError;
            result["wm_null_elapsed_ms"]=clock.ElapsedMilliseconds;
            result["failure_stage"]="FROM_HANDLE";
            automation = new UIA3Automation();
            var root = automation.NativeAutomation.ElementFromHandle(handle);
            result["failure_stage"]="UIA_PID";
            var rootPid = root.CurrentProcessId;
            result["uia_pid_matches"]=rootPid == pid;
            if (rootPid != pid) throw new InvalidOperationException();
            result["failure_stage"]="UIA_ROOT_ID";
            var actualRootId = root.CurrentAutomationId;
            result["uia_root_id_matches"]=actualRootId == rootId;
            if (actualRootId != rootId) throw new InvalidOperationException();
            var ids = synthetic ? new[] {"Synthetic.TabProbe.Toolbar","Synthetic.TabProbe.SecondTab","Synthetic.TabProbe.SecondButton"}
                : new[] {"RootTabs","Tab.ChatGPT","Tab.AgentLab","Tab.AutomationAgents","Nav.Home","Auth.SignIn","AgentLab.Prompt","Auth.Status"};
            var known = ids.ToDictionary(id => id, _ => (object?)null);
            result["known_controls"]=known;
            foreach (var id in ids)
            {
                result["failure_stage"]="CONTROL_QUERY";
                var control = root.FindFirst(UIA.TreeScope.TreeScope_Descendants,
                    automation.NativeAutomation.CreatePropertyCondition(30011, id));
                // Query only fixed declarative IDs. Never materialize arbitrary
                // IDs or read names, values or document contents.
                known[id]=control is not null && control.CurrentProcessId == pid;
            }
            automation.Dispose(); automation = null;
            result["failure_stage"]="NONE";result["probe_completed"]=true;exit=0;
        }
        catch (Exception exception)
        {
            result["failure_hresult"]=exception.GetBaseException().HResult;
        }
        finally
        {
            try { automation?.Dispose(); } catch { }
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        return exit;
    }
}
