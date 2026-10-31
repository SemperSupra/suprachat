using Avalonia.Automation;
using Avalonia.Controls;
using SupraChat.Core;

namespace SupraChat;

public partial class MainWindow
{
    private bool _w0WorkbenchInstalled;
    // Transitional per-window owner; deliberately not cross-process or release qualified.
    private readonly IWorkbenchSessionAuthority _w0SessionAuthority = new ExperimentalLocalWorkbenchSessionAuthority();

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
