#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $HOME 'Projects\SemperSupra'),
    [switch]$SkipApply,
    [switch]$UseExistingBootstrap
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
$Bootstrap = Join-Path $Root '_bootstrap-suprachat-validation'
$ValidationBranch = 'validation/setup-workspace-ps51-ps7-20261002'
$Remote = 'git@github.com:SemperSupra/suprachat.git'

$WrapperCommit = '3bed932119f19f6a8929000d62fd39cb6d7a2339'
$WrapperBlob = '71b99c30c986b3ee5dfa6cb2aced353d36f1106a'

$WinPS = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Invoke-GitChecked {
    param([Parameter(Mandatory=$true)][string[]]$Arguments)

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ('git {0} failed with exit code {1}.' -f ($Arguments -join ' '), $LASTEXITCODE)
    }
}

function Get-GitText {
    param([Parameter(Mandatory=$true)][string[]]$Arguments)

    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }

    if ($exitCode -ne 0) {
        $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        throw ('git {0} failed with exit code {1}.{2}{3}' -f ($Arguments -join ' '), $exitCode, [Environment]::NewLine, $text)
    }

    return (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
}

function Get-PythonUserScriptsPath {
    foreach ($candidate in @('py','python','python3')) {
        $command = Get-Command -Name $candidate -ErrorAction SilentlyContinue
        if (-not $command) { continue }

        $path = if ($command.PSObject.Properties['Source'] -and $command.Source) {
            [string]$command.Source
        }
        elseif ($command.PSObject.Properties['Path'] -and $command.Path) {
            [string]$command.Path
        }
        else {
            [string]$command.Name
        }

        $saved = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = @(& $path -c "import sysconfig; print(sysconfig.get_path('scripts', scheme='nt_user'))" 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $saved
        }

        if ($exitCode -eq 0) {
            $scriptsPath = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
            if (-not [string]::IsNullOrWhiteSpace($scriptsPath) -and (Test-Path -LiteralPath $scriptsPath)) {
                return $scriptsPath
            }
        }
    }

    return $null
}

function Ensure-PathEntry {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [switch]$PersistUserPath
    )

    $currentEntries = @($env:PATH -split [IO.Path]::PathSeparator)
    if ($currentEntries -notcontains $Path) {
        $env:PATH = $Path + [IO.Path]::PathSeparator + $env:PATH
    }

    if ($PersistUserPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path','User')
        $userEntries = if ([string]::IsNullOrWhiteSpace($userPath)) {
            @()
        }
        else {
            @($userPath -split ';')
        }

        if ($userEntries -notcontains $Path) {
            $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) {
                $Path
            }
            else {
                $Path + ';' + $userPath
            }

            [Environment]::SetEnvironmentVariable('Path',$newUserPath,'User')
        }
    }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git was not found on PATH.'
}

if (-not (Test-Path -LiteralPath $WinPS)) {
    throw ('Windows PowerShell 5.1 was not found at: ' + $WinPS)
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null

Write-Host ''
Write-Host '==> Acquire exact tested SupraChat bootstrap'

if ($UseExistingBootstrap) {
    if (-not (Test-Path -LiteralPath (Join-Path $Bootstrap '.git'))) {
        throw ('UseExistingBootstrap requires a Git repository at: ' + $Bootstrap)
    }
}
elseif (-not (Test-Path -LiteralPath $Bootstrap)) {
    Invoke-GitChecked -Arguments @(
        'clone',
        '--single-branch',
        '--branch', $ValidationBranch,
        $Remote,
        $Bootstrap
    )
}
else {
    if (-not (Test-Path -LiteralPath (Join-Path $Bootstrap '.git'))) {
        throw ('Bootstrap path exists but is not a Git repository: ' + $Bootstrap)
    }

    $origin = Get-GitText -Arguments @('-C',$Bootstrap,'remote','get-url','origin')
    if ($origin -ne $Remote) {
        throw ("Unexpected bootstrap origin. Expected '{0}', got '{1}'." -f $Remote, $origin)
    }

    Invoke-GitChecked -Arguments @('-C',$Bootstrap,'fetch','origin',$ValidationBranch)
}

$actualWrapperBlob = Get-GitText -Arguments @(
    '-C',$Bootstrap,
    'rev-parse',
    ($WrapperCommit + ':tools/local/Bootstrap-SupraChatLocal.ps1')
)

if ($actualWrapperBlob -ne $WrapperBlob) {
    throw ("Wrong bootstrap-wrapper blob. Expected {0}; got {1}." -f $WrapperBlob, $actualWrapperBlob)
}

Write-Host ('Wrapper blob: ' + $actualWrapperBlob)

$tempScript = Join-Path ([IO.Path]::GetTempPath()) ('Bootstrap-SupraChatLocal-' + [Guid]::NewGuid().ToString('N') + '.ps1')

try {
    $content = @(& git -C $Bootstrap show ($WrapperCommit + ':tools/local/Bootstrap-SupraChatLocal.ps1'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not materialize the exact tested bootstrap wrapper.'
    }

    $content | Set-Content -LiteralPath $tempScript -Encoding UTF8

    $childArgs = @(
        '-NoProfile',
        '-ExecutionPolicy','Bypass',
        '-File',$tempScript,
        '-Root',$Root
    )

    if ($SkipApply) {
        $childArgs += '-SkipApply'
    }

    if ($UseExistingBootstrap) {
        $childArgs += '-UseExistingBootstrap'
    }

    & $WinPS @childArgs
    if ($LASTEXITCODE -ne 0) {
        throw ('SupraChat bootstrap failed with exit code {0}.' -f $LASTEXITCODE)
    }
}
finally {
    if (Test-Path -LiteralPath $tempScript) {
        Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '==> Make git-filter-repo available to this shell and future shells'

if (-not (Get-Command -Name 'git-filter-repo' -ErrorAction SilentlyContinue)) {
    $scriptsPath = Get-PythonUserScriptsPath
    if (-not $scriptsPath) {
        throw 'git-filter-repo was installed, but the Python user Scripts directory could not be resolved.'
    }

    Ensure-PathEntry -Path $scriptsPath -PersistUserPath
    Write-Host ('Python user Scripts: ' + $scriptsPath)
}

$version = Get-GitText -Arguments @('filter-repo','--version')
Write-Host ('git filter-repo --version: ' + $version)

Write-Host ''
if ($SkipApply) {
    Write-Host 'PASS: Integrated SupraChat bootstrap validation completed through Plan.'
}
else {
    Write-Host 'PASS: SupraChat local bootstrap, workspace Apply, and git-filter-repo setup completed.'
    Write-Host ('Workspace root: ' + $Root)
}
