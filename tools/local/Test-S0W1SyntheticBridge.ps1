param([Parameter(Mandatory=$true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
  Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} elseif (Test-Path (Join-Path $root '_tools/dotnet/dotnet.exe')) {
  Join-Path $root '_tools/dotnet/dotnet.exe'
} elseif (Test-Path (Join-Path (Split-Path (Split-Path $root -Parent) -Parent) '_tools/dotnet/dotnet.exe')) {
  Join-Path (Split-Path (Split-Path $root -Parent) -Parent) '_tools/dotnet/dotnet.exe'
} else {
  (Get-Command dotnet).Source
}
$dotnetDir = Split-Path $dotnet -Parent
if ($dotnetDir -and (Test-Path $dotnetDir)) {
  $env:DOTNET_ROOT = $dotnetDir
  $env:PATH = "$dotnetDir;$env:PATH"
}
$cli = Join-Path $root 'SupraChat.Automation/bin/Release/net10.0/SupraChat.Automation.dll'
$gui = Join-Path $root 'SupraChat/bin/Release/net10.0/SupraChat.dll'
$guiExe = Join-Path $root 'SupraChat/bin/Release/net10.0/SupraChat.exe'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$env:SUPRACHAT_W6_DISPOSABLE_ONLY = 'yes'
$env:SUPRACHAT_LEDGER_TOKEN = [guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N')
$env:SUPRACHAT_W6_GUI_PORT = [string]$port
$env:SUPRACHAT_INITIAL_TAB = 'Sessions'
$owner = $null; $desktop = $null; $passed = $false; $phase = 'START'
try {
  $pi = [Diagnostics.ProcessStartInfo]::new()
  $pi.FileName = $dotnet; $pi.UseShellExecute = $false
  # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell 5.1.
  $pi.Arguments = ('"{0}" session-ledger-host --port {1}' -f $cli,$port)
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
  $desktopInfo = [Diagnostics.ProcessStartInfo]::new()
  if (Test-Path $guiExe) {
    $desktopInfo.FileName = $guiExe
    $desktopInfo.Arguments = ''
  } else {
    $desktopInfo.FileName = $dotnet
    $desktopInfo.Arguments = ('"{0}"' -f $gui)
  }
  $desktopInfo.UseShellExecute = $false
  $desktop = [Diagnostics.Process]::Start($desktopInfo)
  if (!$desktop) { throw 'GUI_START_FAILED' }
  $window = $null; $deadline=[datetime]::UtcNow.AddSeconds(20)
  $mainWinCond = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    'SupraChat.MainWindow')
  $procCond = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $desktop.Id)
  do {
    $desktop.Refresh()
    if ($desktop.HasExited) { throw 'GUI_EXITED' }
    $hwnd = $desktop.MainWindowHandle
    if ($hwnd -and $hwnd -ne [intptr]::Zero) {
      try {
        $candidate=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        if ($candidate.Current.AutomationId -eq 'SupraChat.MainWindow') {
          $window = $candidate
        }
      } catch {}
    }
    if (!$window) {
      try {
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
          [System.Windows.Automation.TreeScope]::Children,
          $procCond)
        if (!$candidate) {
          $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $procCond)
        }
        if ($candidate -and $candidate.Current.AutomationId -eq 'SupraChat.MainWindow') {
          $window = $candidate
        }
      } catch {}
    }
    if (!$window) {
      try {
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
          [System.Windows.Automation.TreeScope]::Children,
          $mainWinCond)
        if (!$candidate) {
          $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $mainWinCond)
        }
        if ($candidate -and $candidate.Current.AutomationId -eq 'SupraChat.MainWindow') {
          $window = $candidate
        }
      } catch {}
    }
    if (!$window) { Start-Sleep -Milliseconds 150 }
  } while (!$window -and [datetime]::UtcNow -lt $deadline)
  if (!$window) { throw 'GUI_UIA_ROOT_UNAVAILABLE' }
  function Get-Control([string]$id, [int]$timeoutSeconds = 20) {
    $predicate = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $procCondition = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $desktop.Id)
    $andCondition = New-Object System.Windows.Automation.AndCondition($procCondition, $predicate)
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
      $element = $null
      try {
        $desktop.Refresh()
        $hwnd = $null
        try { $hwnd = $desktop.MainWindowHandle } catch {}
        if ($hwnd -ne $null -and [int64]$hwnd -ne 0) {
          try {
            $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            if ($candidate -and $candidate.Current.ProcessId -eq $desktop.Id) {
              $element = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $predicate)
            }
          } catch {}
        }
        if (-not $element -and $window) {
          try {
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $predicate)
          } catch {}
        }
        if (-not $element) {
          try {
            $element = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
              [System.Windows.Automation.TreeScope]::Descendants, $andCondition)
          } catch {}
        }
      } catch {}
      if ($element) { return $element }
      Start-Sleep -Milliseconds 150
    } while ([datetime]::UtcNow -lt $deadline)
    throw "UIA_MISSING:$id"
  }
  $rootTabs = Get-Control 'RootTabs'
  $tab = $null
  try { $tab = Get-Control 'Tab.Sessions' 5 } catch {}
  if (!$tab) {
    try {
      $children = $rootTabs.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($c in $children) {
        if ($c.Current.AutomationId -eq 'Tab.Sessions' -or $c.Current.Name -eq 'Sessions') {
          $tab = $c
          break
        }
      }
      if (!$tab -and $children.Count -ge 4) {
        $tab = $children[3]
      }
    } catch {}
  }
  if (!$tab) { throw 'UIA_MISSING:Tab.Sessions' }
  try {
    $selection = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
  } catch {}
  $entry = Get-Control 'Sessions.NewTitle' 20
  $entry.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic W1')
  (Get-Control 'Sessions.Create').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  function Inventory {
    $lines=& $dotnet $cli session-ledger-list --port $port 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'CLI_FAILED' }
    $doc=($lines|Out-String)|ConvertFrom-Json
    if (!$doc.ok -or !$doc.value.succeeded) { throw 'CLI_REFUSED' }
    return $doc.value.payload
  }
  $phase = 'CLI'; $session=$null; $deadline=[datetime]::UtcNow.AddSeconds(15)
  while ([datetime]::UtcNow -lt $deadline) {
    $v=Inventory
    if ($v.sessions -and $v.sessions.Count -eq 1) { $session=@($v.sessions)[0]; break }
    Start-Sleep -Milliseconds 150
  }
  if (!$session -or $session.version -ne 0) { throw 'GUI_CLI_MISMATCH' }
  $phase='AGENT'
  $rpc=@{jsonrpc='2.0';id=77;method='session/ledger/change';params=@{
    port=$port;request=@{sessionId=$session.id;expectedVersion=0;operation='set-title';
    idempotencyKey='s0-synthetic-actor-update';audience='agent';value='Synthetic agent'}
  }}|ConvertTo-Json -Depth 8 -Compress
  # WinPS 5.1 promotes redirected native stderr into a terminating
  # NativeCommandError with ErrorActionPreference=Stop. Read both streams
  # explicitly and accept only the fixed metadata-only stdio readiness line.
  $agentInfo = [Diagnostics.ProcessStartInfo]::new()
  $agentInfo.FileName = $dotnet
  $agentInfo.Arguments = ('"{0}" stdio' -f $cli)
  $agentInfo.UseShellExecute = $false
  $agentInfo.CreateNoWindow = $true
  $agentInfo.RedirectStandardInput = $true
  $agentInfo.RedirectStandardOutput = $true
  $agentInfo.RedirectStandardError = $true
  # In .NET Framework 4.8 (Windows PowerShell 5.1), Process.Start with
  # RedirectStandardInput=true constructs StreamWriter(pipe, Console.InputEncoding)
  # and sets AutoFlush=true inside StartWithCreateProcess, which immediately
  # flushes Console.InputEncoding.GetPreamble() (3-byte UTF-8 BOM on CP65001
  # runners) to the pipe before Process.Start returns. Set Console.InputEncoding
  # to UTF-8 without BOM before starting the child process.
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  $priorInputEncoding = [Console]::InputEncoding
  [Console]::InputEncoding = $utf8NoBom
  try {
    $agent = [Diagnostics.Process]::Start($agentInfo)
    if (!$agent) { throw 'AGENT_START_FAILED' }
    try {
      $stdoutTask = $agent.StandardOutput.ReadToEndAsync()
      $stderrTask = $agent.StandardError.ReadToEndAsync()
      $rpcBytes = $utf8NoBom.GetBytes($rpc + "`n")
      $agent.StandardInput.BaseStream.Write($rpcBytes, 0, $rpcBytes.Length)
      $agent.StandardInput.BaseStream.Flush()
      $agent.StandardInput.BaseStream.Dispose()
      if (!$agent.WaitForExit(12000)) {
        $agent.Kill()
        throw 'AGENT_TIMEOUT'
      }
      $stdoutText = $stdoutTask.GetAwaiter().GetResult()
      $stderrText = $stderrTask.GetAwaiter().GetResult()
      if ($agent.ExitCode -ne 0) { throw 'AGENT_PROCESS_FAILED' }
      if ($stderrText.Trim() -ne '{"suprachat":"stdio-ready","schema":"suprachat-jsonrpc/v1"}') {
        throw 'AGENT_STDERR_UNEXPECTED'
      }
      $lines = @($stdoutText -split "\r?\n" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
      if ($lines.Count -ne 1) { throw 'AGENT_RESPONSE_COUNT_INVALID' }
      $reply = $lines[0] | ConvertFrom-Json
    } finally {
      $agent.Dispose()
    }
  } finally {
    [Console]::InputEncoding = $priorInputEncoding
  }
  # Log only bounded classification flags; never echo untrusted
  # RPC response bodies or synthetic bearer values into public CI.
  if ($reply.id -ne 77) { throw 'AGENT_RPC_ID_MISMATCH' }
  if (!$reply.result) { throw 'AGENT_RPC_RESULT_MISSING' }
  if (!$reply.result.succeeded) { throw 'AGENT_RPC_APPLICATION_REFUSED' }
  if (!$reply.result.payload) { throw 'AGENT_RPC_PAYLOAD_MISSING' }
  if (!$reply.result.payload.session) { throw 'AGENT_RPC_SESSION_MISSING' }
  if ($reply.result.payload.session.version -ne 1) { throw 'AGENT_RPC_VERSION_MISMATCH' }
  $phase='GUI_AGAIN'
  $entry = Get-Control 'Sessions.NewTitle'
  $entry.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic final')
  (Get-Control 'Sessions.Rename').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $final=$null; $deadline=[datetime]::UtcNow.AddSeconds(15)
  while ([datetime]::UtcNow -lt $deadline) {
    $v=Inventory
    if ($v.sessions -and $v.sessions.Count -eq 1 -and $v.sessions[0].version -eq 2) { $final=$v.sessions[0]; break }
    Start-Sleep -Milliseconds 150
  }
  if (!$final -or $final.id -ne $session.id) { throw 'THREE_AUDIENCE_CONVERGENCE_FAILED' }
  $passed=$true; $phase='PASS'
  'S0 normal W1 GUI, CLI and agent share one disposable session owner: PASS'
} finally {
  foreach ($p in @($desktop,$owner)) {
    if ($p) { if (!$p.HasExited) { $p.Kill(); $null=$p.WaitForExit(4000) }; $p.Dispose() }
  }
  $result=@{schema='suprachat-s0-w1-bridge/v1';passed=$passed;phase=$phase;
    powershell_major=$PSVersionTable.PSVersion.Major;
    same_session_versions=$(if($passed){@(0,1,2)}else{@()});
    synthetic_only=$true;authenticated_principals=$false;production_storage=$false;
    provider_actions=$false}
  [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReceiptPath),($result|ConvertTo-Json -Depth 4 -Compress))
  Remove-Item Env:SUPRACHAT_W6_GUI_PORT,Env:SUPRACHAT_W6_LEDGER_TOKEN,Env:SUPRACHAT_LEDGER_TOKEN,Env:SUPRACHAT_W6_DISPOSABLE_ONLY -ErrorAction SilentlyContinue
}
