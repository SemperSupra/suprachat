param([Parameter(Mandatory=$true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cli = Join-Path $root 'SupraChat.Automation/bin/Release/net10.0/SupraChat.Automation.dll'
$gui = Join-Path $root 'SupraChat/bin/Release/net10.0/SupraChat.dll'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$env:SUPRACHAT_W6_DISPOSABLE_ONLY = 'yes'
$env:SUPRACHAT_LEDGER_TOKEN = [guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N')
$env:SUPRACHAT_W6_GUI_PORT = [string]$port
$owner = $null; $desktop = $null; $passed = $false; $phase = 'START'
try {
  $pi = [Diagnostics.ProcessStartInfo]::new()
  $pi.FileName = (Get-Command dotnet).Source; $pi.UseShellExecute = $false
  foreach ($v in @($cli,'session-ledger-host','--port',[string]$port)) { $pi.ArgumentList.Add($v) }
  $owner = [Diagnostics.Process]::Start($pi)
  if (!$owner) { throw 'HOST_START_FAILED' }
  $deadline=[datetime]::UtcNow.AddSeconds(20)
  while ([datetime]::UtcNow -lt $deadline) {
    if ($owner.HasExited) { throw 'HOST_EXITED' }
    try {
      $h=Invoke-RestMethod "http://127.0.0.1:$port/v1/health" -Headers @{Authorization="Bearer $env:SUPRACHAT_LEDGER_TOKEN"} -TimeoutSec 2
      if ($h.schema -eq 'suprachat-w6-loopback-session-host/v1') { break }
    } catch { Start-Sleep -Milliseconds 150 }
  }
  if (!$h -or $h.schema -ne 'suprachat-w6-loopback-session-host/v1') { throw 'HOST_NOT_READY' }
  $phase = 'UIA'
  $desktop = [Diagnostics.Process]::Start((Get-Command dotnet).Source,$gui)
  if (!$desktop) { throw 'GUI_START_FAILED' }
  $window = $null; $deadline=[datetime]::UtcNow.AddSeconds(20)
  do {
    $desktop.Refresh()
    if ($desktop.HasExited) { throw 'GUI_EXITED' }
    if ($desktop.MainWindowHandle -ne [intptr]::Zero) {
      try { $window=[System.Windows.Automation.AutomationElement]::FromHandle($desktop.MainWindowHandle) } catch {}
    }
    if (!$window) { Start-Sleep -Milliseconds 150 }
  } while (!$window -and [datetime]::UtcNow -lt $deadline)
  if (!$window) { throw 'GUI_UIA_ROOT_UNAVAILABLE' }
  function Get-Control([string]$id) {
    $predicate=[System.Windows.Automation.PropertyCondition]::new(
      [System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $deadline=[datetime]::UtcNow.AddSeconds(18)
    while ([datetime]::UtcNow -lt $deadline) {
      try {
        $control=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$predicate)
        if ($control) { return $control }
      } catch [System.Runtime.InteropServices.COMException] {
        if ($_.Exception.HResult -notin @(-2147418113,-2147418111,-2147417846)) { throw }
      } catch [System.Windows.Automation.ElementNotAvailableException] {}
      Start-Sleep -Milliseconds 150
    }
    throw "UIA_MISSING:$id"
  }
  (Get-Control 'Tab.Sessions').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $entry=Get-Control 'Sessions.NewTitle'
  $entry.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic W1')
  (Get-Control 'Sessions.Create').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  function Inventory {
    $lines=& dotnet $cli session-ledger-list --port $port 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'CLI_FAILED' }
    $doc=($lines|Out-String)|ConvertFrom-Json
    if (!$doc.ok -or !$doc.value.succeeded) { throw 'CLI_REFUSED' }
    return $doc.value.payload
  }
  $phase = 'CLI'; $session=$null; $deadline=[datetime]::UtcNow.AddSeconds(15)
  while ([datetime]::UtcNow -lt $deadline) {
    $v=Inventory
    if ($v.sessions.Count -eq 1) { $session=@($v.sessions)[0]; break }
    Start-Sleep -Milliseconds 150
  }
  if (!$session -or $session.version -ne 0) { throw 'GUI_CLI_MISMATCH' }
  $phase='AGENT'
  $rpc=@{jsonrpc='2.0';id=77;method='session/ledger/change';params=@{
    port=$port;request=@{sessionId=$session.id;expectedVersion=0;operation='set-title';
    idempotencyKey='s0-synthetic-actor-update';audience='agent';value='Synthetic agent'}
  }}|ConvertTo-Json -Depth 8 -Compress
  $reply=($rpc | & dotnet $cli stdio 2>$null | Select-Object -Last 1)|ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or $reply.id -ne 77 -or !$reply.result.succeeded -or
      $reply.result.payload.session.version -ne 1) { throw 'AGENT_COMMIT_FAILED' }
  $phase='GUI_AGAIN'
  $entry.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic final')
  (Get-Control 'Sessions.Rename').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $final=$null; $deadline=[datetime]::UtcNow.AddSeconds(15)
  while ([datetime]::UtcNow -lt $deadline) {
    $v=Inventory
    if ($v.sessions.Count -eq 1 -and $v.sessions[0].version -eq 2) { $final=$v.sessions[0]; break }
    Start-Sleep -Milliseconds 150
  }
  if (!$final -or $final.id -ne $session.id) { throw 'THREE_AUDIENCE_CONVERGENCE_FAILED' }
  $passed=$true; $phase='PASS'
  'S0 normal W1 GUI, CLI and agent share one disposable session owner: PASS'
} finally {
  foreach ($p in @($desktop,$owner)) {
    if ($p) { if (!$p.HasExited) { $p.Kill($true); $null=$p.WaitForExit(4000) }; $p.Dispose() }
  }
  $result=@{schema='suprachat-s0-w1-bridge/v1';passed=$passed;phase=$phase;
    same_session_versions=$(if($passed){@(0,1,2)}else{@()});
    synthetic_only=$true;authenticated_principals=$false;production_storage=$false;
    provider_actions=$false}
  [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReceiptPath),($result|ConvertTo-Json -Depth 4 -Compress))
  Remove-Item Env:SUPRACHAT_W6_GUI_PORT,Env:SUPRACHAT_LEDGER_TOKEN,Env:SUPRACHAT_W6_DISPOSABLE_ONLY -ErrorAction SilentlyContinue
}
