#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Check','Install')]
    [string]$Mode = 'Check'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-PythonCommand {
    foreach ($name in @('py','python','python3')) {
        $command = Get-Command -Name $name -ErrorAction SilentlyContinue
        if ($command) {
            if ($command.PSObject.Properties['Source'] -and $command.Source) {
                return [string]$command.Source
            }
            if ($command.PSObject.Properties['Path'] -and $command.Path) {
                return [string]$command.Path
            }
            return [string]$command.Name
        }
    }
    return $null
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $Command @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        throw ("Command failed ({0}): {1} {2}{3}{4}" -f $exitCode, $Command, ($Arguments -join ' '), [Environment]::NewLine, $text)
    }

    return [ordered]@{
        ExitCode = $exitCode
        Output = @($output)
        Text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    }
}

function Add-PathEntryForProcess {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $entries = @($env:PATH -split [IO.Path]::PathSeparator)
    if ($entries -notcontains $Path) {
        $env:PATH = $Path + [IO.Path]::PathSeparator + $env:PATH
    }
}

$existing = Get-Command -Name 'git-filter-repo' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host ('git-filter-repo already available: ' + $existing.Source)
    $verify = Invoke-NativeCapture -Command 'git' -Arguments @('filter-repo','--version')
    Write-Host ('git filter-repo --version: ' + $verify.Text)
    exit 0
}

if ($Mode -eq 'Check') {
    Write-Host 'git-filter-repo is not available on PATH.'
    exit 2
}

$python = Get-PythonCommand
if (-not $python) {
    throw 'Python was not found. Install a supported Python 3 runtime, reopen PowerShell, and rerun this installer.'
}

$version = Invoke-NativeCapture -Command $python -Arguments @('--version')
Write-Host ('Python: ' + $version.Text)

$pip = Invoke-NativeCapture -Command $python -Arguments @('-m','pip','--version')
Write-Host ('pip: ' + $pip.Text)

Write-Host 'Installing/upgrading git-filter-repo into the current user Python environment...'
$install = Invoke-NativeCapture -Command $python -Arguments @(
    '-m','pip','install',
    '--disable-pip-version-check',
    '--user',
    '--upgrade',
    'git-filter-repo'
)
if ($install.Text) { Write-Host $install.Text }

$scriptsQuery = "import sysconfig; print(sysconfig.get_path('scripts', scheme='nt_user'))"
$scriptsResult = Invoke-NativeCapture -Command $python -Arguments @('-c',$scriptsQuery)
$userScripts = $scriptsResult.Text.Trim()

if ([string]::IsNullOrWhiteSpace($userScripts)) {
    throw 'Python did not return the user Scripts directory.'
}

Add-PathEntryForProcess -Path $userScripts

$installed = Get-Command -Name 'git-filter-repo' -ErrorAction SilentlyContinue
if (-not $installed) {
    throw ("git-filter-repo installed, but its executable was not found after adding the Python user Scripts directory to this process PATH: {0}" -f $userScripts)
}

$verifyDirect = Invoke-NativeCapture -Command $installed.Source -Arguments @('--version')
$verifyGit = Invoke-NativeCapture -Command 'git' -Arguments @('filter-repo','--version')

Write-Host ('git-filter-repo: ' + $installed.Source)
Write-Host ('direct version:  ' + $verifyDirect.Text)
Write-Host ('git version:     ' + $verifyGit.Text)
Write-Host ''
Write-Host 'PASS: git-filter-repo is installed and usable in this PowerShell process.'
Write-Host ('Current-process PATH addition: ' + $userScripts)
Write-Host 'Note: if that directory is not already in your persistent user PATH, a new shell may not see git-filter-repo until PATH is updated.'
