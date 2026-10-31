using Avalonia.Automation;
using Avalonia.Controls;
using SupraChat.Core;

namespace SupraChat;

public partial class MainWindow
{
    private bool _w0WorkbenchInstalled;
    // Explicit disposable test opt-in. Never silently instantiate a second
    // writable ledger when a shared-owner endpoint was requested.
    private readonly IWorkbenchSessionAuthority _w0SessionAuthority = CreateW0Authority();

    private static IWorkbenchSessionAuthority CreateW0Authority()
    {
        var portText = Environment.GetEnvironmentVariable("SUPRACHAT_W6_GUI_PORT");
        if (portText is null)
            return new ExperimentalLocalWorkbenchSessionAuthority();
        if (Environment.GetEnvironmentVariable("SUPRACHAT_W6_DISPOSABLE_ONLY") != "yes" ||
            !int.TryParse(portText, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("W6_SYNTHETIC_GUI_PORT_ADMISSION_REFUSED");
        return new W6SyntheticWorkbenchSessionAuthority(port);
    }

    public void InstallW0WorkbenchTab()
    {
        if (_w0WorkbenchInstalled) return;
        var tab = new TabItem
        {
            Header = "Sessions",
            Content = new WorkbenchView(_w0SessionAuthority)
        };
        AutomationProperties.SetAutomationId(tab, "Tab.Sessions");
        RootTabs.Items.Add(tab);
        _w0WorkbenchInstalled = true;
    }
}
