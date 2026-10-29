param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$ObserverExecutable,
    [Parameter(Mandatory)][string]$ReceiptPath, [switch]$NegativeNoPropertyListener)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$fixture=$null; $observer=$null; $evidence=$null; $passed=$false; $failure='EVENT_WITNESS_NOT_VERIFIED'
$synthetic='SyntheticOnly'+[Guid]::NewGuid().ToString('N')
try {
    $env:SUPRACHAT_SYNTHETIC_UIA_SECRET=$synthetic
    $fixture=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -PassThru
    Remove-Item Env:SUPRACHAT_SYNTHETIC_UIA_SECRET
    $deadline=[DateTime]::UtcNow.AddSeconds(15); $root=$null
    do {
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'FIXTURE_UNAVAILABLE' }
        if ($fixture.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate=[System.Windows.Automation.AutomationElement]::FromHandle($fixture.MainWindowHandle)
            if ($candidate.Current.ProcessId -eq $fixture.Id -and $candidate.Current.AutomationId -ceq 'SupraChat.SyntheticSecurityProbe') { $root=$candidate }
        }
        if (!$root) { Start-Sleep -Milliseconds 100 }
    } while (!$root -and [DateTime]::UtcNow -lt $deadline)
    if (!$root) { throw 'FIXTURE_UNAVAILABLE' }
    $hwnd=$fixture.MainWindowHandle
    function Find-Control([string]$Id) {
        $fixture.Refresh()
        if ($fixture.HasExited -or $fixture.MainWindowHandle -ne $hwnd) { throw 'FIXTURE_IDENTITY_REFUSED' }
        $root=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        if ($root.Current.ProcessId -ne $fixture.Id -or $root.Current.AutomationId -cne 'SupraChat.SyntheticSecurityProbe') { throw 'FIXTURE_IDENTITY_REFUSED' }
        $matches=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
        if ($matches.Count -ne 1) { throw 'FIXTURE_IDENTITY_REFUSED' }
        return $matches[0]
    }
    $env:SUPRACHAT_SYNTHETIC_UIA_SECRET=$synthetic
    $start=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($ObserverExecutable))
    $start.ArgumentList.Add([string]$fixture.Id);$start.ArgumentList.Add([string]$hwnd.ToInt64())
    if ($NegativeNoPropertyListener) { $start.ArgumentList.Add('--negative-no-property-listener') }
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $observer=[Diagnostics.Process]::Start($start)
    Remove-Item Env:SUPRACHAT_SYNTHETIC_UIA_SECRET
    function Expect([string]$Expected) {
        $line=$observer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
        if ($line -cne $Expected) { throw 'EVENT_HANDSHAKE_REFUSED' }
    }
    function Signal([string]$Value) { $observer.StandardInput.WriteLine($Value);$observer.StandardInput.Flush() }
    function Invoke-Control([string]$Id) { (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Expect 'LISTENER_READY'
    Invoke-Control 'Synthetic.Probe.EventControl'; Signal 'CONTROL'
    Expect 'CONTROL_EVENT_OBSERVED'
    Invoke-Control 'Synthetic.Probe.Fill'; Signal 'FILLED'
    Expect 'LISTENERS_REMOVED'
    Invoke-Control 'Synthetic.Probe.EventControl'
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while ((Find-Control 'Synthetic.Probe.State').Current.Name -cne 'POST_REMOVAL' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if ((Find-Control 'Synthetic.Probe.State').Current.Name -cne 'POST_REMOVAL') { throw 'POST_REMOVAL_MUTATION_UNVERIFIED' }
    Signal 'POST_REMOVAL'
    $line=$observer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if (!$line -or $line.Length -gt 8192 -or $line.Contains($synthetic)) { throw 'EVENT_RECEIPT_REFUSED' }
    $evidence=$line|ConvertFrom-Json
    if (!$observer.WaitForExit(5000) -or $observer.ExitCode -ne 0 -or
        $evidence.schema -cne 'suprachat-synthetic-uia-property-event-witness/v1' -or !$evidence.passed -or
        !$evidence.positive_control_observed -or !$evidence.fill_control_observed -or
        !$evidence.value_control_observed -or !$evidence.value_fill_observed -or !$evidence.removal_returned -or
        !$evidence.post_removal_mutation_verified -or $evidence.fresh_post_removal_delivery -or
        $evidence.callback_unknown -or $evidence.sensitive_value_disclosed -or $evidence.raw_values_exported) { throw 'EVENT_RECEIPT_REFUSED' }
    $passed=$true;$failure='NONE'
}
catch {
    $known=@('FIXTURE_UNAVAILABLE','FIXTURE_IDENTITY_REFUSED','EVENT_HANDSHAKE_REFUSED','POST_REMOVAL_MUTATION_UNVERIFIED','EVENT_RECEIPT_REFUSED')
    $failure=if($_.Exception.Message -cin $known){$_.Exception.Message}else{'EVENT_WITNESS_NOT_VERIFIED'}
}
finally {
    Remove-Item Env:SUPRACHAT_SYNTHETIC_UIA_SECRET -ErrorAction SilentlyContinue
    $synthetic=$null
    $ownedExitConfirmed=$true
    foreach($owned in @($observer,$fixture)) { if($owned){try{if(!$owned.HasExited){$owned.Kill();if(!$owned.WaitForExit(5000)){$ownedExitConfirmed=$false}}}catch{$ownedExitConfirmed=$false};$owned.Dispose()} }
    if(!$ownedExitConfirmed){$passed=$false;$failure='OWNED_PROCESS_EXIT_UNCONFIRMED'}
    $expectedNegativeRefusal=$NegativeNoPropertyListener.IsPresent -and !$passed -and $failure -ceq 'EVENT_HANDSHAKE_REFUSED' -and $ownedExitConfirmed
    $receipt=[ordered]@{schema='suprachat-synthetic-uia-event-test/v1';passed=$passed;failure=$failure;observer=$evidence;
        first_party_account_involved=$false;raw_values_exported=$false;owned_process_exit_confirmed=$ownedExitConfirmed;
        negative_control_expected=$NegativeNoPropertyListener.IsPresent;negative_control_refused=$expectedNegativeRefusal}
    $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($ReceiptPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $writer=[IO.StreamWriter]::new($stream)
    try{$writer.Write(($receipt|ConvertTo-Json -Depth 8))}finally{$writer.Dispose()}
    $receipt|ConvertTo-Json -Depth 8
}
if($NegativeNoPropertyListener -and !$expectedNegativeRefusal){throw 'NEGATIVE_LISTENER_TEST_FAILED'}
if(!$passed -and !$expectedNegativeRefusal){throw $failure}
