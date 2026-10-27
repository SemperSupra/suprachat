param(
    [Parameter(Mandatory)][string]$Executable
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
$exe = [IO.Path]::GetFullPath($Executable)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'QUALIFIED_EXECUTABLE_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# Strictly test-only assembly, never added to the real application package.
$interop = Join-Path ([IO.Path]::GetDirectoryName($exe)) 'Interop.UIAutomationClient.dll'
if (!(Test-Path -LiteralPath $interop -PathType Leaf)) { throw 'UIA3_COM_INTEROP_REQUIRED' }
$interopAssembly = [Reflection.Assembly]::LoadFrom($interop)
$types = @($interopAssembly.GetExportedTypes())
$uiaInterface = @($types | Where-Object { $_.Name -eq 'IUIAutomation' -and $_.IsInterface })
$scopeTypes = @($types | Where-Object { $_.Name -eq 'TreeScope' -and $_.IsEnum })
if ($uiaInterface.Count -ne 1 -or $scopeTypes.Count -ne 1) {
    # Log only public library type names, never UI tree content or sentinel.
    Write-Host ('UIA3 qualified metadata types: ' + (($types | Where-Object {
        $_.Name -match 'Automation|TreeScope|CUIA'
    } | Select-Object -ExpandProperty FullName | Select-Object -First 20) -join ','))
    throw 'UIA3_REQUIRED_INTERFACE_NOT_FOUND'
}
$script:nativeTreeScope = [Enum]::Parse($scopeTypes[0], 'TreeScope_Descendants')
$factory = @($types | Where-Object { $_.Name -eq 'CUIAutomationClass' -and $_.IsClass })
if ($factory.Count -eq 1) {
    # TlbImp exposes COM coclasses as *Class, not their matching interface.
    $script:nativeUia = [Activator]::CreateInstance($factory[0])
} else {
    $comType = [Type]::GetTypeFromCLSID([Guid]'ff48dba4-60ef-4201-aa87-54103eef594e')
    $comObject = [Activator]::CreateInstance($comType)
    $script:nativeUia = $comObject -as $uiaInterface[0]
}
if ($null -eq $script:nativeUia) { throw 'UIA3_COM_ACTIVATION_REFUSED' }

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

        # The stock .NET 10 managed client does not expose the Legacy pattern
        # or register property 30090. Use the independent Windows UIA3 COM
        # interface from pinned test-only Interop.UIAutomationClient instead.
        # The native request asks the actual provider for its legacy fallback
        # availability (30090) and scalars (30092/30093/30094).
        $root = Get-Window
        $nativeRoot = $script:nativeUia.ElementFromHandle($process.MainWindowHandle)
        $comPidBound = $nativeRoot.CurrentProcessId -eq $process.Id
        $comHwndBound = [long]$nativeRoot.CurrentNativeWindowHandle -eq $process.MainWindowHandle.ToInt64()
        $comId = $nativeRoot.CurrentAutomationId
        $comIdBound = $comId -ceq 'SupraChat.SyntheticSecurityProbe'
        # Independently bind exact HWND + PID to the managed UIA root already
        # checked above. Some UIA3 proxy roots omit the declared app root ID;
        # an alternate nonblank identity is still a hard refusal.
        if (!$comPidBound -or !$comHwndBound -or
            (!$comIdBound -and ![string]::IsNullOrEmpty($comId))) {
            Write-Host ('Synthetic COM root binding diagnostics: ' + (@{
                pid_match = $comPidBound; hwnd_match = $comHwndBound
                id_match = $comIdBound; id_blank = [string]::IsNullOrEmpty($comId)
            } | ConvertTo-Json -Compress))
            throw 'SYNTHETIC_COM_WINDOW_BINDING_REFUSED'
        }
        $nativeCondition = $script:nativeUia.CreatePropertyCondition(30011, 'Synthetic.Probe.Passphrase')
        $nativeSecret = $nativeRoot.FindFirst(
            $script:nativeTreeScope, $nativeCondition)
        if ($null -eq $nativeSecret -or $nativeSecret.CurrentProcessId -ne $process.Id) {
            throw 'SYNTHETIC_COM_CONTROL_BINDING_REFUSED'
        }
        $legacyPresent = $nativeSecret.GetCurrentPropertyValue(30090)
        if ($legacyPresent -isnot [bool]) { throw 'SYNTHETIC_LEGACY_AVAILABILITY_UNQUALIFIED' }
        foreach ($entry in @(
            @{ id=30092; failure='SYNTHETIC_UIA_LEGACY_NAME_EXPOSED' },
            @{ id=30093; failure='SYNTHETIC_UIA_LEGACY_VALUE_EXPOSED' },
            @{ id=30094; failure='SYNTHETIC_UIA_LEGACY_DESCRIPTION_EXPOSED' }
        )) {
            $readback = $nativeSecret.GetCurrentPropertyValue($entry.id)
            if ($legacyPresent -and $null -eq $readback) {
                throw 'SYNTHETIC_LEGACY_READBACK_UNQUALIFIED'
            }
            if ($readback -is [string]) { Check-Not-Synthetic $readback $entry.failure }
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
