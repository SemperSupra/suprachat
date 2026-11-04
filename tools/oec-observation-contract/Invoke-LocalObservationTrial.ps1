#Requires -Version 5.1
<#
.SYNOPSIS
  Offline, consented SupraChat first-party aggregate qualification.
.DESCRIPTION
  Never starts authentication, capture, browser instrumentation, a tunnel,
  MCP server, or a network listener. The existing native validator owns
  lifecycle/build checks. The independent projector emits only a bounded,
  sanitized receipt. Original evidence and validation stay private/local.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$IReviewedFirstPartyCapture,
    [string]$SummaryPath,
    [string]$AssemblyPath,
    [ValidateSet('OperatorStop','LeaseExpired','Navigation','SummarySave','ReviewClosed')]
    [string]$ExpectedEndReason = 'SummarySave',
    [string]$PrivateOutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$nativeValidator = Join-Path $root 'tools/local/Test-NativeMetadataSummary.ps1'
$projector = Join-Path $PSScriptRoot 'export_native_summary.py'

if ($SelfTest) {
    if ($IReviewedFirstPartyCapture -or $SummaryPath -or $AssemblyPath -or $PrivateOutputDirectory) {
        throw 'SELFTEST_NO_LIVE_ARGUMENTS_PERMITTED'
    }
    if (!(Test-Path -LiteralPath $nativeValidator -PathType Leaf) -or
        !(Test-Path -LiteralPath $projector -PathType Leaf)) {
        throw 'QUALIFICATION_SOURCES_MISSING'
    }
    [pscustomobject]@{
        schema = 'oec-local-trial-preflight/v1'
        passed = $true
        native_validator_present = $true
        sanitized_projector_present = $true
        browser_launched = $false
        authenticated_state = 'NOT_ASSESSED'
        tunnel_connected = $false
    } | ConvertTo-Json -Compress
    return
}
if (!$IReviewedFirstPartyCapture) {
    throw 'EXPLICIT_OPERATOR_REVIEW_REQUIRED'
}
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'POWERSHELL_7_REQUIRED_FOR_NATIVE_VALIDATOR'
}
if (![IO.Path]::IsPathFullyQualified($SummaryPath) -or
    ![IO.Path]::IsPathFullyQualified($AssemblyPath) -or
    ![IO.Path]::IsPathFullyQualified($PrivateOutputDirectory)) {
    throw 'ABSOLUTE_PRIVATE_PATHS_REQUIRED'
}
if (!(Test-Path -LiteralPath $SummaryPath -PathType Leaf) -or
    !(Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
    throw 'LOCAL_EVIDENCE_OR_ASSEMBLY_UNAVAILABLE'
}
if ((Split-Path $AssemblyPath -Leaf) -cne 'SupraChat.dll' -or
    (Split-Path $SummaryPath -Leaf) -cnotmatch '^suprachat-metadata-summary-[A-Za-z0-9-]+\.json$') {
    throw 'UNEXPECTED_SOURCE_ARTIFACT'
}
$private = [IO.Path]::GetFullPath($PrivateOutputDirectory)
$repository = [IO.Path]::GetFullPath($root).TrimEnd([char]92,[char]47)
$comparison = [StringComparison]::OrdinalIgnoreCase
if ($private.Equals($repository, $comparison) -or
    $private.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, $comparison) -or
    $private.StartsWith($repository + [IO.Path]::AltDirectorySeparatorChar, $comparison)) {
    throw 'REFUSED_PUBLIC_REPOSITORY_CUSTODY'
}
$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
if ($null -eq $python) { throw 'LOCAL_PYTHON_REQUIRED_FOR_SANITIZED_PROJECTION' }
$assembly = (Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash
New-Item -ItemType Directory -Force -Path $private | Out-Null
$runId = [Guid]::NewGuid().ToString('N')
$validationFile = Join-Path $private ('native-validation-' + $runId + '.json')
$projectionFile = Join-Path $private ('safe-projection-' + $runId + '.json')
$nativeOut = & $nativeValidator -SummaryPath $SummaryPath -ExpectedAssemblyHash $assembly -ExpectedEndReason $ExpectedEndReason -ReceiptPath $validationFile
$native = ($nativeOut | Out-String).Trim() | ConvertFrom-Json
if ($native.aggregate_contract_valid -ne $true -or
    $native.cleanup_complete -ne $true -or
    $native.signed_in_state -ne 'OPERATOR_CHECK_REQUIRED' -or
    $native.public_upload_performed -ne $false) {
    throw 'NATIVE_QUALIFICATION_NOT_ACCEPTED'
}
$projected = & $python.Source $projector $SummaryPath 2>$null
if ($LASTEXITCODE -ne 0) { throw 'SANITIZED_PROJECTION_REFUSED' }
$projection = ($projected | Out-String).Trim() | ConvertFrom-Json
if ($projection.schema -ne 'oec-consented-first-party-projection/v1' -or
    $projection.source_aggregate_sha256 -cne $native.source_sha256 -or
    $projection.authenticated_state -ne 'OPERATOR_CHECK_REQUIRED' -or
    $projection.user_content_exported -ne $false) {
    throw 'INDEPENDENT_RECEIPT_MISMATCH'
}
$bytes = [Text.UTF8Encoding]::new($false).GetBytes((($projection | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine))
$handle = [IO.FileStream]::new($projectionFile, [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $handle.Write($bytes, 0, $bytes.Length); $handle.Flush($true) }
finally { $handle.Dispose() }
[pscustomobject]@{
    schema='oec-local-trial-qualification/v1'
    native_aggregate_validated=$true
    private_projection_saved=$true
    authenticated_state='OPERATOR_CHECK_REQUIRED'
    remote_tool_access_verified=$false
    tunnel_connected=$false
    no_automatic_upload=$true
    projected_evidence_class=$projection.evidence_class
} | ConvertTo-Json -Compress
