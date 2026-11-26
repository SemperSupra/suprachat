#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Invoke-SupraChat-RDTE.ps1')

$script:assertions=0
function Confirm([bool]$Condition,[string]$Name) {
  if(-not $Condition){throw ('ASSERT '+$Name)}
  $script:assertions++
}
function MustFail([scriptblock]$Action,[string]$Name) {
  $failed=$false
  try { & $Action | Out-Null } catch { $failed=$true }
  Confirm $failed $Name
}
$script:SourceSha=('a'*40)
$script:DriverSha=('b'*64)
$script:DleRepo='ExampleOrg/owned-dle'
$script:DleIssue=7
$marker='SUPRACHAT-RDTE-PROMOTION/V1'
$promotion=@($marker,'','Status: ADMITTED',
  ('SourceSHA: '+$script:SourceSha),
  'AssetPath: tools/rdte-package/Invoke-SupraChat-RDTE.ps1',
  ('AssetSHA256: '+$script:DriverSha)) -join [Environment]::NewLine
$owner=[pscustomobject]@{login='mark-e-deyoung'}
$valid=[pscustomobject]@{id=[long]250;user=$owner;body=$promotion;html_url='https://example.invalid/250'}
$foreign=[pscustomobject]@{id=[long]251;user=[pscustomobject]@{login='different'};body=$promotion}
$embedded=[pscustomobject]@{id=[long]252;user=$owner;body=('Example follows'+[Environment]::NewLine+$promotion)}
$revoked=[pscustomobject]@{id=[long]255;user=$owner;body=$promotion.Replace('Status: ADMITTED','Status: REVOKED')}
$bad=[pscustomobject]@{id=[long]253;user=$owner;body=($promotion+[Environment]::NewLine+'Status: ADMITTED')}
Confirm ((Find-Promotion @($embedded,$foreign,$valid)).id -eq 250) 'foreign and embedded promotion ignored'
Confirm ((Find-Promotion @($valid,$revoked)).Status -ceq 'REVOKED') 'revoke wins'
MustFail {Find-Promotion @($bad)} 'duplicate field denied'
MustFail {Find-Promotion @($valid,$valid)} 'duplicate ID denied'
MustFail {Find-Promotion @($embedded,$foreign)} 'missing owner promotion denied'

# Test the actual Read-Dle pagination and admission logic with deterministic fake transport.
$script:fake=[pscustomobject]@{
  pages=New-Object System.Collections.ArrayList
  posts=0
  calls=New-Object System.Collections.ArrayList
}
$full1=New-Object System.Collections.ArrayList
$full2=New-Object System.Collections.ArrayList
foreach($i in 1..100) { [void]$full1.Add([pscustomobject]@{id=[long]$i;body='older comment';user=$owner}) }
foreach($i in 101..200) { [void]$full2.Add([pscustomobject]@{id=[long]$i;body='older comment';user=$owner}) }
[void]$script:fake.pages.Add($full1.ToArray())
[void]$script:fake.pages.Add($full2.ToArray())
[void]$script:fake.pages.Add(@($embedded,$valid))
function GitHub {
  param([string]$Method,[string]$Path,[object]$Body=$null)
  [void]$script:fake.calls.Add(($Method+' '+$Path))
  if($Method -ceq 'GET' -and $Path -ceq '/repos/ExampleOrg/owned-dle/issues/7') {
    return [pscustomobject]@{state='open';comments=202}
  }
  if($Method -ceq 'GET' -and $Path -match '^/repos/ExampleOrg/owned-dle/issues/7/comments\?per_page=100&page=([1-9][0-9]*)$') {
    $index=[int]$Matches[1]-1
    if($index -ge $script:fake.pages.Count){return @()}
    return $script:fake.pages[$index]
  }
  if($Method -ceq 'POST') {
    $script:fake.posts++
    throw 'POST disabled in the source-only model-free fixture'
  }
  throw 'Unexpected GitHub endpoint in test double.'
}
$d=Read-Dle
Confirm ($d.pages -eq 3 -and $d.comments.Count -eq 202) 'pagination includes every 100+ DLE comment'
Confirm ($d.admission.id -eq 250) 'latest first-line owner admission found'
Confirm (@($script:fake.calls).Count -eq 4) 'one issue GET and three bounded pages'
# Read-Dle validates exact immutable package identity, not only signer/label.
$prior=$script:DriverSha
$script:DriverSha=('c'*64)
MustFail {Read-Dle} 'mismatched published package digest denied'
$script:DriverSha=$prior
[void]$script:fake.pages.RemoveAt(2)
[void]$script:fake.pages.Add(@($valid,$revoked))
MustFail {Read-Dle} 'latest revocation blocks execution'
Confirm ($script:fake.posts -eq 0) 'model-free fixture performs no remote write'

# Receipt matching is the previous V6 first failure: -cnotmatch must not overwrite collection.
$resultMarker='SUPRACHAT-RDTE-HYGIENE/V7 RUN_LABEL=fixture SOURCE_SHA='+$script:SourceSha+' DRIVER_SHA256='+$script:DriverSha
$resultBody=@($resultMarker,'','Status: CENSUS_COMPLETE','CENSUS_SHA256: '+('d'*64)) -join [Environment]::NewLine
$receipt=[pscustomobject]@{id=[long]300;body=$resultBody}
$kickoff=[pscustomobject]@{id=[long]299;body=('## Kickoff '+[Environment]::NewLine+$resultMarker)}
Confirm ((Existing-Receipt @{comments=@($kickoff,$receipt)} $resultMarker).id -eq 300) 'readback ignores illustrative kickoff'
MustFail {Checked-Receipt @{comments=@($receipt)} $resultMarker ($resultBody+'tamper')} 'full body mismatch denied'
MustFail {Existing-Receipt @{comments=@($receipt,$receipt)} $resultMarker} 'duplicate receipts denied'

Write-Output ('NATIVE PS5.1 MODEL-FREE TARGET PASS; assertions='+$script:assertions)
