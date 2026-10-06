using System;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace SupraChat;

/// <summary>
/// Password-entry TextBox whose automation peer deliberately withholds the Value pattern.
/// Avalonia 12.1.0 masks password text visually, but its stock Windows automation bridge
/// exposes TextBox.Text through IValueProvider and does not surface UIA IsPassword.
/// Until the framework provides a native password automation contract, the safest bounded
/// behavior is to retain semantic/focus accessibility while refusing programmatic value reads.
/// </summary>
public sealed class SecurePassphraseTextBox : TextBox
{
    public SecurePassphraseTextBox()
    {
        PasswordChar = '●';
        RevealPassword = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new SecurePassphraseTextBoxAutomationPeer(this);
}

/// <summary>
/// Preserves TextBox automation semantics except the secret-bearing Value provider.
/// </summary>
public sealed class SecurePassphraseTextBoxAutomationPeer : TextBoxAutomationPeer
{
    public SecurePassphraseTextBoxAutomationPeer(SecurePassphraseTextBox owner)
        : base(owner)
    {
    }

    protected override object? GetProviderCore(Type providerType)
    {
        if (providerType == typeof(IValueProvider))
            return null;

        return base.GetProviderCore(providerType);
    }

    protected override void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Do not emit secret-bearing ValuePattern property-change notifications.
        if (e.Property == TextBox.TextProperty)
            return;

        base.OwnerPropertyChanged(sender, e);
    }
}
