#requires -Version 5.1
[CmdletBinding()]
param(
  [ValidateSet('SelfTest','Configure','Plan','Apply','Verify','Run','Undo','Unconfigure')][string]$Mode='Plan',
  [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunLabel='local-rdte-01',
  [ValidateSet('Plan','HygienePlan','HygieneApply','Verify','Publish','Apply','SelfTest')][string]$TargetMode='Plan',
  [switch]$ConfirmUndo,
  [string]$AuthorityRef=''
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
  param([Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Comments,[switch]$AllowRevoked)
  $candidates=New-Object System.Collections.ArrayList
  foreach($c in $Comments) {
    if($null -eq $c -or $null -eq $c.body -or $null -eq $c.user) { continue }
    if([string]$c.user.login -cne $script:ApprovedAuthor) { continue }
    $lines=@((Normalize-Text ([string]$c.body)).Split([char]10))
    if($lines[0] -cne $script:Marker) { continue }
    if($lines.Count -lt 6 -or $lines[1] -cne '') { throw 'Malformed promotion envelope.' }
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
  if($latest.Status -cne 'ADMITTED' -and -not $AllowRevoked) { throw 'Latest promotion revoked.' }
  return $latest
}
function Collect-DlePages {
  param([Parameter(Mandatory=$true)][scriptblock]$ReadPage)
  $all=New-Object System.Collections.ArrayList
  $ended=$false
  for($page=1;$page -le 10;$page++) {
    $items=@(& $ReadPage $page)
    foreach($item in $items) {
      if($null -eq $item -or $null -eq $item.id) { throw 'Malformed DLE comment in page.' }
      [void]$all.Add($item)
    }
    if($items.Count -lt 100) { $ended=$true;break }
  }
  if(-not $ended) { throw 'Private DLE exceeds bounded comment window.' }
  return $all.ToArray()
}
function Select-UndoPromotion {
  param([Parameter(Mandatory=$true)][AllowEmptyCollection()][object[]]$Comments,[object]$Owner)
  $admitted=New-Object System.Collections.ArrayList
  foreach($comment in $Comments) {
    if($null -eq $comment -or $null -eq $comment.body -or $null -eq $comment.user) { continue }
    if([string]$comment.user.login -cne $script:ApprovedAuthor) { continue }
    $lines=@((Normalize-Text ([string]$comment.body)).Split([char]10))
    if($lines[0] -cne $script:Marker) { continue }
    $p=Select-Promotion -Comments @($comment) -AllowRevoked
    if($p.Status -cne 'ADMITTED') { continue }
    if([string]$p.SourceSHA -ceq [string]$Owner.source_sha -and
       [string]$p.AssetSHA256 -ceq [string]$Owner.asset_sha256) {
      [void]$admitted.Add($p)
    }
  }
  if($admitted.Count -eq 0) { throw 'No owner-authored historical admission for safe Undo.' }
  return @($admitted | Sort-Object -Property id)[-1]
}
function Read-DleComments {
  param([object]$Authority)
  $token=$env:GH_TOKEN
  if([string]::IsNullOrWhiteSpace($token)) { $token=$env:GITHUB_TOKEN }
  $gh=$null
  if([string]::IsNullOrWhiteSpace($token)) {
    $gh=Get-Command gh -ErrorAction SilentlyContinue
    if($null -eq $gh) { throw 'Existing authenticated gh or process-local token required.' }
  }
  $fetch={
    param($page)
    $route='repos/'+$Authority.repository+'/issues/'+$Authority.issue+'/comments?per_page=100&page='+$page
    if(-not [string]::IsNullOrWhiteSpace($token)) {
      $h=@{Authorization=('Bearer '+$token);Accept='application/vnd.github+json'}
      $items=@(Invoke-RestMethod -Uri ('https://api.github.com/'+$route) -Headers $h -Method Get -TimeoutSec 30)
    } else {
      $json=& $gh.Source api --method GET $route
      if($LASTEXITCODE -ne 0) { throw 'Authenticated DLE read failed.' }
      $items=@(($json -join [Environment]::NewLine | ConvertFrom-Json))
    }
    foreach($item in $items) { Write-Output $item }
  }.GetNewClosure()
  return (Collect-DlePages -ReadPage $fetch)
}
function Parse-Authority([string]$Reference) {
  $pattern='^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)#([1-9][0-9]{0,8})$'
  $parsed=[regex]::Match($Reference,$pattern)
  if(-not $parsed.Success -or $parsed.Groups[1].Value.Contains('..') -or $parsed.Groups[2].Value.Contains('..') -or $parsed.Groups[1].Value -in @('.','..') -or $parsed.Groups[2].Value -in @('.','..')) { throw 'Invalid owner DLE issue reference.' }
  return [pscustomobject]@{
    contract='suprachat-dle-authority/v1'
    repository=($parsed.Groups[1].Value+'/'+$parsed.Groups[2].Value)
    issue=[int]$parsed.Groups[3].Value
    owner=$script:ApprovedAuthor
  }
}
function Read-Authority([string]$Path) {
  Assert-NoReparse $Path
  $authority=Read-State $Path
  $canonical=Parse-Authority ($authority.repository+'#'+$authority.issue)
  if([string]$authority.contract -cne $canonical.contract -or
     [string]$authority.repository -cne $canonical.repository -or
     [int]$authority.issue -ne $canonical.issue -or
     [string]$authority.owner -cne $canonical.owner) {
    throw 'Local authority configuration is invalid.'
  }
  return $canonical
}
function Configure-Authority([string]$Path,[string]$Reference) {
  $candidate=Parse-Authority $Reference
  if(Test-Path -LiteralPath $Path) {
    $existing=Read-Authority $Path
    if($existing.repository -cne $candidate.repository -or $existing.issue -ne $candidate.issue) {
      throw 'Immutable authority already configured; no overwrite.'
    }
    return 'CONFIG_NOOP_VERIFIED'
  }
  $dir=Split-Path -Parent $Path
  Assert-AncestorsSafe $dir
  if(-not (Test-Path -LiteralPath $dir)) {
    New-Item -Path $dir -ItemType Directory -Force | Out-Null
  }
  Assert-NoReparse $dir
  Write-State $Path $candidate
  [void](Read-Authority $Path)
  return 'CONFIG_WRITTEN_VERIFIED'
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
function Assert-AncestorsSafe([string]$Path) {
  $current=[IO.Path]::GetFullPath($Path)
  while($true) {
    if(Test-Path -LiteralPath $current) {
      $item=Get-Item -LiteralPath $current -Force
      if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Reparse point in RDTE path ancestry.'
      }
    }
    $parent=[IO.Directory]::GetParent($current)
    if($null -eq $parent -or $parent.FullName -ceq $current) { break }
    $current=$parent.FullName
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
  Assert-AncestorsSafe $Path
  $json=ConvertTo-Json -InputObject $Value -Depth 8 -Compress
  $bytes=([Text.UTF8Encoding]::new($false)).GetBytes($json)
  $handle=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try {
    $handle.Write($bytes,0,$bytes.Length)
    $handle.Flush($true)
  } finally {
    $handle.Dispose()
  }
}
function Read-State([string]$Path) {
  Assert-AncestorsSafe $Path
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
  Assert-AncestorsSafe $dir
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
  Assert-AncestorsSafe $target
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
  Assert-AncestorsSafe $dir
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
  $configPath=Join-Path $HOME '.local\share\suprachat-rdte\bootstrap\authority.json'
  if($Mode -eq 'Configure') {
    $requested=Parse-Authority $AuthorityRef
    [void]@(Read-DleComments -Authority $requested)
    Write-Output (Configure-Authority $configPath $AuthorityRef)
    exit 0
  }
  if($Mode -eq 'Unconfigure') {
    if(-not $ConfirmUndo) { throw 'Unconfigure requires ConfirmUndo.' }
    if(Test-Path -LiteralPath (Join-Path $HOME '.local\share\suprachat-rdte\packages')) {
      $packages=@(Get-ChildItem -LiteralPath (Join-Path $HOME '.local\share\suprachat-rdte\packages') -Force)
      if($packages.Count -gt 0) { throw 'Unconfigure blocked while owned package slots remain.' }
    }
    if(-not (Test-Path -LiteralPath $configPath)) { Write-Output 'CONFIG_NOOP_ABSENT';exit 0 }
    [void](Read-Authority $configPath)
    Assert-AncestorsSafe $configPath
    Remove-Item -LiteralPath $configPath -Force
    Write-Output 'CONFIG_REMOVED'
    exit 0
  }
  $authority=Read-Authority $configPath
  $comments=@(Read-DleComments -Authority $authority)
  if($Mode -eq 'Undo') {
    if(-not $ConfirmUndo) { throw 'Explicit Undo confirmation missing.' }
    $base=Join-Path $HOME '.local\share\suprachat-rdte\packages'
    $run=Owned-Path $base $RunLabel
    if(-not (Test-Path -LiteralPath $run)) { Write-Output 'NOOP_ABSENT';exit 0 }
    Assert-NoReparse $run
    $owner=Read-State (Join-Path $run 'owner.json')
    $old=Select-UndoPromotion -Comments $comments -Owner $owner
    Write-Output (Undo-Artifact $old $base $RunLabel $true)
    exit 0
  }
  $admission=Select-Promotion -Comments $comments
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
  }
} catch {
  Write-Error ('RDTE bootstrap '+$Mode+' failed: '+$_.Exception.Message)
  exit 1
}
