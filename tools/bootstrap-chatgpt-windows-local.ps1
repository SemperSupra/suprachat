#Requires -Version 7.0
<#
Project-local, stateless official-tool bootstrap. Dot-source from the qualification
actor so process-only PATH and DOTNET_ROOT changes survive. This code NEVER
changes system/user PATH, package manager databases or Windows execution policy.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [switch]$NeedDotnet,
    [switch]$NeedNode,
    [switch]$ForcePortable
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
    [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'BOOTSTRAP_UNSUPPORTED_ARCH_WIN_X64_ONLY'
}

$project = [IO.Path]::GetFullPath($ProjectRoot)
$toolRoot = Join-Path $project '.local'
$downloads = Join-Path $toolRoot '_downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $toolRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $toolRoot 'nuget'
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $toolRoot 'playwright-browsers'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# This is a LOCAL Git exclusion; the published repository is untouched.
$exclude = Join-Path $project '.git/info/exclude'
if ((Test-Path $exclude) -and -not ((Get-Content $exclude) -contains '.local/')) {
    Add-Content -LiteralPath $exclude -Value '.local/'
}

function Download-VerifiedZip(
    [string]$Url, [string]$ZipPath, [string]$Hash, [string]$Algorithm
) {
    if ($Url -notmatch '^https://(builds[.]dotnet[.]microsoft[.]com|nodejs[.]org)/') {
        throw 'BOOTSTRAP_DOWNLOAD_HOST_NOT_ALLOWED'
    }
    if ($Hash -notmatch '^[a-fA-F0-9]{64}$' -and $Hash -notmatch '^[a-fA-F0-9]{128}$') {
        throw 'BOOTSTRAP_INVALID_EXPECTED_HASH'
    }
    Invoke-WebRequest -Uri $Url -OutFile $ZipPath -TimeoutSec 600 -ErrorAction Stop | Out-Null
    if (-not (Test-Path $ZipPath)) { throw 'BOOTSTRAP_DOWNLOAD_MISSING' }
    $observed = (Get-FileHash -Algorithm $Algorithm -LiteralPath $ZipPath).Hash
    if ($observed -ine $Hash) {
        Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
        throw 'BOOTSTRAP_HASH_MISMATCH'
    }
}

$record = [ordered]@{
    schema = 'suprachat-portable-tools/v1'
    scope = 'project-local'
    dotnet = 'unused'
    node = 'unused'
    dotnet_version = $null
    node_version = $null
    dotnet_sha512 = $null
    node_sha256 = $null
}

if ($NeedDotnet) {
    # Release metadata contains Microsoft's SHA-512 hash for the exact SDK ZIP.
    $pin = '10.0.402'
    $dotnetDir = Join-Path $toolRoot 'dotnet'
    $dotnetExe = Join-Path $dotnetDir 'dotnet.exe'
    $sdkDir = Join-Path $dotnetDir ('sdk/' + $pin)
    if ($ForcePortable -or -not (Test-Path $dotnetExe) -or -not (Test-Path $sdkDir)) {
        $metadataUrl = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
        $metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 90 -ErrorAction Stop
        $release = @($metadata.releases | Where-Object { $_.sdk.version -eq $pin }) | Select-Object -First 1
        if (-not $release) { throw 'BOOTSTRAP_DOTNET_PIN_NOT_IN_MANIFEST' }
        $asset = @($release.sdk.files | Where-Object { $_.name -eq 'dotnet-sdk-win-x64.zip' -or $_.name -eq ('dotnet-sdk-' + $pin + '-win-x64.zip') }) | Select-Object -First 1
        if (-not $asset -or $asset.url -notmatch '^https://builds[.]dotnet[.]microsoft[.]com/dotnet/Sdk/10[.]0[.]402/') {
            throw 'BOOTSTRAP_DOTNET_ASSET_NOT_OFFICIAL'
        }
        $zip = Join-Path $downloads ('dotnet-sdk-' + $pin + '-win-x64.zip')
        try {
            Download-VerifiedZip $asset.url $zip $asset.hash 'SHA512'
            New-Item -ItemType Directory -Force -Path $dotnetDir | Out-Null
            Expand-Archive -LiteralPath $zip -DestinationPath $dotnetDir -Force
        } finally {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        }
        $record.dotnet_sha512 = $asset.hash
    }
    if (-not (Test-Path $dotnetExe) -or -not (Test-Path $sdkDir)) {
        throw 'BOOTSTRAP_DOTNET_EXTRACT_FAILED'
    }
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = $dotnetDir + [IO.Path]::PathSeparator + $env:PATH
    $sdkInventory = @(& $dotnetExe --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not @($sdkInventory | Where-Object { $_ -match '^10[.]0[.]402\s' }).Count) {
        throw 'BOOTSTRAP_DOTNET_VERIFY_FAILED'
    }
    $record.dotnet_version = $pin
    $record.dotnet = 'portable-verified'
}

if ($NeedNode) {
    $pin = 'v24.21.0'
    $filename = 'node-' + $pin + '-win-x64.zip'
    $nodeDir = Join-Path $toolRoot ('node-' + $pin + '-win-x64')
    $nodeExe = Join-Path $nodeDir 'node.exe'
    if ($ForcePortable -or -not (Test-Path $nodeExe)) {
        $base = 'https://nodejs.org/dist/' + $pin
        $checksums = (Invoke-WebRequest -Uri ($base + '/SHASUMS256.txt') -TimeoutSec 90 -ErrorAction Stop).Content
        $escapedFilename = [regex]::Escape($filename)
        $candidate = @($checksums -split [char]10 | Where-Object {
            $_.Trim() -match ('^[a-fA-F0-9]{64}\s+[*]?' + $escapedFilename + '\s*$')
        }) | Select-Object -First 1
        if (-not $candidate) { throw 'BOOTSTRAP_NODE_HASH_NOT_IN_MANIFEST' }
        $hash = ($candidate.Trim() -split '\s+')[0]
        $zip = Join-Path $downloads $filename
        try {
            Download-VerifiedZip ($base + '/' + $filename) $zip $hash 'SHA256'
            Expand-Archive -LiteralPath $zip -DestinationPath $toolRoot -Force
        } finally {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        }
        $record.node_sha256 = $hash
    }
    if (-not (Test-Path $nodeExe)) { throw 'BOOTSTRAP_NODE_EXTRACT_FAILED' }
    $env:PATH = $nodeDir + [IO.Path]::PathSeparator + $env:PATH
    $observed = (& $nodeExe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $observed -ne $pin) {
        throw 'BOOTSTRAP_NODE_VERIFY_FAILED'
    }
    $record.node_version = $pin
    $record.node = 'portable-verified'
}
# Return only trusted provenance metadata, not executable paths or credentials.
[pscustomobject]$record
