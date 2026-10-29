using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 0) return 64;
        var value = new TextBox { Text = "EMPTY", IsReadOnly = true };
        AutomationProperties.SetAutomationId(value, "Synthetic.NativeValue.Text");
        var button = new Button { Content = "Mutate public control" };
        AutomationProperties.SetAutomationId(button, "Synthetic.NativeValue.Mutate");
        button.Click += (_, _) =>
        {
            var before = value.Text;
            value.Text = before == "EMPTY" ? "ARMED" : "POST_REMOVAL";
            // Test-only bridge: explicitly raise the native UIA property change.
            // A positive result validates observer wiring, not Avalonia forwarding.
            var peer = UIElementAutomationPeer.FromElement(value) ??
                UIElementAutomationPeer.CreatePeerForElement(value);
            peer?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, value.Text);
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(value);
        panel.Children.Add(button);
        var window = new Window { Title = "Disposable native value event fixture",
            Width = 420, Height = 180, Content = panel };
        AutomationProperties.SetAutomationId(window, "SupraChat.NativeValuePositiveFixture");
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        application.Run(window);
        return 0;
    }
}
