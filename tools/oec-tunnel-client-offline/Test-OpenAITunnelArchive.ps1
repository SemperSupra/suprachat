#Requires -Version 5.1
<#
.SYNOPSIS
  Verify the official Windows OpenAI tunnel-client archive before local use.
.DESCRIPTION
  Read-only archive digest and environment-presence checks. Never extracts or
  executes the archive, opens a network socket, uses a credential, creates a
  tunnel, queries ChatGPT or prints a tunnel ID / key / local path.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ArchivePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
# Only absolute local-drive paths are admitted. No drive-relative, UNC, remote
# share, or repository-relative archive path is accepted.
if($ArchivePath -cnotmatch '^[A-Za-z]:[\\/]') {
  throw 'LOCAL_ABSOLUTE_ARCHIVE_PATH_REQUIRED'
}
if(!(Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
  throw 'TUNNEL_CLIENT_ARCHIVE_UNAVAILABLE'
}
$archive=Get-Item -LiteralPath $ArchivePath -ErrorAction Stop
if($archive.Length -lt 1024 -or $archive.Length -gt 67108864 -or
   $archive.Extension -cne '.zip') {
  throw 'UPSTREAM_ARCHIVE_FORMAT_OR_SIZE_REFUSED'
}
$expected='1d0ecc3a54f2a51e27a635ac155f40fbb95df58c52cdeddf6c75532cc0bbeb25'
$actual=(Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if($actual -cne $expected) {
  throw 'UPSTREAM_ARCHIVE_DIGEST_MISMATCH'
}
[pscustomobject]@{
  schema='oec-tunnel-client-local-preflight/v1'
  upstream_repository='openai/tunnel-client'
  upstream_release='v0.0.19'
  platform='windows-amd64'
  archive_sha256=$expected
  upstream_zip_verified=$true
  control_plane_runtime_key_present=(-not [string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_API_KEY))
  tunnel_id_present=(-not [string]::IsNullOrWhiteSpace($env:CONTROL_PLANE_TUNNEL_ID))
  key_scope_validated=$false
  tunnel_provisioned_and_verified=$false
  local_mcp_server_verified=$false
  chatgpt_custom_connector_permission='NOT_ASSESSED'
  chatgpt_connected=$false
  authenticated_webui_observed=$false
  sensitive_content_captured=$false
} | ConvertTo-Json -Compress
