using System.Diagnostics;
using FlaUI.UIA3;
using UIA = Interop.UIAutomationClient;

namespace SupraChat.UiaComObserver;

// Strict external Windows UIA3 client. Does not inject, type, scrape browser
// content, authenticate or emit raw UIA values. Runs in a separate process
// from the disposable synthetic control fixture.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length != 2 ||
                !int.TryParse(args[0], out int pid) || pid <= 0 ||
                !long.TryParse(args[1], out long nativeHwnd) || nativeHwnd <= 0)
                return Fail("ARGUMENTS_REFUSED", 64);
            var synthetic = Environment.GetEnvironmentVariable("SUPRACHAT_SYNTHETIC_UIA_SECRET");
            if (synthetic is null || synthetic.Length is < 24 or > 80 ||
                !synthetic.StartsWith("SyntheticOnly", StringComparison.Ordinal))
                return Fail("SYNTHETIC_SCOPE_REFUSED", 64);
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            if (process.HasExited || process.MainWindowHandle.ToInt64() != nativeHwnd)
                return Fail("OWNER_HWND_REFUSED", 65);
            using var automation = new UIA3Automation();
            // FlaUI's tested COM binding uses an IntPtr for ElementFromHandle.
            // The HWND and resulting UIA PID/AutomationId must both agree.
            var root = automation.NativeAutomation.ElementFromHandle(new IntPtr(nativeHwnd));
            if (root is null || root.CurrentProcessId != pid ||
                root.CurrentAutomationId != "SupraChat.SyntheticSecurityProbe")
                return Fail("NATIVE_ROOT_IDENTITY_REFUSED", 65);
            var condition = automation.NativeAutomation.CreatePropertyCondition(
                30011, "Synthetic.Probe.Passphrase");
            var field = root.FindFirst(UIA.TreeScope.TreeScope_Descendants, condition);
            if (field is null || field.CurrentProcessId != pid ||
                field.CurrentAutomationId != "Synthetic.Probe.Passphrase")
                return Fail("NATIVE_CONTROL_IDENTITY_REFUSED", 65);

            var value = field.GetCurrentPropertyValue(30090);
            if (value is not bool available)
                return Fail("LEGACY_AVAILABILITY_UNQUALIFIED", 66);
            if (available)
            {
                foreach (var property in new[] { 30092, 30093, 30094, 30097, 30100 })
                {
                    var candidate = field.GetCurrentPropertyValue(property);
                    if (candidate is not null &&
                        candidate is not string && candidate is not int)
                        return Fail("LEGACY_PROPERTY_TYPE_UNQUALIFIED", 66);
                    if (candidate is string s && s.Contains(synthetic, StringComparison.Ordinal))
                        return Fail("LEGACY_SYNTHETIC_VALUE_EXPOSED", 67);
                }
            }
            Console.WriteLine(available ? "LEGACY_PRESENT_NONDISCLOSURE_PASS" :
                "LEGACY_PATTERN_ABSENT_ON_NATIVE_UIA_PEER");
            return 0;
        }
        catch
        {
            // Exception text can include provider data; never export it.
            return Fail("UIA3_OBSERVER_FAILED", 68);
        }
    }

    private static int Fail(string reason, int code)
    {
        Console.WriteLine(reason);
        return code;
    }
}
