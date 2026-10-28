param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$WinApp,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = (Resolve-Path -LiteralPath $Executable).Path
$cli = (Get-Command $WinApp).Source
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
$env:WINAPP_UI_WORKFLOW_ID = 'suprachat-synthetic-tab-' + [Guid]::NewGuid().ToString('N')
# Machine mode suppresses the welcome banner emitted by a fresh CLI cache.
$version = (& $cli --version --json | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -cne '0.7.1') { throw 'PINNED_WINAPP_REQUIRED' }
$results = @()
foreach ($variant in @('placeholder', 'webview')) {
    $process = $null
    try {
        # Visible disposable test fixture; no product profile or real page.
        $process = Start-Process -FilePath $exe -ArgumentList @('--variant', $variant) -PassThru
        function Get-ProbeRoot {
            $process.Refresh()
            if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
            $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if ($candidate.Current.ProcessId -eq $process.Id -and
                $candidate.Current.AutomationId -ceq 'SupraChat.SyntheticTabProbe') { return $candidate }
            return $null
        }
        function Find-ProbeControl($Root, [string]$Id) {
            return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
        }
        $readyExpected = if ($variant -ceq 'webview') { 'NATIVE_PAGE_READY' } else { 'PLACEHOLDER_READY' }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $ready = $false
        do {
            $root = Get-ProbeRoot
            if ($root) {
                $status = Find-ProbeControl $root 'Synthetic.TabProbe.Ready'
                $ready = $status -and $status.Current.Name -ceq $readyExpected
            }
            if (!$ready) { Start-Sleep -Milliseconds 100 }
        } while (!$ready -and [DateTime]::UtcNow -lt $deadline)
        if (!$ready) { throw 'SYNTHETIC_TAB_FIXTURE_NOT_READY' }
        $initialHandle = $process.MainWindowHandle
        function Read-ManagedSnapshot {
            $value = [ordered]@{root_accepted=$false;toolbar_found=$false;
                second_button_found=$false;second_selected=$false;completed=$false}
            try {
                $root = Get-ProbeRoot
                $value.root_accepted = $null -ne $root
                if ($root) {
                    $value.toolbar_found = $null -ne (Find-ProbeControl $root 'Synthetic.TabProbe.Toolbar')
                    $value.second_button_found = $null -ne (Find-ProbeControl $root 'Synthetic.TabProbe.SecondButton')
                    $tab = Find-ProbeControl $root 'Synthetic.TabProbe.SecondTab'
                    if ($tab) { $value.second_selected = $tab.GetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
                }
                $value.completed = $true
            } catch { } # Fixed unknown flags; never export provider exception text.
            return $value
        }
        function Read-ExternalSnapshot {
            $value = [ordered]@{root_accepted=$false;toolbar_found=$false;
                second_button_found=$false;second_selected=$false;completed=$false}
            try {
                $reply = & $cli ui status -w "$($initialHandle.ToInt64())" --json 2>$null
                if ($LASTEXITCODE -ne 0) { return $value }
                $identity = ($reply | Out-String | ConvertFrom-Json)
                if ($identity.processId -ne $process.Id) { return $value }
                # v0.7.1's selector lookup excludes the window root itself.
                # Inspect only the shallow, disposable fixture and retain no tree.
                $reply = & $cli ui inspect -w "$($initialHandle.ToInt64())" --depth 1 --json 2>$null
                if ($LASTEXITCODE -ne 0) { return $value }
                $identity = ($reply | Out-String | ConvertFrom-Json)
                $windows = @($identity.windows | Where-Object hwnd -eq $initialHandle.ToInt64())
                if ($windows.Count -ne 1) { return $value }
                $roots = @($windows[0].elements | Where-Object automationId -CEQ 'SupraChat.SyntheticTabProbe')
                if ($roots.Count -ne 1) { return $value }
                $value.root_accepted = $true
                foreach ($item in @(
                    @('Synthetic.TabProbe.Toolbar','IsEnabled','toolbar_found'),
                    @('Synthetic.TabProbe.SecondButton','IsEnabled','second_button_found'),
                    @('Synthetic.TabProbe.SecondTab','IsSelected','second_selected'))) {
                    $reply = & $cli ui get-property $item[0] -w "$($initialHandle.ToInt64())" --property $item[1] --json 2>$null
                    if ($LASTEXITCODE -ne 0) { return $value }
                    $observed = ($reply | Out-String | ConvertFrom-Json)
                    if ($observed.element.automationId -cne $item[0] -or
                        $observed.properties.($item[1]) -cnotin @('True','False')) { return $value }
                    $value[$item[2]] = $observed.properties.($item[1]) -ceq 'True'
                }
                $value.completed = $true
            } catch { }
            return $value
        }
        $before = Read-ManagedSnapshot
        if (!$before.completed -or !$before.root_accepted -or !$before.toolbar_found -or $before.second_selected) {
            throw 'SYNTHETIC_TAB_INITIAL_STATE_REFUSED'
        }
        $tab = Find-ProbeControl (Get-ProbeRoot) 'Synthetic.TabProbe.SecondTab'
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        # Observe through the persistent managed client and separate fresh WinApp
        # processes. One client cannot substitute for the other's missing evidence.
        Start-Sleep -Milliseconds 300
        $managed = Read-ManagedSnapshot
        $external = Read-ExternalSnapshot
        $process.Refresh()
        $sameHandle = $process.MainWindowHandle -eq $initialHandle
        $qualified = $managed.completed -and $managed.root_accepted -and $managed.toolbar_found -and
            $managed.second_button_found -and $managed.second_selected -and
            $external.completed -and $external.root_accepted -and $external.toolbar_found -and
            $external.second_button_found -and $external.second_selected -and $sameHandle
        $results += [ordered]@{variant=$variant;initial=$before;managed_after=$managed;
            external_after=$external;hwnd_unchanged=$sameHandle;
            peers_preserved=$qualified}
    }
    finally {
        if ($process) { $process.Refresh(); if (!$process.HasExited) { Stop-Process -Id $process.Id }; $process.Dispose() }
    }
}
$receipt = [ordered]@{schema='suprachat-synthetic-tab-root-comparison/v1';
    evidence='DISPOSABLE_SYNTHETIC_LOOPBACK';variants=$results;
    all_peers_preserved=(@($results | Where-Object {!$_.peers_preserved}).Count -eq 0);
    production_root_cause_established=$false;real_profile_used=$false;first_party_traffic=$false}
$stream = [IO.FileStream]::new([IO.Path]::GetFullPath($ReceiptPath),
    [IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$writer = [IO.StreamWriter]::new($stream)
try { $writer.Write(($receipt | ConvertTo-Json -Depth 8)) } finally { $writer.Dispose() }
$receipt | ConvertTo-Json -Depth 8
if (!$receipt.all_peers_preserved) { throw 'SYNTHETIC_TAB_PEER_LOSS_OR_UNKNOWN' }
