param([Parameter(Mandatory)][string]$Fixture,[Parameter(Mandatory)][string]$Observer)
$ErrorActionPreference='Stop'
if (!$IsWindows) { throw 'WINDOWS_REQUIRED' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=$null;$q=$null
try {
  $p=Start-Process -FilePath (Resolve-Path -LiteralPath $Fixture).Path -PassThru
  $deadline=[DateTime]::UtcNow.AddSeconds(20);$root=$null
  do {
    $p.Refresh()
    if ($p.HasExited) { throw 'FIXTURE_EXITED' }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
      try {
        $r=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
        if ($r.Current.ProcessId -eq $p.Id -and $r.Current.AutomationId -ceq 'SupraChat.NativeValuePositiveFixture') { $root=$r }
      } catch {}
    }
    if (!$root) {Start-Sleep -Milliseconds 100}
  } while (!$root -and [DateTime]::UtcNow -lt $deadline)
  if (!$root) { throw 'FIXTURE_NOT_READY' }
  function Find-Exact([string]$id) {
    $x=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
    if ($x.Count -ne 1 -or $x[0].Current.ProcessId -ne $p.Id) {throw 'TARGET_IDENTITY_REFUSED'}
    return $x[0]
  }
  $button=Find-Exact 'Synthetic.NativeValue.Mutate'
  $field=Find-Exact 'Synthetic.NativeValue.Text'
  $start=[Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Observer).Path)
  $start.ArgumentList.Add([string]$p.Id)
  $start.ArgumentList.Add([string]$p.MainWindowHandle.ToInt64())
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true
  $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $q=[Diagnostics.Process]::Start($start)
  function Expect([string]$expected) {
    $line=$q.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($line -cne $expected) {throw 'NATIVE_VALUE_EVENT_NOT_VERIFIED'}
  }
  function Signal([string]$s) {$q.StandardInput.WriteLine($s);$q.StandardInput.Flush()}
  Expect 'READY'
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  if ($field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -cne 'ARMED') {
    throw 'PUBLIC_MUTATION_UNVERIFIED'
  }
  Signal 'MUTATED'
  Expect 'VALUE_OBSERVED_1'
  Expect 'REMOVED'
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  if ($field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -cne 'POST_REMOVAL') {
    throw 'POST_REMOVAL_MUTATION_UNVERIFIED'
  }
  Signal 'POST_REMOVAL'
  Expect 'POSITIVE_BRIDGE_PASS'
  if (!$q.WaitForExit(5000) -or $q.ExitCode -ne 0) {throw 'OBSERVER_EXIT_NOT_VERIFIED'}
  Write-Host 'NATIVE_WPF_VALUE_POSITIVE_AND_REMOVAL_PASS'
}
finally {
  if ($q) { try { $q.StandardInput.Close();if (!$q.HasExited) {$q.Kill();$null=$q.WaitForExit(2000)} }catch{};$q.Dispose() }
  if ($p) {try {if (!$p.HasExited) {Stop-Process -Id $p.Id -Force}}catch{};$p.Dispose()}
}
