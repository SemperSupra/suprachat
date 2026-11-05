#Requires -Version 5.1
<#
.SYNOPSIS
  Install the checksum-pinned official tunnel-client v0.0.19 without admin rights.
.DESCRIPTION
  Dry-run unless -Apply. No keys, tunnel IDs, browser state, plugin install or
  service mutation. Accepts an existing archive for offline preflight.
#>
[CmdletBinding()]
param(
 [switch]$Apply,
 [string]$ArchivePath,
 [string]$Destination = (Join-Path $env:LOCALAPPDATA 'SupraChat\TunnelClient')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$expected = '1d0ecc3a54f2a51e27a635ac155f40fbb95df58c52cdeddf6c75532cc0bbeb25'
$url = 'https://github.com/openai/tunnel-client/releases/download/v0.0.19/tunnel-client-v0.0.19-windows-amd64.zip'
if ($Destination -cnotmatch '^[A-Za-z]:[\\/]') { throw 'ABSOLUTE_LOCAL_DESTINATION_REQUIRED' }
$dest = [IO.Path]::GetFullPath($Destination)
if ($dest -match '^[A-Za-z]:[\\/]$') { throw 'REFUSED_VOLUME_ROOT' }
if ($ArchivePath -and $ArchivePath -cnotmatch '^[A-Za-z]:[\\/]') { throw 'ABSOLUTE_LOCAL_ARCHIVE_REQUIRED' }
if (!$Apply) {
  [pscustomobject]@{
    schema='suprachat-tunnel-client-install-plan/v1'
    will_install=$false
    pinned_version='v0.0.19'
    pinned_sha256=$expected
    archive_supplied=(![string]::IsNullOrWhiteSpace($ArchivePath))
    credentials_required=$false
    tunnel_connected=$false
    chatgpt_connector_created=$false
  } | ConvertTo-Json -Compress
  return
}
$stage = Join-Path $env:TEMP ('oec-tunnel-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
  $bundle = if ($ArchivePath) { [IO.Path]::GetFullPath($ArchivePath) } else { Join-Path $stage 'official.zip' }
  if ($ArchivePath) {
    if (!(Test-Path -LiteralPath $bundle -PathType Leaf)) { throw 'OFFICIAL_ARCHIVE_MISSING' }
  } else {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $bundle -MaximumRedirection 5 -UseBasicParsing
  }
  $item = Get-Item -LiteralPath $bundle -ErrorAction Stop
  if ($item.Length -lt 1024 -or $item.Length -gt 67108864) { throw 'OFFICIAL_ARCHIVE_SIZE_REFUSED' }
  $digest = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($digest -cne $expected) { throw 'OFFICIAL_ARCHIVE_DIGEST_MISMATCH' }
  $expanded = Join-Path $stage 'expanded'
  Expand-Archive -LiteralPath $bundle -DestinationPath $expanded -ErrorAction Stop
  $executables = @(Get-ChildItem -LiteralPath $expanded -Recurse -File -Filter 'tunnel-client.exe')
  if ($executables.Count -ne 1) { throw 'UPSTREAM_BINARY_AMBIGUOUS' }
  $binary = $executables[0].FullName
  $version = (& $binary --version | Out-String)
  if ($LASTEXITCODE -ne 0 -or $version -notmatch 'v?0\.0\.19') {
    throw 'UPSTREAM_BINARY_VERSION_MISMATCH'
  }
  # Stage before changing destination, then put only the known executable into place.
  New-Item -ItemType Directory -Path $dest -Force | Out-Null
  $target = Join-Path $dest 'tunnel-client.exe'
  $candidate = Join-Path $dest ('tunnel-client-' + [Guid]::NewGuid().ToString('N') + '.exe')
  Copy-Item -LiteralPath $binary -Destination $candidate
  try {
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash) {
      throw 'COPIED_BINARY_DIGEST_MISMATCH'
    }
    Move-Item -LiteralPath $candidate -Destination $target -Force
  } finally {
    Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
  }
  [pscustomobject]@{
    schema='suprachat-tunnel-client-install-receipt/v1'
    installed=$true
    pinned_version='v0.0.19'
    archive_sha256=$expected
    installed_binary_sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    credentials_read=$false
    tunnel_connected=$false
    chatgpt_connector_created=$false
  } | ConvertTo-Json -Compress
} finally {
  Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
