#requires -Version 5.1
[CmdletBinding()]
param(
  [ValidateSet('SelfTest','Plan','Apply','Verify','Run','Undo')][string]$Mode='Plan',
  [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunLabel='local-rdte-01',
  [ValidateSet('Plan','HygienePlan','HygieneApply','Verify','Publish','Apply','SelfTest')][string]$TargetMode='Plan',
  [switch]$ConfirmUndo
)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
$script:Marker='SUPRACHAT-RDTE-PROMOTION/V1'
$script:ApprovedAuthor='mark-e-deyoung'
$script:AssetPath='tools/rdte-package/Invoke-SupraChat-RDTE.ps1'

function Normalize-Text([string]$Value) {
  return $Value.Replace(([string][char]13 + [char]10),[string][char]10)
}
function Select-Promotion {
  param([Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Comments)
  $candidates=New-Object System.Collections.ArrayList
  foreach($c in $Comments) {
    if($null -eq $c -or $null -eq $c.body -or $null -eq $c.user) { continue }
    if([string]$c.user.login -cne $script:ApprovedAuthor) { continue }
    $lines=@((Normalize-Text ([string]$c.body)).Split([char]10))
    if($lines[0] -cne $script:Marker) { continue }
    if($lines.Count -lt 7 -or $lines[1] -cne '') { throw 'Malformed promotion envelope.' }
    $map=@{}
    for($i=2;$i -lt $lines.Count;$i++) {
      $line=$lines[$i]
      if([string]::IsNullOrWhiteSpace($line)) { continue }
      $split=$line.IndexOf(': ')
      if($split -le 0) { throw 'Malformed promotion field.' }
      $key=$line.Substring(0,$split)
      $value=$line.Substring($split+2)
      if(@('Status','SourceSHA','AssetPath','AssetSHA256') -cnotcontains $key -or $map.ContainsKey($key)) {
        throw 'Unknown or duplicate promotion key.'
      }
      $map[$key]=$value
    }
    if($map.Count -ne 4) { throw 'Incomplete promotion.' }
    if($map.Status -cnotin @('ADMITTED','REVOKED') -or
       $map.SourceSHA -cnotmatch '^[0-9a-f]{40}$' -or
       $map.AssetSHA256 -cnotmatch '^[0-9a-f]{64}$' -or
       $map.AssetPath -cne $script:AssetPath) { throw 'Invalid promotion policy.' }
    if([long]$c.id -le 0) { throw 'Invalid promotion ID.' }
    [void]$candidates.Add([pscustomobject]@{
      id=[long]$c.id;Status=$map.Status;SourceSHA=$map.SourceSHA;
      AssetSHA256=$map.AssetSHA256;AssetPath=$map.AssetPath
    })
  }
  if($candidates.Count -eq 0) { throw 'No owner-authored promotion admission.' }
  $sorted=@($candidates | Sort-Object -Property id)
  for($i=1;$i -lt $sorted.Count;$i++) {
    if($sorted[$i].id -eq $sorted[$i-1].id) { throw 'Duplicate promotion IDs.' }
  }
  $latest=$sorted[-1]
  if($latest.Status -cne 'ADMITTED') { throw 'Latest promotion revoked.' }
  return $latest
}
function Read-DleComments {
  $token=$env:GH_TOKEN
  if([string]::IsNullOrWhiteSpace($token)) { $token=$env:GITHUB_TOKEN }
  $gh=$null
  if([string]::IsNullOrWhiteSpace($token)) {
    $gh=Get-Command gh -ErrorAction SilentlyContinue
    if($null -eq $gh) { throw 'Existing authenticated gh or process-local token required.' }
  }
  $all=New-Object System.Collections.ArrayList
  $ended=$false
  for($page=1;$page -le 10;$page++) {
    $route='repos/SemperSupra/suprachat-private/issues/2/comments?per_page=100&page='+$page
    if(-not [string]::IsNullOrWhiteSpace($token)) {
      $h=@{Authorization=('Bearer '+$token);Accept='application/vnd.github+json'}
      $items=@(Invoke-RestMethod -Uri ('https://api.github.com/'+$route) -Headers $h -Method Get -TimeoutSec 30)
    } else {
      $json=& $gh.Source api --method GET $route
      if($LASTEXITCODE -ne 0) { throw 'Authenticated DLE read failed.' }
      $items=@(($json -join [Environment]::NewLine | ConvertFrom-Json))
    }
    foreach($item in $items) { [void]$all.Add($item) }
    if($items.Count -lt 100) { $ended=$true;break }
  }
  if(-not $ended) { throw 'Private DLE exceeds bounded comment window.' }
  return ,$all.ToArray()
}
function Assert-NoReparse([string]$Path) {
  if(-not (Test-Path -LiteralPath $Path)) { return }
  $item=Get-Item -LiteralPath $Path -Force
  if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Unowned reparse point detected.'
  }
  if($item.PSIsContainer) {
    foreach($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
      Assert-NoReparse $child.FullName
    }
  }
}
function Owned-Path([string]$Base,[string]$Label) {
  if($Label -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $Label -in @('.','..')) {
    throw 'Invalid run label.'
  }
  $root=[IO.Path]::GetFullPath($Base).TrimEnd('\','/')
  $path=[IO.Path]::GetFullPath((Join-Path $root $Label))
  if(-not $path.StartsWith(($root+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run path escapes owned root.'
  }
  return $path
}
function Asset-Url($Admission) {
  if($Admission.SourceSHA -cnotmatch '^[0-9a-f]{40}$' -or
    $Admission.AssetPath -cne $script:AssetPath) { throw 'Invalid artifact locator.' }
  return ('https://raw.githubusercontent.com/SemperSupra/suprachat/'+$Admission.SourceSHA+'/'+$script:AssetPath)
}
function Write-State([string]$Path,$Value) {
  if(Test-Path -LiteralPath $Path) { throw 'Immutable state exists; no overwrite.' }
  $json=ConvertTo-Json -InputObject $Value -Depth 8 -Compress
  [IO.File]::WriteAllText($Path,$json,(New-Object Text.UTF8Encoding($false)))
}
function Read-State([string]$Path) {
  if(-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Owned state missing.' }
  return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}
function Check-Identity($Owner,$Admission,[string]$Label) {
  if([string]$Owner.contract -cne 'suprachat-asset/v1' -or
     [string]$Owner.label -cne $Label -or
     [string]$Owner.source_sha -cne [string]$Admission.SourceSHA -or
     [string]$Owner.asset_sha256 -cne [string]$Admission.AssetSHA256 -or
     [string]$Owner.asset_path -cne $script:AssetPath) {
    throw 'Owner state does not match current DLE admission.'
  }
}
function Verify-Installed($Admission,[string]$Base,[string]$Label) {
  $dir=Owned-Path $Base $Label
  Assert-NoReparse $dir
  $owner=Read-State (Join-Path $dir 'owner.json')
  Check-Identity $owner $Admission $Label
  $ready=Read-State (Join-Path $dir 'ready.json')
  if([string]$ready.status -cne 'READY' -or
     [string]$ready.asset_sha256 -cne [string]$Admission.AssetSHA256) {
    throw 'Asset not in READY state.'
  }
  $file=Join-Path $dir 'Invoke-SupraChat-RDTE.ps1'
  if(-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Asset missing.' }
  $hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
  if($hash -cne [string]$Admission.AssetSHA256) { throw 'Installed asset hash drift.' }
  return $file
}
function Install-Artifact {
  param($Admission,[string]$Base,[string]$Label,[scriptblock]$Downloader)
  $target=Owned-Path $Base $Label
  $url=Asset-Url $Admission
  if(Test-Path -LiteralPath $target) {
    $old=Verify-Installed $Admission $Base $Label
    return [pscustomobject]@{Status='NOOP_VERIFIED';Asset=$old}
  }
  if(-not (Test-Path -LiteralPath $Base)) {
    New-Item -ItemType Directory -Path $Base -Force | Out-Null
  }
  Assert-NoReparse $Base
  New-Item -ItemType Directory -Path $target -ErrorAction Stop | Out-Null
  Write-State (Join-Path $target 'owner.json') ([pscustomobject]@{
    contract='suprachat-asset/v1';label=$Label;source_sha=$Admission.SourceSHA;
    asset_path=$script:AssetPath;asset_sha256=$Admission.AssetSHA256
  })
  $partial=Join-Path $target 'artifact.pending'
  & $Downloader $url $partial
  if(-not (Test-Path -LiteralPath $partial -PathType Leaf)) { throw 'Download missing.' }
  Assert-NoReparse $partial
  if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -cne
     [string]$Admission.AssetSHA256) { throw 'Downloaded hash mismatch; preserve incomplete run.' }
  $file=Join-Path $target 'Invoke-SupraChat-RDTE.ps1'
  Move-Item -LiteralPath $partial -Destination $file -ErrorAction Stop
  Write-State (Join-Path $target 'ready.json') ([pscustomobject]@{
    status='READY';asset_sha256=$Admission.AssetSHA256
  })
  $verified=Verify-Installed $Admission $Base $Label
  return [pscustomobject]@{Status='READY_VERIFIED';Asset=$verified}
}
function Undo-Artifact($Admission,[string]$Base,[string]$Label,[bool]$Confirmed) {
  if(-not $Confirmed) { throw 'Undo confirmation required.' }
  $dir=Owned-Path $Base $Label
  if(-not (Test-Path -LiteralPath $dir)) { return 'NOOP_ABSENT' }
  Assert-NoReparse $dir
  Check-Identity (Read-State (Join-Path $dir 'owner.json')) $Admission $Label
  foreach($entry in @(Get-ChildItem -LiteralPath $dir -Force)) {
    if($entry.Name -cnotin @('owner.json','ready.json','artifact.pending','Invoke-SupraChat-RDTE.ps1')) {
      throw 'Unexpected run content; refuse Undo.'
    }
  }
  Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop
  return 'UNDO_COMPLETE'
}
if($MyInvocation.InvocationName -eq '.') { return }
try {
  if($Mode -eq 'SelfTest') {
    $rejected=$false
    try { Select-Promotion -Comments @() | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Empty DLE should fail.' }
    Write-Output 'SELFTEST PASS: empty DLE fails closed.'
    exit 0
  }
  $admission=Select-Promotion -Comments @(Read-DleComments)
  $base=Join-Path $HOME '.local\share\suprachat-rdte\packages'
  if($Mode -eq 'Plan') {
    Write-Output ('PLAN source='+$admission.SourceSHA+' sha256='+$admission.AssetSHA256)
    Write-Output ('run='+$RunLabel+' existing='+(Test-Path -LiteralPath (Owned-Path $base $RunLabel)))
    Write-Output 'NO automatic update, global install, login, user Git mutation, merge or release.'
  } elseif($Mode -eq 'Apply') {
    $download={ param($Url,$Path) Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing -TimeoutSec 120 }
    $result=Install-Artifact $admission $base $RunLabel $download
    Write-Output ('APPLY '+$result.Status+': artifact verified, not executed.')
  } elseif($Mode -eq 'Verify') {
    $file=Verify-Installed $admission $base $RunLabel
    Write-Output ('VERIFY PASS: '+$file)
  } elseif($Mode -eq 'Run') {
    $file=Verify-Installed $admission $base $RunLabel
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $file -Mode $TargetMode -RunLabel $RunLabel
    if($LASTEXITCODE -ne 0) { throw 'Child exit code nonzero.' }
    Write-Output 'Child exited zero; DLE semantic acceptance separate.'
  } elseif($Mode -eq 'Undo') {
    Write-Output (Undo-Artifact $admission $base $RunLabel ([bool]$ConfirmUndo))
  }
} catch {
  Write-Error ('RDTE bootstrap '+$Mode+' failed: '+$_.Exception.Message)
  exit 1
}
