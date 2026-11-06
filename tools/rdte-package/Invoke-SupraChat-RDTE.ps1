#requires -Version 5.1
<#
.SYNOPSIS
  SupraChat local, model-free RDTE driver. Source and decisions live in private GitHub DLE.
.DESCRIPTION
  No Git, Docker, Python, model, administrator privilege, or global installation required.
  SelfTest and Plan do not change local files. Apply uses only an owner-marked disposable
  run directory; Publish posts sanitized receipts to the private owning DLE.
  Running source tests is NOT a hostile-code sandbox. Use a dedicated VM if that is required.
#>
[CmdletBinding()]
param(
  [ValidateSet('SelfTest','LocalSmoke','Plan','Apply','HygienePlan','HygieneApply','Verify','Publish','Undo')]
  [string]$Mode = 'Plan',
  [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_-]{2,47}$')]
  [string]$RunLabel = 'model-a-local-01',
  [switch]$InstallDisposableDotNet,
  [switch]$InstallDisposableGo,
  [switch]$ConfirmUndo
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:DleRepo = ''
$script:DleIssue = 0
$script:SourceRepo = 'SemperSupra/suprachat'
$script:SourceSha = ('1'*40) # synthetic only; real source from bootstrap owner
$script:Contract = 'V7'
$script:SdkDotNet = '10.0.403'
$script:SdkGo = 'go1.23.12'
$script:ScriptFile = [IO.Path]::GetFullPath($PSCommandPath)
$script:DriverSha = (Get-FileHash -LiteralPath $script:ScriptFile -Algorithm SHA256).Hash.ToLowerInvariant()
$script:AdmissionMarker = 'SUPRACHAT-RDTE-DRIVER/' + $script:Contract + ' SOURCE_SHA=' + $script:SourceSha + ' DRIVER_SHA256=' + $script:DriverSha
$script:Base = [IO.Path]::GetFullPath((Join-Path $HOME '.local\share\suprachat-rdte\runs'))
$script:RunRoot = [IO.Path]::GetFullPath((Join-Path $script:Base $RunLabel))
$script:OwnerFile = Join-Path $script:RunRoot 'owner.json'
$script:StateFile = Join-Path $script:RunRoot 'state.json'
$script:SummaryFile = Join-Path $script:RunRoot 'summary.json'
$script:Cache = $null
$script:HygieneFile = Join-Path $script:RunRoot 'hygiene.json'
$script:HygieneStateFile = Join-Path $script:RunRoot 'hygiene-state.json'

function Assert-LocalSafety {
  if ($PSVersionTable.PSVersion.Major -lt 5) { throw 'PowerShell 5.1 or newer is required.' }
  if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This driver is for Windows; do not run on a cloud/Linux host.' }
  $prefix = $script:Base.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $script:RunRoot.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Run path escapes disposable base.' }
  if ($script:RunRoot -eq $script:Base -or $script:RunRoot -eq [IO.Path]::GetPathRoot($script:RunRoot)) { throw 'Unsafe cleanup path.' }
  foreach ($p in @((Join-Path $HOME '.local'),(Join-Path $HOME '.local\share'),$script:Base,$script:RunRoot)) {
    if (Test-Path -LiteralPath $p) {
      $item=Get-Item -LiteralPath $p -Force
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse point not allowed: ' + $p) }
    }
  }
}
function Assert-NoReparseTree([string]$p) {
  if (-not (Test-Path -LiteralPath $p)) { return }
  $pending=New-Object System.Collections.Stack
  $pending.Push((Get-Item -LiteralPath $p -Force))
  while ($pending.Count -gt 0) {
    $item=$pending.Pop()
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Refusing reparse point: ' + $item.FullName) }
    if ($item.PSIsContainer) {
      foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force)) { $pending.Push($child) }
    }
  }
}
function Read-Json([string]$p) {
  if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw ('Required state missing: ' + $p) }
  return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Write-Json([object]$object,[string]$p) {
  # V4 Windows raised an invalid-path exception in the File.Replace call.
  # The exact runtime cause remains unproven; use an explicit valid backup
  # path and fail closed on interrupted transactions rather than assuming it.
  $tmp = $p + '.pending'
  $backup = $p + '.previous'
  if ([IO.File]::Exists($tmp) -or [IO.File]::Exists($backup)) {
    throw 'Ambiguous prior JSON transaction residue; preserve local evidence and do not overwrite.'
  }
  $json = ConvertTo-Json -InputObject $object -Depth 15 -Compress
  [IO.File]::WriteAllText($tmp,$json + [Environment]::NewLine,([System.Text.UTF8Encoding]::new($false)))
  if ([IO.File]::Exists($p)) {
    [IO.File]::Replace($tmp,$p,$backup)
    # Previous version is retained if cleanup fails; a following write is
    # refused until the state is reconciled, not silently overwritten.
    if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
  } else {
    [IO.File]::Move($tmp,$p)
  }
}
function Hash-File([string]$p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Initialize-Root {
  Assert-LocalSafety
  if (-not (Test-Path -LiteralPath $script:RunRoot)) { New-Item -ItemType Directory -Force -Path $script:RunRoot | Out-Null }
  Assert-LocalSafety
  if (-not (Test-Path -LiteralPath $script:OwnerFile)) {
    if (@(Get-ChildItem -LiteralPath $script:RunRoot -Force).Count -gt 0) { throw 'Unowned existing run directory; refusing adoption.' }
    Write-Json ([ordered]@{schema='suprachat-rdte-owner/v7'; run_label=$RunLabel; source_sha=$script:SourceSha; driver_sha256=$script:DriverSha; dle_repo=$script:DleRepo; dle_issue=$script:DleIssue; created_utc=[DateTime]::UtcNow.ToString('o')}) $script:OwnerFile
  }
  Assert-Owner
}
function Assert-Owner {
  Assert-LocalSafety
  $o = Read-Json $script:OwnerFile
  if ($o.schema -ne 'suprachat-rdte-owner/v7' -or $o.run_label -ne $RunLabel -or $o.source_sha -ne $script:SourceSha -or $o.driver_sha256 -ne $script:DriverSha -or $o.dle_repo -ne $script:DleRepo -or [int]$o.dle_issue -ne $script:DleIssue) { throw 'Owner identity drift; refuse mutation or cleanup.' }
}
function Resolve-Token {
  if ($env:GH_TOKEN) { return $env:GH_TOKEN }
  if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if ($gh) {
    $v = (& $gh.Source auth token 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $v) { return $v }
  }
  throw 'Private DLE authorization absent. Provide process-local GH_TOKEN or pre-authenticated gh; no global installation is performed.'
}
function GitHub([string]$Method,[string]$Path,[object]$Body=$null) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $token = Resolve-Token
  $url = 'https://api.github.com' + $Path
  $hdr = @{Authorization=('Bearer ' + $token); Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'; 'User-Agent'='suprachat-rdte-local-v7'}
  $args = @{Uri=$url; Method=$Method; Headers=$hdr; UseBasicParsing=$true; TimeoutSec=45}
  if ($null -ne $Body) { $args.Body = ConvertTo-Json -InputObject $Body -Compress -Depth 10; $args.ContentType='application/json; charset=utf-8' }
  return Invoke-RestMethod @args
}
function Find-Promotion([object[]]$comments) {
  $promotions=New-Object System.Collections.ArrayList
  foreach($entry in $comments) {
    if($null -eq $entry -or $null -eq $entry.id -or $null -eq $entry.body -or $null -eq $entry.user){continue}
    if([string]$entry.user.login -cne 'mark-e-deyoung'){continue}
    $lines=@((Normalize-ReceiptBody ([string]$entry.body)).Split([char]10))
    if($lines.Count -eq 0 -or $lines[0] -cne 'SUPRACHAT-RDTE-PROMOTION/V1'){continue}
    if($lines.Count -lt 6 -or $lines[1] -cne ''){throw 'Malformed first-line promotion envelope.'}
    $fields=@{}
    for($i=2;$i -lt $lines.Count;$i++){
      if([string]::IsNullOrWhiteSpace($lines[$i])){continue}
      $pos=$lines[$i].IndexOf(': ')
      if($pos -le 0){throw 'Malformed promotion field.'}
      $key=$lines[$i].Substring(0,$pos)
      $value=$lines[$i].Substring($pos+2)
      if(@('Status','SourceSHA','AssetPath','AssetSHA256') -cnotcontains $key -or $fields.ContainsKey($key)){
        throw 'Unknown or duplicate promotion field.'
      }
      $fields[$key]=$value
    }
    if($fields.Count -ne 4 -or $fields.Status -cnotin @('ADMITTED','REVOKED') -or
       $fields.SourceSHA -cnotmatch '^[0-9a-f]{40}$' -or
       $fields.AssetPath -cne 'tools/rdte-package/Invoke-SupraChat-RDTE.ps1' -or
       $fields.AssetSHA256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid promotion fields.'}
    [void]$promotions.Add([pscustomobject]@{
      id=[long]$entry.id;Status=$fields.Status;SourceSHA=$fields.SourceSHA;
      AssetPath=$fields.AssetPath;AssetSHA256=$fields.AssetSHA256
    })
  }
  if($promotions.Count -eq 0){throw 'No owner-admitted promotion.'}
  $ordered=@($promotions|Sort-Object -Property id)
  for($i=1;$i -lt $ordered.Count;$i++){
    if($ordered[$i].id -eq $ordered[$i-1].id){throw 'Duplicate promotion identity.'}
  }
  return $ordered[-1]
}
function Read-Dle {
  $issue=GitHub 'GET' ('/repos/'+$script:DleRepo+'/issues/'+$script:DleIssue)
  if($issue.state -ne 'open'){throw 'DLE owner issue is not open.'}
  $all=New-Object System.Collections.ArrayList
  $pages=0
  for($page=1;$page -le 20;$page++){
    $response=GitHub 'GET' ('/repos/'+$script:DleRepo+'/issues/'+$script:DleIssue+'/comments?per_page=100&page='+$page)
    $items=@($response)
    if($items.Count -eq 1 -and $null -ne $items[0] -and $items[0] -is [System.Array]){$items=@($items[0])}
    foreach($item in $items){if($null -ne $item){[void]$all.Add($item)}}
    $pages++
    if($items.Count -lt 100){break}
    if($page -eq 20){throw 'DLE pagination bound reached.'}
  }
  $comments=@($all.ToArray())
  $latest=Find-Promotion $comments
  if($latest.Status -cne 'ADMITTED'){throw 'Owner has revoked latest promotion.'}
  if($latest.SourceSHA -cne $script:SourceSha -or $latest.AssetSHA256 -cne $script:DriverSha){
    throw 'Owner promotion does not match exact downloaded script/source SHA.'
  }
  return @{issue=$issue;comments=$comments;admission=$latest;pages=$pages}
}
function Initialize-BootstrapAuthority {
  $folder=Split-Path -Parent $script:ScriptFile
  $bootstrapOwner=Join-Path $folder 'owner.json'
  $configPath=Join-Path $HOME '.local\share\suprachat-rdte\bootstrap\authority.json'
  $owner=Read-Json $bootstrapOwner
  $config=Read-Json $configPath
  if([string]$owner.contract -cne 'suprachat-asset/v1' -or
     [string]$owner.asset_path -cne 'tools/rdte-package/Invoke-SupraChat-RDTE.ps1' -or
     [string]$owner.asset_sha256 -cne $script:DriverSha -or
     [string]$owner.source_sha -cnotmatch '^[0-9a-f]{40}$' -or
     [string]$config.contract -cne 'suprachat-dle-authority/v1' -or
     [string]$config.owner -cne 'mark-e-deyoung' -or
     [string]$config.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
     [int]$config.issue -lt 1){throw 'Bootstrap owner/DLE authority identity mismatch.'}
  $script:DleRepo=[string]$config.repository
  $script:DleIssue=[int]$config.issue
  $script:SourceSha=[string]$owner.source_sha
  $script:AdmissionMarker='SUPRACHAT-RDTE-PROMOTION/V1'
}
function Ensure-Admission {
  $d = Read-Dle
  Write-Host ('DLE admitted: https://github.com/' + $script:DleRepo + '/issues/' + $script:DleIssue)
  Write-Host ('Pinned source: ' + $script:SourceSha + '; driver: ' + $script:DriverSha)
  return $d
}
function Normalize-ReceiptBody([string]$body) {
  # Normalize transport line endings only. Do not trim substantive evidence.
  return ($body -replace "`r`n", "`n")
}
function Existing-Receipt([object]$d,[string]$marker) {
  # A kickoff plan can contain the literal marker inside a paragraph. It is
  # NOT a return receipt. Require exact first line, blank separator and Status.
  # A malformed top-line candidate is an ambiguity, not permission to overwrite.
  $receiptCandidates=New-Object System.Collections.ArrayList
  foreach($c in @($d.comments)) {
    if ($null -eq $c -or $null -eq $c.body) { continue }
    $body=Normalize-ReceiptBody ([string]$c.body)
    $lines=@($body -split "`n")
    if ($lines.Count -eq 0 -or $lines[0] -cne $marker) { continue }
    if ($lines.Count -lt 3 -or $lines[1] -cne '' -or $lines[2] -cnotmatch '^Status: (CENSUS_COMPLETE|COMPLETE|INCOMPLETE|STRUCTURAL_ID_FOUND|STRUCTURAL_ID_NOT_FOUND)$') {
      throw 'Malformed first-line DLE result marker; manual reconciliation required.'
    }
    [void]$receiptCandidates.Add($c)
  }
  if ($receiptCandidates.Count -gt 1) { throw 'Duplicate first-line DLE receipts; manual reconciliation required.' }
  if ($receiptCandidates.Count -eq 1) { return $receiptCandidates[0] }
  return $null
}
function Checked-Receipt([object]$d,[string]$marker,[string]$expectedBody) {
  $receipt=Existing-Receipt $d $marker
  if ($null -eq $receipt) { return $null }
  if ((Normalize-ReceiptBody ([string]$receipt.body)) -cne (Normalize-ReceiptBody $expectedBody)) {
    throw 'First-line remote receipt payload differs from locally verified evidence; preserve both.'
  }
  return $receipt
}
function Download([string]$url,[string]$destination) {
  [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 600 | Out-Null
  if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) { throw 'Download was empty.' }
}
function Get-PortableDotNet {
  $installed = Get-Command dotnet.exe -ErrorAction SilentlyContinue
  if ($installed) { return $installed.Source }
  if (-not $InstallDisposableDotNet) { return $null }
  $dir = Join-Path $script:RunRoot 'sdk\dotnet'
  $exe = Join-Path $dir 'dotnet.exe'
  if (Test-Path -LiteralPath $exe) { return $exe }
  $meta = Invoke-RestMethod -Uri 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 45
  $release = @($meta.releases | Where-Object { $_.sdk.version -eq $script:SdkDotNet } | Select-Object -First 1)
  if ($release.Count -ne 1) { throw 'Pinned Microsoft SDK version not in vendor metadata.' }
  $package = @($release[0].sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1)
  if ($package.Count -ne 1 -or -not $package[0].hash -or $package[0].hash -notmatch '^[0-9a-fA-F]{128}$') { throw 'Microsoft SHA512 metadata missing.' }
  $zip = Join-Path $script:RunRoot 'dotnet-pinned.zip'
  Download ([string]$package[0].url) $zip
  if ((Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash -ine [string]$package[0].hash) { throw 'Microsoft SDK SHA512 mismatch.' }
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
  Assert-NoReparseTree $dir
  if (-not (Test-Path -LiteralPath $exe)) { throw 'Portable dotnet executable missing.' }
  return $exe
}
function Get-PortableGo {
  $installed = Get-Command go.exe -ErrorAction SilentlyContinue
  if ($installed) { return $installed.Source }
  if (-not $InstallDisposableGo) { return $null }
  $exe = Join-Path $script:RunRoot 'sdk\go\bin\go.exe'
  if (Test-Path -LiteralPath $exe) { return $exe }
  $meta = Invoke-RestMethod -Uri 'https://go.dev/dl/?mode=json&include=all' -TimeoutSec 45
  $version = @($meta | Where-Object { $_.version -eq $script:SdkGo } | Select-Object -First 1)
  if ($version.Count -ne 1) { throw 'Pinned Go version not in official release metadata.' }
  $f = @($version[0].files | Where-Object { $_.filename -eq ($script:SdkGo + '.windows-amd64.zip') } | Select-Object -First 1)
  if ($f.Count -ne 1 -or [string]$f[0].sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Go SHA256 metadata missing.' }
  $zip = Join-Path $script:RunRoot 'go-pinned.zip'
  Download ('https://go.dev/dl/' + $f[0].filename) $zip
  if ((Hash-File $zip) -ine [string]$f[0].sha256) { throw 'Go SDK SHA256 mismatch.' }
  $dir=Join-Path $script:RunRoot 'sdk'
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
  Assert-NoReparseTree $dir
  if (-not (Test-Path -LiteralPath $exe)) { throw 'Portable Go executable missing.' }
  return $exe
}
function Get-Source {
  $out = Join-Path $script:RunRoot 'src'
  $pin = Join-Path $out 'source-sha.txt'
  if (Test-Path -LiteralPath $pin) {
    if ((Get-Content -LiteralPath $pin -Raw).Trim() -ne $script:SourceSha) { throw 'Cached source SHA drift.' }
    $dirs = @(Get-ChildItem -LiteralPath $out -Directory | Where-Object { $_.Name -ne '.local' })
    if ($dirs.Count -eq 1) { return $dirs[0].FullName }
    throw 'Source cache ambiguous.'
  }
  if (Test-Path -LiteralPath $out) { throw 'Partial source extraction exists; refuse blind retry.' }
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  $zip = Join-Path $script:RunRoot 'source-immutable.zip'
  Download ('https://api.github.com/repos/' + $script:SourceRepo + '/zipball/' + $script:SourceSha) $zip
  Expand-Archive -LiteralPath $zip -DestinationPath $out -Force
  Assert-NoReparseTree $out
  $dirs = @(Get-ChildItem -LiteralPath $out -Directory)
  if ($dirs.Count -ne 1 -or $dirs[0].Name -notmatch '27460bd') { throw 'Archive does not have expected immutable commit prefix.' }
  [IO.File]::WriteAllText($pin,$script:SourceSha,([System.Text.UTF8Encoding]::new($false)))
  return $dirs[0].FullName
}
function Create-IsolatedEnvironment {
  $isolated = Join-Path $script:RunRoot 'isolated'
  foreach ($sub in @('home','tmp','nuget','go-cache','go-mod','go-path','xdg','dotnet')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $isolated $sub) | Out-Null
  }
  return $isolated
}
function Invoke-Test([string]$name,[string]$executable,[string]$arguments,[string]$cwd,[int]$seconds) {
  $logDir = Join-Path $script:RunRoot 'logs'
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  $logOut=Join-Path $logDir ($name + '.out.txt')
  $logErr=Join-Path $logDir ($name + '.err.txt')
  $p = New-Object Diagnostics.Process
  $p.StartInfo.FileName = $executable
  $p.StartInfo.Arguments = $arguments
  $p.StartInfo.WorkingDirectory = $cwd
  $p.StartInfo.UseShellExecute = $false
  $p.StartInfo.CreateNoWindow = $true
  $p.StartInfo.RedirectStandardOutput = $true
  $p.StartInfo.RedirectStandardError = $true
  $isolated = Create-IsolatedEnvironment
  $p.StartInfo.EnvironmentVariables['DOTNET_CLI_HOME'] = Join-Path $isolated 'dotnet'
  $p.StartInfo.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
  $p.StartInfo.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
  $p.StartInfo.EnvironmentVariables['NUGET_PACKAGES'] = Join-Path $isolated 'nuget'
  $p.StartInfo.EnvironmentVariables['GOCACHE'] = Join-Path $isolated 'go-cache'
  $p.StartInfo.EnvironmentVariables['GOMODCACHE'] = Join-Path $isolated 'go-mod'
  $p.StartInfo.EnvironmentVariables['GOPATH'] = Join-Path $isolated 'go-path'
  $p.StartInfo.EnvironmentVariables['XDG_CACHE_HOME'] = Join-Path $isolated 'xdg'
  $p.StartInfo.EnvironmentVariables['TMP'] = Join-Path $isolated 'tmp'
  $p.StartInfo.EnvironmentVariables['TEMP'] = Join-Path $isolated 'tmp'
  $p.StartInfo.EnvironmentVariables['USERPROFILE'] = Join-Path $isolated 'home'
  $p.StartInfo.EnvironmentVariables['APPDATA'] = Join-Path $isolated 'home'
  $p.StartInfo.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $isolated 'home'
  $p.StartInfo.EnvironmentVariables['GOTOOLCHAIN'] = 'local'
  $p.StartInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
  foreach ($k in @($p.StartInfo.EnvironmentVariables.Keys)) {
    if ($k -match '^(GH_TOKEN|GITHUB_TOKEN|GIT_ASKPASS|OPENAI_.*|ANTHROPIC_.*|AZURE_.*|AWS_.*|GOOGLE_.*|CLOUDFLARE_.*|MCP_.*|SSH_AUTH_SOCK)$') { $p.StartInfo.EnvironmentVariables.Remove($k) }
  }
  $status='FAIL';$exit=-1
  try {
    [void]$p.Start()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) {
      try { $p.Kill() } catch { }
      [void]$p.WaitForExit(10000)
      $status='TIMEOUT'
    } else {
      $exit = $p.ExitCode
      if ($exit -eq 0) { $status='PASS' }
    }
    $outTask.Wait();$errTask.Wait()
    [IO.File]::WriteAllText($logOut,[string]$outTask.Result,([System.Text.UTF8Encoding]::new($false)))
    [IO.File]::WriteAllText($logErr,[string]$errTask.Result,([System.Text.UTF8Encoding]::new($false)))
  } finally { $p.Dispose() }
  return [ordered]@{name=$name;status=$status;exit=$exit;stdout_sha256=(Hash-File $logOut);stderr_sha256=(Hash-File $logErr)}
}
function Run-Matrix([string]$source,[string]$dn,[string]$go) {
  $results=New-Object System.Collections.ArrayList
  $n=0
  $dotCandidates=@(Get-ChildItem -LiteralPath $source -Filter '*Tests.csproj' -File -Recurse | Where-Object { $_.FullName -notmatch '(?i)(\\|/)(obj|bin)(\\|/)' -and $_.FullName -notmatch '(?i)(UIA|Live|Browser|E2E|WindowsSmoke)' } | Sort-Object FullName | Select-Object -First 6)
  foreach ($csproj in $dotCandidates) {
    $n++
    if (-not $dn) { [void]$results.Add([ordered]@{name=('dotnet-'+$n);status='NOT_RUN';exit=-1;stdout_sha256='';stderr_sha256=''}) ; continue }
    Write-Host ('Testing .NET ' + $csproj.Name)
    [void]$results.Add((Invoke-Test ('dotnet-'+$n) $dn ('test "' + $csproj.FullName + '" --nologo --verbosity quiet') $source 420))
  }
  $goCandidates=@(Get-ChildItem -LiteralPath $source -Filter 'go.mod' -File -Recurse | Where-Object { $_.FullName -notmatch '(?i)(\\|/)(vendor|\.git|bin)(\\|/)' } | Sort-Object FullName | Select-Object -First 8)
  $n=0
  foreach ($mod in $goCandidates) {
    $n++
    if (-not $go) { [void]$results.Add([ordered]@{name=('go-'+$n);status='NOT_RUN';exit=-1;stdout_sha256='';stderr_sha256=''}) ; continue }
    Write-Host ('Testing Go ' + $mod.Directory.Name)
    [void]$results.Add((Invoke-Test ('go-'+$n) $go 'test ./...' $mod.Directory.FullName 420))
  }
  if ($results.Count -eq 0) { [void]$results.Add([ordered]@{name='no-safe-test-projects-discovered';status='NOT_RUN';exit=-1;stdout_sha256='';stderr_sha256=''}) }
  return @($results.ToArray())
}
function Validate-Logs($summary) {
  foreach ($r in @($summary.matrix)) {
    if ($r.status -eq 'NOT_RUN') { continue }
    $o=Join-Path $script:RunRoot ('logs\'+$r.name+'.out.txt')
    $e=Join-Path $script:RunRoot ('logs\'+$r.name+'.err.txt')
    if (-not (Test-Path -LiteralPath $o) -or -not (Test-Path -LiteralPath $e)) { throw 'Missing test logs.' }
    if ((Hash-File $o) -ne $r.stdout_sha256 -or (Hash-File $e) -ne $r.stderr_sha256) { throw 'Test evidence hash drift.' }
  }
}
function ReceiptMarker([string]$suffix) {
  return ('SUPRACHAT-RDTE-' + $suffix + '/' + $script:Contract + ' RUN_LABEL=' + $RunLabel + ' SOURCE_SHA=' + $script:SourceSha + ' DRIVER_SHA256=' + $script:DriverSha)
}
function Post-Receipt([string]$marker,[string]$body) {
  $bodyLines=@((Normalize-ReceiptBody $body) -split "`n")
  if ($bodyLines.Count -lt 3 -or $bodyLines[0] -cne $marker -or $bodyLines[1] -cne '' -or
      $bodyLines[2] -cnotmatch '^Status: (CENSUS_COMPLETE|COMPLETE|INCOMPLETE|STRUCTURAL_ID_FOUND|STRUCTURAL_ID_NOT_FOUND)$') {
    throw 'Refuse to POST a malformed or non-leading result marker.'
  }
  $d=Ensure-Admission
  $existing=Checked-Receipt $d $marker $body
  if ($null -ne $existing) {
    Write-Host ('DLE receipt already verified: ' + $existing.html_url)
    return
  }
  try { $null=GitHub 'POST' ('/repos/'+$script:DleRepo+'/issues/'+$script:DleIssue+'/comments') @{body=$body} }
  catch { Write-Warning 'POST returned uncertain result; performing remote readback before any retry.' }
  $readback=Read-Dle
  $found=Checked-Receipt $readback $marker $body
  if ($null -eq $found) { throw 'DLE receipt not confirmed; preserve local evidence. Reconcile remote before retry.' }
  Write-Host ('Verified DLE receipt: ' + $found.html_url)
}
function Assert-ReceiptFixtures {
  $marker='SUPRACHAT-RDTE-HYGIENE/V7 RUN_LABEL=fixture SOURCE_SHA='+$script:SourceSha+' DRIVER_SHA256='+$script:DriverSha
  $payload=$marker + "`n`nStatus: CENSUS_COMPLETE`nCENSUS_SHA256: " + ('a'*64)
  $kickoff=[pscustomobject]@{id=101;html_url='https://example.invalid/kickoff';body=('## Kickoff' + "`n" + $marker + "`nInstructions only.")}
  $valid=[pscustomobject]@{id=102;html_url='https://example.invalid/receipt';body=$payload}
  $d=@{comments=@($kickoff)}
  if ($null -ne (Existing-Receipt $d $marker)) { throw 'Embedded kickoff marker incorrectly accepted as receipt.' }
  $d=@{comments=@($kickoff,$valid)}
  if ((Checked-Receipt $d $marker $payload).id -ne 102) { throw 'Exact first-line receipt not recovered.' }
  $mismatchRejected=$false
  try { $null=Checked-Receipt $d $marker ($payload+'BAD') } catch { $mismatchRejected=$true }
  if (-not $mismatchRejected) { throw 'Conflicting receipt payload accepted.' }
  $duplicateRejected=$false
  try { $null=Existing-Receipt @{comments=@($valid,$valid)} $marker } catch { $duplicateRejected=$true }
  if (-not $duplicateRejected) { throw 'Duplicate first-line receipts were accepted.' }
  $bad=[pscustomobject]@{id=103;body=($marker+"`nThis is not a result.")}
  $malformedRejected=$false
  try { $null=Existing-Receipt @{comments=@($bad)} $marker } catch { $malformedRejected=$true }
  if (-not $malformedRejected) { throw 'Malformed first-line result was accepted.' }
  $prefix=[pscustomobject]@{id=104;body=($marker+' EXTRA' + "`n`nStatus: CENSUS_COMPLETE")}
  if ($null -ne (Existing-Receipt @{comments=@($prefix)} $marker)) { throw 'Prefix-only marker accepted.' }
}
function Count-Status($matrix,[string]$want) { return @($matrix | Where-Object { $_.status -eq $want }).Count }
function Invoke-SelfTest {
  Assert-LocalSafety
  if($script:DriverSha -cnotmatch '^[0-9a-f]{64}$'){throw 'Driver digest invalid.'}
  $promotion=@('SUPRACHAT-RDTE-PROMOTION/V1','','Status: ADMITTED',
    ('SourceSHA: '+('1'*40)),
    'AssetPath: tools/rdte-package/Invoke-SupraChat-RDTE.ps1',
    ('AssetSHA256: '+('2'*64))) -join [Environment]::NewLine
  if(@($promotion.Split([char]10)).Count -ne 6){throw 'Promotion fixture must have exactly six lines.'}
  $owner=[pscustomobject]@{login='mark-e-deyoung'}
  $valid=[pscustomobject]@{id=[long]42;user=$owner;body=$promotion}
  $embedded=[pscustomobject]@{id=[long]43;user=$owner;body=('Example: '+[Environment]::NewLine+$promotion)}
  if((Find-Promotion @($embedded,$valid)).id -ne 42){throw 'First-line promotion fixture failed.'}
  $revoked=[pscustomobject]@{id=[long]44;user=$owner;body=$promotion.Replace('Status: ADMITTED','Status: REVOKED')}
  if((Find-Promotion @($valid,$revoked)).Status -cne 'REVOKED'){throw 'Promotion revocation fixture failed.'}
  Assert-ReceiptFixtures
  Write-Host ('SELFTEST PASS: synthetic DLE/promotion/receipt invariants; sha256='+$script:DriverSha)
}
function Invoke-LocalSmoke {
  Assert-LocalSafety
  # Unique disposable path; no DLE call, no other host/repository changes.
  $tempRoot=Join-Path $script:Base ('smoke-v7-' + $RunLabel + '-' + [guid]::NewGuid().ToString('N'))
  $prefix=$script:Base.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $tempRoot.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Smoke path escapes disposable base.' }
  if (Test-Path -LiteralPath $tempRoot) { throw 'Smoke root collision.' }
  if (-not (Test-Path -LiteralPath $script:Base)) { New-Item -ItemType Directory -Force -Path $script:Base | Out-Null }
  Assert-LocalSafety
  try {
    New-Item -ItemType Directory -Path $tempRoot -ErrorAction Stop | Out-Null
    $owner=Join-Path $tempRoot '.smoke-owner'
    [IO.File]::WriteAllText($owner,$script:DriverSha,([System.Text.UTF8Encoding]::new($false)))
    $p=Join-Path $tempRoot 'fixture.json'
    Write-Json ([ordered]@{phase='STARTED'}) $p
    if ((Read-Json $p).phase -ne 'STARTED') { throw 'Atomic create/read failed.' }
    Write-Json ([ordered]@{phase='COMPLETE';count=2}) $p
    if ((Read-Json $p).phase -ne 'COMPLETE') { throw 'Atomic replacement/read failed.' }
    if ((Test-Path -LiteralPath ($p+'.pending')) -or (Test-Path -LiteralPath ($p+'.previous'))) { throw 'Atomic JSON residue remains.' }
    if ((Get-FailureType ([pscustomobject]@{phase='STARTED'})) -cne 'STARTED_UNCERTAIN') { throw 'Missing failure_type STARTED fallback failed.' }
    if ((Get-FailureType ([pscustomobject]@{phase='INCOMPLETE'})) -cne 'INCOMPLETE_UNCLASSIFIED') { throw 'Missing failure_type INCOMPLETE fallback failed.' }
    $sample=[pscustomobject]@{pr_count=2; prs=@(
      [pscustomobject]@{number=11;candidate='UNIQUE_SOURCE_PRESERVE'},
      [pscustomobject]@{number=12;candidate='DIVERGENT_SOURCE_REVIEW'})}
    Assert-HygieneClassifications $sample
    $groupNames=@($sample.prs | Group-Object -Property { [string]$_.candidate } | ForEach-Object { $_.Name })
    if ($groupNames.Count -ne 2 -or '' -in $groupNames) { throw 'Named hygiene classification grouping failed.' }
    $bad=[pscustomobject]@{pr_count=1;prs=@([pscustomobject]@{number=13;candidate=''})}
    $rejected=$false
    try { Assert-HygieneClassifications $bad } catch { $rejected=$true }
    if (-not $rejected) { throw 'Empty-classification negative fixture was accepted.' }
    Assert-ReceiptFixtures
    Write-Host 'LOCAL SMOKE PASS: atomic JSON create/replace/read; strict first-line receipt, conflict, duplicate, embedded-kickoff negatives; named hygiene classes.'
  } finally {
    if (Test-Path -LiteralPath $tempRoot) {
      $owner=Join-Path $tempRoot '.smoke-owner'
      if ((Test-Path -LiteralPath $owner -PathType Leaf) -and ((Get-Content -LiteralPath $owner -Raw).Trim() -ceq $script:DriverSha)) {
        Assert-NoReparseTree $tempRoot
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
      } else { Write-Warning 'Unowned smoke directory retained for manual reconciliation; no automatic cleanup.' }
    }
  }
}
function Invoke-Plan {
  $d=Ensure-Admission
  Assert-LocalSafety
  Write-Host 'PLAN (read-only): exact-SHA GitHub archive; portable vendor-SHA-verified SDKs only if absent/opted in; isolated .local test caches; evidence hashes; sanitized private-DLE receipt.'
  Write-Host ('RunLabel=' + $RunLabel + '; existing_run=' + [bool](Test-Path -LiteralPath $script:RunRoot))
  Write-Host ('dotnet_installed=' + [bool](Get-Command dotnet.exe -ErrorAction SilentlyContinue) + '; go_installed=' + [bool](Get-Command go.exe -ErrorAction SilentlyContinue))
  Write-Host ('OptInDotNet=' + [bool]$InstallDisposableDotNet + '; OptInGo=' + [bool]$InstallDisposableGo)
  Write-Host 'Run HygienePlan (read only) and HygieneApply (owned census evidence) in independent workcells; Publish returns both receipts. No automatic merges, PR closures or branch deletion.'
  Write-Host 'NO operations: merge, close, delete Git branches/worktrees, authenticated login, capture credentials, browser launch, Cloudflare tunnel, release or deploy.'
}
function Invoke-Apply {
  $d=Ensure-Admission
  if (-not (Test-Path -LiteralPath $script:StateFile)) { Invoke-LocalSmoke }
  Initialize-Root
  if (Test-Path -LiteralPath $script:StateFile) {
    $state=Read-Json $script:StateFile
    if ($state.phase -eq 'COMPLETE') { Write-Host 'Already complete: no rerun. Use Verify/Publish.'; return }
    throw 'Interrupted or ambiguous previous Apply: refusing blind retry. Preserve first failure; use Publish or a NEW RunLabel.'
  }
  Write-Json ([ordered]@{phase='STARTED'; started_utc=[DateTime]::UtcNow.ToString('o'); source_sha=$script:SourceSha; driver_sha256=$script:DriverSha}) $script:StateFile
  try {
    $source=Get-Source
    $dn=Get-PortableDotNet
    $go=Get-PortableGo
    $matrix=@(Run-Matrix $source $dn $go)
    $summary=[ordered]@{schema='suprachat-rdte-summary/v5'; run_label=$RunLabel; source_sha=$script:SourceSha; driver_sha256=$script:DriverSha; source_archive_sha256=(Hash-File (Join-Path $script:RunRoot 'source-immutable.zip')); dotnet_executable_sha256=$(if($dn){Hash-File $dn}else{''}); go_executable_sha256=$(if($go){Hash-File $go}else{''}); matrix=$matrix; finalized_utc=[DateTime]::UtcNow.ToString('o')}
    Write-Json $summary $script:SummaryFile
    Write-Json ([ordered]@{phase='COMPLETE';summary_sha256=(Hash-File $script:SummaryFile);finished_utc=[DateTime]::UtcNow.ToString('o')}) $script:StateFile
    Write-Host ('Apply finished, matrix: PASS='+(Count-Status $matrix 'PASS')+' FAIL='+(Count-Status $matrix 'FAIL')+' TIMEOUT='+(Count-Status $matrix 'TIMEOUT')+' NOT_RUN='+(Count-Status $matrix 'NOT_RUN'))
  } catch {
    $detail=[string]$_.Exception.Message
    foreach ($secret in @($env:GH_TOKEN,$env:GITHUB_TOKEN)) { if ($secret) { $detail=$detail.Replace([string]$secret,'[REDACTED]') } }
    if ($detail.Length -gt 1000) { $detail=$detail.Substring(0,1000) }
    Write-Json ([ordered]@{phase='INCOMPLETE'; failure_type=$_.Exception.GetType().Name; failure_detail_local_only=$detail; failed_utc=[DateTime]::UtcNow.ToString('o')}) $script:StateFile
    throw
  }
}
function Check-Local {
  Assert-Owner
  $state=Read-Json $script:StateFile
  if ($state.phase -ne 'COMPLETE') { return @{complete=$false;state=$state} }
  $sum=Read-Json $script:SummaryFile
  if ($state.summary_sha256 -ne (Hash-File $script:SummaryFile) -or $sum.source_sha -ne $script:SourceSha -or $sum.driver_sha256 -ne $script:DriverSha -or $sum.run_label -ne $RunLabel) { throw 'Summary verification failed.' }
  if ($sum.source_archive_sha256 -ne (Hash-File (Join-Path $script:RunRoot 'source-immutable.zip'))) { throw 'Source archive digest drift.' }
  Validate-Logs $sum
  return @{complete=$true;state=$state;summary=$sum;matrix_sha256=(Hash-File $script:SummaryFile)}
}
function Get-FailureType($state) {
  # STARTED/legacy/partially-written records intentionally lack failure_type.
  # Never allow error publication to throw from an optional metadata field.
  if ($null -eq $state) { return 'UNKNOWN_STATE' }
  $property = $state.PSObject.Properties['failure_type']
  if ($null -ne $property -and $null -ne $property.Value) {
    $value=[string]$property.Value
    if ($value -match '^[A-Za-z][A-Za-z0-9_]{0,63}$') { return $value }
  }
  if ([string]$state.phase -eq 'STARTED') { return 'STARTED_UNCERTAIN' }
  if ([string]$state.phase -eq 'INCOMPLETE') { return 'INCOMPLETE_UNCLASSIFIED' }
  return 'UNKNOWN_STATE'
}
function Build-ResultReceiptBody($check,[string]$marker) {
  if ($check.complete) {
    $m=@($check.summary.matrix)
    $failedNames=@($m | Where-Object { $_.status -eq 'FAIL' -or $_.status -eq 'TIMEOUT' } | ForEach-Object { $_.name }) -join ','
    if (-not $failedNames) { $failedNames='none' }
    return (@($marker,'','Status: COMPLETE','PASS: '+(Count-Status $m 'PASS'),'FAIL: '+(Count-Status $m 'FAIL'),'TIMEOUT: '+(Count-Status $m 'TIMEOUT'),'NOT_RUN: '+(Count-Status $m 'NOT_RUN'),'FAILED_LABELS: '+$failedNames,'SUMMARY_SHA256: '+$check.matrix_sha256,'SOURCE_ARCHIVE_SHA256: '+$check.summary.source_archive_sha256,'','Scope: immutable public-source, deterministic model-free tests only; no live account, ChatGPT MCP registration, credential, UIA or deployment acceptance.','Next baton: reconcile current DLE and exact public PR heads; classify failed test labels using local owned logs; preserve #152 first failure, #169/#153 merge refusals and crash-journal PR-create refusal. Account HIL and release are held.') -join "`n")
  }
  return (@($marker,'','Status: INCOMPLETE','FAILURE_TYPE: '+(Get-FailureType $check.state),'','First failure retained locally; do not treat this as a test PASS. Next baton: inspect owned local run without rerunning uncertain effects; use a fresh label for a new bounded rep.') -join "`n")
}
function Publish-Result {
  $check=Check-Local
  $marker=ReceiptMarker 'RESULT'
  Post-Receipt $marker (Build-ResultReceiptBody $check $marker)
}
function Invoke-Verify {
  $null=Ensure-Admission
  $any=$false
  if (Test-Path -LiteralPath $script:StateFile) {
    $any=$true
    $check=Check-Local
    if (-not $check.complete) { Write-Host 'VERIFY test: INCOMPLETE; publish incomplete receipt before undo.' }
    else { Write-Host ('VERIFY TEST EVIDENCE integrity PASS, summary_sha256='+$check.matrix_sha256) }
    $d=Read-Dle
    $marker=ReceiptMarker 'RESULT'
    $receipt=Checked-Receipt $d $marker (Build-ResultReceiptBody $check $marker)
    if ($null -eq $receipt) { Write-Host 'Test DLE return not published; run -Mode Publish.' }
    else { Write-Host ('VERIFY TEST DLE return confirmed: '+$receipt.html_url) }
  }
  if (Test-Path -LiteralPath $script:HygieneStateFile) {
    $any=$true
    $h=Check-Hygiene
    if (-not $h.complete) { Write-Host 'VERIFY hygiene: INCOMPLETE; preserve first failure.' }
    else { Write-Host ('VERIFY HYGIENE EVIDENCE integrity PASS, census_sha256='+$h.sha) }
    $d=Read-Dle
    $marker=ReceiptMarker 'HYGIENE'
    $receipt=Checked-Receipt $d $marker (Build-HygieneReceiptBody $h $marker)
    if ($null -eq $receipt) { Write-Host 'Hygiene DLE return not published; run -Mode Publish.' }
    else { Write-Host ('VERIFY HYGIENE DLE return confirmed: '+$receipt.html_url) }
  }
  if (-not $any) { throw 'No owned execution state. Plan/Apply did not succeed; nothing to verify.' }
}
function Invoke-Undo {
  if (-not $ConfirmUndo) { throw 'Undo requires explicit -ConfirmUndo.' }
  Assert-LocalSafety
  if (-not (Test-Path -LiteralPath $script:RunRoot)) { Write-Host 'UNDO NOOP: owned run tree is already absent.'; return }
  $d=Ensure-Admission
  Assert-Owner
  $any=$false
  if (Test-Path -LiteralPath $script:StateFile) {
    $any=$true
    $check=Check-Local
    $marker=ReceiptMarker 'RESULT'
    if ($null -eq (Checked-Receipt $d $marker (Build-ResultReceiptBody $check $marker))) { throw 'Undo requires exact matching outbound test DLE return.' }
  }
  if (Test-Path -LiteralPath $script:HygieneStateFile) {
    $any=$true
    $check=Check-Hygiene
    $marker=ReceiptMarker 'HYGIENE'
    if ($null -eq (Checked-Receipt $d $marker (Build-HygieneReceiptBody $check $marker))) { throw 'Undo requires exact matching outbound hygiene DLE return.' }
  }
  if (-not $any) { throw 'Unfinished owned run has no state; require manual recovery before undo.' }
  Assert-NoReparseTree $script:RunRoot
  Remove-Item -LiteralPath $script:RunRoot -Force -Recurse
  Write-Host 'UNDO PASS: removed only owner-marked disposable run tree; script and existing user environment untouched.'
}
function Get-PageItems([string]$path,[int]$maxPages=10) {
  $result=New-Object System.Collections.ArrayList
  for ($p=1;$p -le $maxPages;$p++) {
    $sep=$(if($path.Contains('?')){'&'}else{'?'})
    $items=@(GitHub 'GET' ($path+$sep+'per_page=100&page='+$p))
    if ($items.Count -eq 1 -and $null -ne $items[0] -and $items[0] -is [System.Array]) { $items=@($items[0]) }
    foreach($x in $items) { if($null -ne $x) { [void]$result.Add($x) } }
    if ($items.Count -lt 100) { return @($result.ToArray()) }
  }
  throw 'GitHub read exceeds hygiene bounded pagination; no partial cleanup proposal.'
}
function Get-HygieneCensus([switch]$AlreadyAdmitted) {
  # Public repo only. Private DLE is exclusively for protected receipt/authority.
  if (-not $AlreadyAdmitted) { $null=Ensure-Admission }
  $repo=$script:SourceRepo
  $root='/repos/'+$repo
  $metadata=GitHub 'GET' $root
  if ($metadata.private -or $metadata.default_branch -ne 'main') { throw 'Expected public main branch not verified.' }
  $branch=GitHub 'GET' ($root+'/branches/main')
  $head=[string]$branch.commit.sha
  if ($head -notmatch '^[0-9a-f]{40}$') { throw 'Invalid observed main SHA.' }
  $commit=GitHub 'GET' ($root+'/git/commits/'+$head)
  $treeSha=[string]$commit.tree.sha
  if ($treeSha -notmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve exact main tree SHA.' }
  $tree=GitHub 'GET' ($root+'/git/trees/'+$treeSha+'?recursive=1')
  if ($tree.truncated) { throw 'Main git tree truncated; refuse incomplete source census.' }
  $fileSha=[System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
  foreach ($t in @($tree.tree)) { if ($t.type -eq 'blob') { $fileSha[[string]$t.path]=[string]$t.sha } }
  $prs=@(Get-PageItems ($root+'/pulls?state=open') 10)
  $results=New-Object System.Collections.ArrayList
  foreach($pr in $prs) {
    $number=[int]$pr.number
    $prDetail=GitHub 'GET' ($root+'/pulls/'+$number)
    if ([int]$prDetail.number -ne $number -or [string]$prDetail.head.sha -cne [string]$pr.head.sha) { throw ('PR #'+$number+' changed during hygiene scan; use a new label.') }
    $candidate='UNIQUE_SOURCE_PRESERVE';$same=0;$divergent=0;$unique=0;$deleted=0;$renamed=0
    $files=@(Get-PageItems ($root+'/pulls/'+$number+'/files') 4)
    if ($files.Count -eq 0 -or $files.Count -gt 300 -or [int]$prDetail.changed_files -ne $files.Count) {
      $candidate='CENSUS_INCOMPLETE_HOLD'
    } else {
      foreach($file in $files) {
        $path=[string]$file.filename
        if ($file.status -eq 'renamed' -or $file.status -eq 'removed') { $renamed++; continue }
        if (-not $fileSha.ContainsKey($path)) { $unique++; continue }
        if ([string]$fileSha[$path] -ceq [string]$file.sha) { $same++ }
        else { $divergent++ }
      }
      if ($renamed -eq 0 -and $unique -eq 0 -and $divergent -eq 0 -and $same -eq $files.Count) { $candidate='EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW' }
      elseif ($renamed -gt 0) { $candidate='DELETE_OR_RENAME_MANUAL_REVIEW' }
      elseif ($divergent -gt 0) { $candidate='DIVERGENT_SOURCE_REVIEW' }
    }
    if ([string]$pr.base.ref -ne 'main' -or [string]$prDetail.base.ref -ne 'main') { $candidate='STACKED_BASE_PRESERVE' }
    if ($number -in @(153,169)) { $candidate='SCOPED_MERGE_REFUSAL_HOLD' }
    [void]$results.Add([pscustomobject][ordered]@{
      number=$number; head_sha=[string]$pr.head.sha; base_branch=[string]$pr.base.ref;
      candidate=$candidate; draft=[bool]$pr.draft; changed_files=[int]$prDetail.changed_files;
      exact_blob_count=$same; divergent_count=$divergent; missing_on_main_count=$unique;
      structural_renames_or_deletes=$renamed
    })
  }
  $after=GitHub 'GET' ($root+'/branches/main')
  if ([string]$after.commit.sha -cne $head) { throw 'Main advanced during inventory; refuse mixed-revision hygiene result. Re-plan.' }
  $currentOpen=@(Get-PageItems ($root+'/pulls?state=open') 10)
  if ($currentOpen.Count -ne $prs.Count) { throw 'Open PR count changed during scan; do not publish stale hygiene evidence.' }
  foreach($i in $currentOpen) {
    $old=@($results.ToArray() | Where-Object { $_.number -eq [int]$i.number })
    if ($old.Count -ne 1 -or $old[0].head_sha -cne [string]$i.head.sha) { throw 'Open PR head or membership changed during scan; do not publish stale evidence.' }
  }
  return [pscustomobject][ordered]@{
    schema='suprachat-hygiene-readonly/v7'; repo=$repo; main_sha=$head; main_tree_sha=$treeSha;
    source_sha=$script:SourceSha; driver_sha256=$script:DriverSha; run_label=$RunLabel;
    pr_count=$results.Count; classification='EVIDENCE_ONLY';
    mutations='NONE'; protected_prs='153,169';
    scan_utc=[DateTime]::UtcNow.ToString('o'); prs=@($results.ToArray());
    baton='Reconcile this exact-source census with current DLE/leases and independent CI/acceptance. Review exact blobs for possible PR supersession; no automatic close, merge, branch deletion, release or refused-action bypass.'
  }
}
function Assert-HygieneClassifications($report) {
  $allowed=@('EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW','DIVERGENT_SOURCE_REVIEW',
    'UNIQUE_SOURCE_PRESERVE','DELETE_OR_RENAME_MANUAL_REVIEW',
    'STACKED_BASE_PRESERVE','CENSUS_INCOMPLETE_HOLD','SCOPED_MERGE_REFUSAL_HOLD')
  $seen=New-Object 'System.Collections.Generic.HashSet[int]'
  $count=0
  foreach ($pr in @($report.prs)) {
    $count++
    if ($null -eq $pr) { throw 'Hygiene candidate record is null.' }
    if (-not ($pr.PSObject.Properties.Name -contains 'candidate')) { throw 'Hygiene candidate field is missing; reject unnamed census.' }
    $candidate=[string]$pr.candidate
    if ($candidate -notin $allowed) { throw 'Unknown or empty hygiene classification; reject census.' }
    if (-not $seen.Add([int]$pr.number)) { throw 'Duplicate PR identifier in hygiene census.' }
  }
  if ($count -ne [int]$report.pr_count) { throw 'Hygiene census count mismatch.' }
}
function Show-HygienePlan($report) {
  Assert-HygieneClassifications $report
  Write-Host ('HYGIENE PLAN read-only: '+$report.repo+' main='+$report.main_sha+' open_prs='+$report.pr_count)
  $groups=@($report.prs | Group-Object -Property { [string]$_.candidate } | Sort-Object Name)
  foreach($g in $groups) { Write-Host ($g.Name + ': '+$g.Count) }
  foreach($r in @($report.prs | Where-Object {$_.candidate -eq 'EXACT_BLOBS_MANUAL_SUPERSESSION_REVIEW'} | Select-Object -First 12)) {
    Write-Host ('REVIEW_ONLY PR #'+$r.number+' head='+$r.head_sha)
  }
  Write-Host 'No repository mutation is authorized: open PRs and branches remain intact. Only an owner-admitted actor may perform merge/close after exact independent evidence.'
}
function Invoke-HygienePlan {
  $report=Get-HygieneCensus
  Show-HygienePlan $report
}
function Invoke-HygieneApply {
  Assert-LocalSafety
  # A same-label hygiene rerun must be a true NOOP, not a new snapshot with different heads.
  if (Test-Path -LiteralPath $script:HygieneStateFile) {
    $st=Read-Json $script:HygieneStateFile
    if ($st.phase -eq 'COMPLETE') { $null=Ensure-Admission; $null=Check-Hygiene; Write-Host 'HYGIENE APPLY NOOP: immutable local census already complete. Verify/Publish.';return }
    throw 'Ambiguous hygiene execution state; no blind retry. Publish incomplete receipt or choose new RunLabel.'
  }
  # Inbound owner authority must be verified BEFORE any temporary local mutation.
  $null=Ensure-Admission
  # Qualify atomic IO before costly remote census or creating durable run state.
  Invoke-LocalSmoke
  # Remote census remains source-read-only; only the owned local evidence is written.
  $report=Get-HygieneCensus -AlreadyAdmitted
  Assert-HygieneClassifications $report
  Initialize-Root
  Write-Json ([ordered]@{phase='STARTED';source_sha=$script:SourceSha;driver_sha256=$script:DriverSha}) $script:HygieneStateFile
  try {
    Write-Json $report $script:HygieneFile
    $sha=Hash-File $script:HygieneFile
    Write-Json ([ordered]@{phase='COMPLETE'; census_sha256=$sha;main_sha=$report.main_sha}) $script:HygieneStateFile
    Show-HygienePlan $report
    Write-Host ('Owned hygiene evidence SHA256: '+$sha)
  } catch { Write-Json ([ordered]@{phase='INCOMPLETE';failure_type=$_.Exception.GetType().Name}) $script:HygieneStateFile;throw }
}
function Check-Hygiene {
  Assert-Owner
  $st=Read-Json $script:HygieneStateFile
  if ($st.phase -ne 'COMPLETE') { return @{complete=$false;state=$st} }
  $report=Read-Json $script:HygieneFile
  $hash=Hash-File $script:HygieneFile
  Assert-HygieneClassifications $report
  if ($hash -cne $st.census_sha256 -or $report.source_sha -cne $script:SourceSha -or $report.driver_sha256 -cne $script:DriverSha -or $report.run_label -cne $RunLabel -or $report.mutations -ne 'NONE') { throw 'Hygiene evidence identity or hash drift.' }
  return @{complete=$true;state=$st;report=$report;sha=$hash}
}
function Build-HygieneReceiptBody($h,[string]$marker) {
  if ($h.complete) {
    $groups=@($h.report.prs | Group-Object -Property { [string]$_.candidate } | Sort-Object Name)
    $lines=New-Object System.Collections.ArrayList
    foreach($g in $groups) { [void]$lines.Add(($g.Name+': '+$g.Count)) }
    $body=@($marker,'','Status: CENSUS_COMPLETE','MAIN_SHA: '+$h.report.main_sha,'OPEN_PR_COUNT: '+$h.report.pr_count,'CENSUS_SHA256: '+$h.sha,'','Classification counts:') + @($lines.ToArray()) + @('','Actions: NONE; no branch, PR, release, source or CI changes. Refusals #153/#169 preserved.','Next baton: source-qualify candidate PRs; independently verify CI, owner lease, dependency/stack ordering and protected holds; explicitly authorize any merge or closure in owning DLE before executing it.')
    return ($body -join "`n")
  }
  return (@($marker,'','Status: INCOMPLETE','FAILURE_TYPE: '+(Get-FailureType $h.state),'','No repository changes; preserve local first failure and start a new label after reconciliation.') -join "`n")
}
function Publish-Hygiene {
  $h=Check-Hygiene
  $marker=ReceiptMarker 'HYGIENE'
  Post-Receipt $marker (Build-HygieneReceiptBody $h $marker)
}

if ($MyInvocation.InvocationName -eq '.') { return }
if ($Mode -notin @('SelfTest','LocalSmoke')) { Initialize-BootstrapAuthority }
try {
  switch ($Mode) {
    'SelfTest' { Invoke-SelfTest }
    'LocalSmoke' { Invoke-LocalSmoke }
    'Plan' { Invoke-Plan }
    'Apply' { Invoke-Apply }
    'HygienePlan' { Invoke-HygienePlan }
    'HygieneApply' { Invoke-HygieneApply }
    'Verify' { Invoke-Verify }
    'Publish' {
      $published=$false
      if (Test-Path -LiteralPath $script:StateFile) { Publish-Result;$published=$true }
      if (Test-Path -LiteralPath $script:HygieneStateFile) { Publish-Hygiene;$published=$true }
      if (-not $published) { throw 'No owned execution state: Apply and HygieneApply did not run. Cannot publish.' }
    }
    'Undo' { Invoke-Undo }
  }
} catch {
  $message = [string]$_.Exception.Message
  foreach($secret in @($env:GH_TOKEN,$env:GITHUB_TOKEN)) { if($secret) { $message=$message.Replace([string]$secret,'[REDACTED]') } }
  Write-Error ('RDTE '+$Mode+' failed (no automatic retry): '+$message)
  exit 1
}