#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Start-SupraChat-RDTE.ps1')

$script:checks=0
function Check([bool]$Condition,[string]$Name) {
  if(-not $Condition) { throw ('ASSERT FAILED: '+$Name) }
  $script:checks++
}
function MustThrow([scriptblock]$Action,[string]$Name) {
  $failed=$false
  try { & $Action | Out-Null } catch { $failed=$true }
  Check $failed $Name
}
$payload='Write-Output "RDTE synthetic package"'
$bytes=[Text.Encoding]::UTF8.GetBytes($payload)
$sha=[BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
$source=('a'*40)
$marker='SUPRACHAT-RDTE-PROMOTION/V1'
$path='tools/rdte-package/Invoke-SupraChat-RDTE.ps1'
$body=@($marker,'','Status: ADMITTED',('SourceSHA: '+$source),('AssetPath: '+$path),('AssetSHA256: '+$sha)) -join [Environment]::NewLine
$owner=[pscustomobject]@{login='mark-e-deyoung'}
$valid=[pscustomobject]@{id=1000;user=$owner;body=$body}
$embedded=[pscustomobject]@{id=1001;user=$owner;body=('## Example'+[Environment]::NewLine+$body)}
$foreign=[pscustomobject]@{id=1002;user=[pscustomobject]@{login='not-the-owner'};body=$body}
$revoked=[pscustomobject]@{id=1003;user=$owner;body=$body.Replace('Status: ADMITTED','Status: REVOKED')}
$unknown=[pscustomobject]@{id=1004;user=$owner;body=$body.Replace('Status: ADMITTED','Status: UNKNOWN')}
$conflictingPath=[pscustomobject]@{id=1005;user=$owner;body=$body.Replace('tools/rdte-package/','tools/surprise/')}
$duplicateKey=[pscustomobject]@{id=1006;user=$owner;body=($body+[Environment]::NewLine+'Status: ADMITTED')}

MustThrow {Select-Promotion -Comments @()} 'empty admission fail closed'
MustThrow {Select-Promotion -Comments @($embedded,$foreign)} 'embedded and foreign ignored'
$p=Select-Promotion -Comments @($embedded,$foreign,$valid)
Check ($p.id -eq 1000 -and $p.SourceSHA -ceq $source -and $p.AssetSHA256 -ceq $sha) 'valid exact owner admission'
MustThrow {Select-Promotion -Comments @($valid,$revoked)} 'latest revoke wins'
MustThrow {Select-Promotion -Comments @($valid,$valid)} 'duplicate id conflicts'
MustThrow {Select-Promotion -Comments @($unknown)} 'unknown promotion status'
MustThrow {Select-Promotion -Comments @($conflictingPath)} 'path disallowed'
MustThrow {Select-Promotion -Comments @($duplicateKey)} 'duplicate key rejected'
Check ((Asset-Url $p) -ceq ('https://raw.githubusercontent.com/SemperSupra/suprachat/'+$source+'/'+$path)) 'exact immutable safe URL'
MustThrow {Owned-Path 'C:\SomeFolder' '..'} 'traversal label rejected'
MustThrow {Owned-Path 'C:\SomeFolder' 'a/b'} 'slash label rejected'
MustThrow {Undo-Artifact $p 'C:\empty-test' 'unit' $false} 'undo explicit confirmation'

$base=Join-Path $env:RUNNER_TEMP ('rdte-bootstrap-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null
try {
  $configPath=Join-Path $base 'bootstrap\authority.json'
  $fixtureRef='ExampleOrg/owner-workset#72'
  $parsed=Parse-Authority $fixtureRef
  Check ($parsed.repository -ceq 'ExampleOrg/owner-workset' -and $parsed.issue -eq 72) 'one-time issue reference parsing'
  MustThrow {Parse-Authority 'https://evil.example/' } 'unknown network host cannot enter authority'
  MustThrow {Parse-Authority 'owner/repo#0'} 'invalid issue zero rejected'
  Check ((Configure-Authority $configPath $fixtureRef) -ceq 'CONFIG_WRITTEN_VERIFIED') 'immutable one-time configuration'
  Check ((Configure-Authority $configPath $fixtureRef) -ceq 'CONFIG_NOOP_VERIFIED') 'repeated config is NOOP'
  Check ((Read-Authority $configPath).repository -ceq 'ExampleOrg/owner-workset') 'config persisted unchanged'
  MustThrow {Configure-Authority $configPath 'DifferentOrg/other#73'} 'cannot redirect configured authority'
  $state=[pscustomobject]@{count=0;mode='valid'}
  $download={
    param($url,$dest)
    $state.count++
    if($state.mode -eq 'fail') { throw 'Synthetic network aborted' }
    if($state.mode -eq 'mismatch') {
      [IO.File]::WriteAllText($dest,'wrong',(New-Object Text.UTF8Encoding))
    } else {
      [IO.File]::WriteAllText($dest,$payload,([Text.UTF8Encoding]::new($false)))
    }
  }.GetNewClosure()
  $a=Install-Artifact $p $base 'good-a' $download
  Check ($a.Status -ceq 'READY_VERIFIED') 'first install complete'
  Check ($state.count -eq 1) 'download exactly once'
  $b=Install-Artifact $p $base 'good-a' $download
  Check ($b.Status -ceq 'NOOP_VERIFIED') 'idempotent repeat'
  Check ($state.count -eq 1) 'no re-download'
  Check ((Test-Path -LiteralPath (Verify-Installed $p $base 'good-a')) -eq $true) 'verify existing'
  $file=Owned-Path $base 'good-a'
  [IO.File]::WriteAllText((Join-Path $file 'Invoke-SupraChat-RDTE.ps1'),'tampered')
  MustThrow {Verify-Installed $p $base 'good-a'} 'tamper detected'
  Check ((Undo-Artifact $p $base 'good-a' $true) -ceq 'UNDO_COMPLETE') 'cleanup owned tampered content'
  Check ((Undo-Artifact $p $base 'good-a' $true) -ceq 'NOOP_ABSENT') 'repeat undo no-op'

  $state.mode='fail'
  MustThrow {Install-Artifact $p $base 'aborted' $download} 'download error preserved'
  Check (Test-Path -LiteralPath (Join-Path $base 'aborted\owner.json')) 'interrupted owner marker'
  MustThrow {Install-Artifact $p $base 'aborted' $download} 'no retry interrupted run'
  Check ((Undo-Artifact $p $base 'aborted' $true) -ceq 'UNDO_COMPLETE') 'reversible incomplete run'

  $state.mode='mismatch'
  MustThrow {Install-Artifact $p $base 'mismatch' $download} 'hash mismatch fails closed'
  Check (-not (Test-Path -LiteralPath (Join-Path $base 'mismatch\ready.json'))) 'never READY on mismatch'
  Check ((Undo-Artifact $p $base 'mismatch' $true) -ceq 'UNDO_COMPLETE') 'remove wrong-hash owned tree'

  $state.mode='valid'
  Install-Artifact $p $base 'unknown' $download | Out-Null
  [IO.File]::WriteAllText((Join-Path $base 'unknown\foreign.txt'),'foreign')
  MustThrow {Undo-Artifact $p $base 'unknown' $true} 'unknown file protected'
  Remove-Item -LiteralPath (Join-Path $base 'unknown\foreign.txt') -Force
  Check ((Undo-Artifact $p $base 'unknown' $true) -ceq 'UNDO_COMPLETE') 'known owned files reversible'
} finally {
  if(Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
}
# Transport pagination is tested without any private token or live GitHub call.
$first=@(1..100 | ForEach-Object { [pscustomobject]@{id=$_;body='fixture';user=$owner} })
$second=@(101..104 | ForEach-Object { [pscustomobject]@{id=$_;body='fixture';user=$owner} })
$pagination=[pscustomobject]@{calls=0}
$reader={
  param($page)
  $pagination.calls++
  if($page -eq 1) { foreach($x in $first){Write-Output $x} }
  if($page -eq 2) { foreach($x in $second){Write-Output $x} }
}.GetNewClosure()
$collected=@(Collect-DlePages -ReadPage $reader)
Check ($collected.Count -eq 104) 'two-page DLE fetch returns each comment exactly once'
Check ($pagination.calls -eq 2) 'pagination terminates on short page'
Check ($collected[0].id -eq 1 -and $collected[-1].id -eq 104) 'pagination preserves sequence'
$emptyPage={ param($page) }.GetNewClosure()
Check (@(Collect-DlePages -ReadPage $emptyPage).Count -eq 0) 'empty DLE page is empty, not an array wrapper'
$malformedPage={ param($page) [pscustomobject]@{body='missing id'} }.GetNewClosure()
MustThrow { Collect-DlePages -ReadPage $malformedPage } 'malformed page item fails closed'
$fullPage={param($page) foreach($x in $first){Write-Output $x}}.GetNewClosure()
MustThrow { Collect-DlePages -ReadPage $fullPage } 'ten-page boundary fails closed'
Write-Output ('NATIVE WINDOWS PS5.1 BOOTSTRAP PASS; assertions='+$script:checks)
