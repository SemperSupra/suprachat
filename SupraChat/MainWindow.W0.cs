using Avalonia.Automation;
using Avalonia.Controls;

namespace SupraChat;

public partial class MainWindow
{
    private bool _w0WorkbenchInstalled;

    public void InstallW0WorkbenchTab()
    {
        if (_w0WorkbenchInstalled) return;
        var tab = new TabItem
        {
            Header = "Sessions",
            Content = new WorkbenchView()
        };
        AutomationProperties.SetAutomationId(tab, "Tab.Sessions");
        RootTabs.Items.Add(tab);
        _w0WorkbenchInstalled = true;
    }
}
