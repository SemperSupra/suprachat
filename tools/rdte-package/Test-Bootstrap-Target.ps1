#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$BootstrapPath,
  [Parameter(Mandatory=$true)][string]$TargetPath
)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
if(-not (Test-Path -LiteralPath $BootstrapPath -PathType Leaf) -or
   -not (Test-Path -LiteralPath $TargetPath -PathType Leaf)){
  throw 'Exact source paths missing.'
}
. $BootstrapPath
$sourceSha=('a'*40)
$artifactDigest=(Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$admission=[pscustomobject]@{
  SourceSHA=$sourceSha
  AssetPath='tools/rdte-package/Invoke-SupraChat-RDTE.ps1'
  AssetSHA256=$artifactDigest
}
$base=Join-Path $env:RUNNER_TEMP ('suprachat-integrated-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null
$state=[pscustomobject]@{downloads=0}
$fetch={
  param($url,$destination)
  if($url -cne ('https://raw.githubusercontent.com/SemperSupra/suprachat/'+$sourceSha+'/'+$admission.AssetPath)){
    throw 'Bootstrap constructed an unexpected immutable URL.'
  }
  $state.downloads++
  Copy-Item -LiteralPath $TargetPath -Destination $destination
}.GetNewClosure()
try {
  $installed=Install-Artifact $admission $base 'integration-a' $fetch
  if($installed.Status -cne 'READY_VERIFIED' -or $state.downloads -ne 1){
    throw 'First package install did not verify.'
  }
  $again=Install-Artifact $admission $base 'integration-a' $fetch
  if($again.Status -cne 'NOOP_VERIFIED' -or $state.downloads -ne 1){
    throw 'Repeat install was not a strict no-op.'
  }
  $asset=Verify-Installed $admission $base 'integration-a'
  $run=@('SelfTest','LocalSmoke')
  foreach($mode in $run){
    & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $asset -Mode $mode
    if($LASTEXITCODE -ne 0){throw ('Verified downloaded target failed native '+$mode)}
  }
  # Execution before owner/DLE configuration must fail without attempting live GitHub writes.
  & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $asset -Mode Plan 2>$null | Out-Null
  if($LASTEXITCODE -eq 0){throw 'Unconfigured target unexpectedly planned an authenticated campaign.'}
  $removed=Undo-Artifact $admission $base 'integration-a' $true
  if($removed -cne 'UNDO_COMPLETE'){throw 'Owned cleanup failed.'}
  if((Undo-Artifact $admission $base 'integration-a' $true) -cne 'NOOP_ABSENT'){throw 'Repeated cleanup was not idempotent.'}
  if(Test-Path -LiteralPath (Join-Path $base 'integration-a')){throw 'Owned package slot persists after Undo.'}
  Write-Output 'INTEGRATED NATIVE PS5.1 BOOTSTRAP/TARGET PASS: exact pin, SHA256, no-op, smoke, hold, cleanup.'
} finally {
  if(Test-Path -LiteralPath $base){
    if(@(Get-ChildItem -LiteralPath $base -Force).Count -gt 0){
      throw 'Unreconciled fixture content; preserve evidence instead of deleting.'
    }
    Remove-Item -LiteralPath $base -Force
  }
}
