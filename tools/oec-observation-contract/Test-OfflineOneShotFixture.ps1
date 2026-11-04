#Requires -Version 7.0
<#
Offline end-to-end test of the user-consented post-capture qualification chain.
Creates only a synthetic metadata aggregate and a synthetic DLL placeholder in
the runner's private temporary directory. No browser, login, tunnel, capture,
OpenAI API call or authentication credential. Never claim a real signed-in run.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$trial=Join-Path $PSScriptRoot 'Invoke-LocalObservationTrial.ps1'
$parent=Join-Path $env:RUNNER_TEMP ('oec-local-fixture-' + [Guid]::NewGuid().ToString('N'))
$private=Join-Path $parent 'private'
$binaryFolder=Join-Path $parent 'binary'
$badFolder=Join-Path $parent 'incorrect-binary'
New-Item -ItemType Directory -Force $private,$binaryFolder,$badFolder | Out-Null
$binary=Join-Path $binaryFolder 'SupraChat.dll'
$badBinary=Join-Path $badFolder 'SupraChat.dll'
[IO.File]::WriteAllText($binary,'PUBLIC_SYNTHETIC_DLL_PLACEHOLDER_FOR_CONTRACT_ONLY')
[IO.File]::WriteAllText($badBinary,'WRONG_BINARY_HASH_PUBLIC_SYNTHETIC_ONLY')
$hash=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
$summary=Join-Path $parent 'suprachat-metadata-summary-ci-fixture.json'
$fixture=@'
import json,pathlib,sys
sys.path.insert(0,'tools/oec-observation-contract')
from test_native_summary import sample
doc=sample()
doc['assembly_sha256']=sys.argv[2]
doc['diagnostics']={'ObservationIncidents':0,'LifecycleIncidents':0,'Reasons':{},'TotalIncidents':0}
doc['lifecycle']={'Request':12}
pathlib.Path(sys.argv[1]).write_text(json.dumps(doc,sort_keys=True),encoding='utf-8')
'@
$fixture | & python - $summary $hash
if($LASTEXITCODE -ne 0){throw 'SYNTHETIC_FIXTURE_CONSTRUCTION_FAILED'}
try {
  $invokeParams=@{IReviewedFirstPartyCapture=$true;SummaryPath=$summary;AssemblyPath=$binary;ExpectedEndReason='SummarySave';PrivateOutputDirectory=$private}
  # Exercise the actual native validator -> local independent projection pipeline.
  $response= & $trial @invokeParams
  $receipt=($response | Out-String).Trim() | ConvertFrom-Json
  if($receipt.schema -ne 'oec-local-trial-qualification/v1' -or
     $receipt.native_aggregate_validated -ne $true -or
     $receipt.private_projection_saved -ne $true -or
     $receipt.authenticated_state -ne 'OPERATOR_CHECK_REQUIRED' -or
     $receipt.tunnel_connected -ne $false -or
     $receipt.remote_tool_access_verified -ne $false -or
     $receipt.no_automatic_upload -ne $true) {
      throw 'OFFLINE_LOCAL_TRIAL_RECEIPT_REJECTED'
  }
  $safe=@(Get-ChildItem -LiteralPath $private -Filter 'safe-projection-*.json')
  $native=@(Get-ChildItem -LiteralPath $private -Filter 'native-validation-*.json')
  if($safe.Count -ne 1 -or $native.Count -ne 1){throw 'EXPECTED_TWO_PRIVATE_RECEIPTS'}
  $projection=Get-Content -LiteralPath $safe[0].FullName -Raw | ConvertFrom-Json
  if($projection.schema -ne 'oec-consented-first-party-projection/v1' -or
     $projection.authenticated_state -ne 'OPERATOR_CHECK_REQUIRED' -or
     $projection.user_content_exported -ne $false -or
     $projection.capture_stopped -ne $true){throw 'OFFLINE_PROJECTION_INVALID'}
  # An unrelated matching-name DLL must fail source/binary identity.
  $msg='NONE'
  try {
    $invokeParams.AssemblyPath=$badBinary
    $null=& $trial @invokeParams
  } catch { $msg=$_.Exception.Message }
  if($msg -notmatch 'METADATA_BUILD_MISMATCH') { throw 'MISMATCHED_ASSEMBLY_NOT_REFUSED' }
  # Public repository custody must never become the output destination.
  $msg='NONE'
  try {
    $invokeParams.AssemblyPath=$binary
    $invokeParams.PrivateOutputDirectory=$root
    $null=& $trial @invokeParams
  } catch { $msg=$_.Exception.Message }
  if($msg -notmatch 'REFUSED_PUBLIC_REPOSITORY_CUSTODY') { throw 'PUBLIC_REPOSITORY_CUSTODY_NOT_REFUSED' }
  Write-Host 'QUALIFIED_OFFLINE_NATIVE_ONE_SHOT_SYNTHETIC end_to_end=true wrong_build_refused=true repo_custody_refused=true authenticated=false tunnel=false'
} finally {
  Remove-Item -LiteralPath $parent -Recurse -Force -ErrorAction SilentlyContinue
}
