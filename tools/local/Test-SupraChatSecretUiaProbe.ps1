param(
    [Parameter(Mandatory)][string]$Executable
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
$exe = [IO.Path]::GetFullPath($Executable)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'QUALIFIED_EXECUTABLE_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# A fresh value exists only in this disposable test process and this script's
# memory. Never print it, capture desktop screenshots or upload a UIA tree.
$synthetic = 'SyntheticOnly' + [Guid]::NewGuid().ToString('N')
$env:SUPRACHAT_SYNTHETIC_UIA_SECRET = $synthetic
$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    Remove-Item Env:SUPRACHAT_SYNTHETIC_UIA_SECRET -ErrorAction SilentlyContinue

    function Get-Window {
        $process.Refresh()
        if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        if ($root.Current.ProcessId -ne $process.Id -or
            $root.Current.AutomationId -cne 'SupraChat.SyntheticSecurityProbe') {
            throw 'SYNTHETIC_WINDOW_BINDING_REFUSED'
        }
        return $root
    }

    function Require-Element([string]$Id) {
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            $root = Get-Window
            if ($root) {
                $condition = [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
                $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($item) { return $item }
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'SYNTHETIC_SEMANTIC_ELEMENT_MISSING'
    }

    function Check-Not-Synthetic([string]$Candidate, [string]$FailureCode) {
        if ($null -ne $Candidate -and
            $Candidate.Contains($synthetic, [StringComparison]::Ordinal)) {
            throw $FailureCode
        }
    }

    function Check-Exposure {
        $secretControl = Require-Element 'Synthetic.Probe.Passphrase'
        $exposed = $null
        if ($secretControl.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern, [ref]$exposed)) {
            throw 'SYNTHETIC_UIA_VALUE_PATTERN_EXPOSED'
        }
        $exposed = $null
        if ($secretControl.TryGetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern, [ref]$exposed)) {
            throw 'SYNTHETIC_UIA_TEXT_PATTERN_EXPOSED'
        }

        # Query the bridge's legacy fallback directly; do not assume that a
        # missing modern ValuePattern means all read channels are safe.
        $legacy = $null
        if ($secretControl.TryGetCurrentPattern(
            [System.Windows.Automation.LegacyIAccessiblePattern]::Pattern, [ref]$legacy)) {
            $values = $legacy.Current
            Check-Not-Synthetic $values.Value 'SYNTHETIC_UIA_LEGACY_VALUE_EXPOSED'
            Check-Not-Synthetic $values.Name 'SYNTHETIC_UIA_LEGACY_NAME_EXPOSED'
            Check-Not-Synthetic $values.Description 'SYNTHETIC_UIA_LEGACY_DESCRIPTION_EXPOSED'
        }

        # The test has no browser, network or account data; scan only its own
        # small native window. No scalar or matching text is exported.
        $window = Get-Window
        $descendants = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($descendants.Count -gt 80) { throw 'SYNTHETIC_UIA_TREE_BOUND_REFUSED' }
        for ($i = 0; $i -lt $descendants.Count; $i++) {
            $current = $descendants.Item($i).Current
            Check-Not-Synthetic $current.Name 'SYNTHETIC_UIA_NAME_EXPOSED'
            Check-Not-Synthetic $current.HelpText 'SYNTHETIC_UIA_HELP_EXPOSED'
            Check-Not-Synthetic $current.ItemStatus 'SYNTHETIC_UIA_STATUS_EXPOSED'
            Check-Not-Synthetic $current.ItemType 'SYNTHETIC_UIA_ITEMTYPE_EXPOSED'
        }
    }

    $passphrase = Require-Element 'Synthetic.Probe.Passphrase'
    $fill = Require-Element 'Synthetic.Probe.Fill'
    $state = Require-Element 'Synthetic.Probe.State'
    if ($state.Current.Name -cne 'EMPTY') { throw 'SYNTHETIC_INITIAL_STATE_REFUSED' }
    Check-Exposure

    $fill.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $state = Require-Element 'Synthetic.Probe.State'
        if ($state.Current.Name -ceq 'FILLED') { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($state.Current.Name -cne 'FILLED') { throw 'SYNTHETIC_FILL_NOT_OBSERVED' }
    if ((Require-Element 'Synthetic.Probe.Fill').Current.IsEnabled) {
        throw 'SYNTHETIC_FILL_POSTCONDITION_REFUSED'
    }
    Check-Exposure

    [ordered]@{
        schema = 'suprachat-native-synthetic-secret-uia/v1'
        evidence = 'DISPOSABLE_LOCAL_WINDOWS_ONLY'
        passed = $true
        filled_control_verified = $true
        value_pattern_exposed = $false
        text_pattern_exposed = $false
        legacy_or_scalar_leak_observed = $false
        first_party_account_involved = $false
        credentials_read_or_exported = $false
        event_notification_channels_qualified = $false
    } | ConvertTo-Json
}
finally {
    Remove-Item Env:SUPRACHAT_SYNTHETIC_UIA_SECRET -ErrorAction SilentlyContinue
    $synthetic = $null
    if ($process) {
        try {
            $process.Refresh()
            if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
        } catch { }
        $process.Dispose()
    }
}
