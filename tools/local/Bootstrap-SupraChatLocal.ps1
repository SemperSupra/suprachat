#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = Join-Path $HOME 'Projects\SemperSupra'
$Bootstrap = Join-Path $Root '_bootstrap-suprachat-validation'

$SetupCommit = '69c3f29af685a0f57faf291283c1615668d94cb7'
$SetupBlob = '8422efe875db3ab9c2017bdb27ad16560e75bc1e'

$InstallerCommit = '655fbe55ec3b31869ad927e471d397e305008eb0'
$InstallerBlob = '7eec45e2608d236099e26a5a4efb94e767005f3d'

$ValidationBranch = 'validation/setup-workspace-ps51-ps7-20261002'
$Remote = 'git@github.com:SemperSupra/suprachat.git'
$QualifiedMigrationHead = '627757867ac304c61e57faf65e2fe5e32a4300afe'

$WinPS = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$Arguments
    )

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ('git {0} failed with exit code {1}.' -f ($Arguments -join ' '), $LASTEXITCODE)
    }
}

function Get-GitText {
    param(
        [Parameter(Mandatory=$true)]
        [string[]]$Arguments
    )

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

function Invoke-WinPSFile {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path,

        [Parameter(Mandatory=$true)]
        [string[]]$Arguments
    )

    & $WinPS -NoProfile -ExecutionPolicy Bypass -File $Path @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ('Windows PowerShell child process failed with exit code {0}: {1}' -f $LASTEXITCODE, $Path)
    }
}

Write-Host ''
Write-Host '==> SupraChat local bootstrap'
Write-Host ('Root:      ' + $Root)
Write-Host ('Bootstrap: ' + $Bootstrap)

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git was not found on PATH.'
}

if (-not (Test-Path -LiteralPath $WinPS)) {
    throw ('Windows PowerShell 5.1 was not found at: ' + $WinPS)
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null

Write-Host ''
Write-Host '==> Bootstrap repository'

if (-not (Test-Path -LiteralPath $Bootstrap)) {
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

    Invoke-GitChecked -Arguments @(
        '-C', $Bootstrap,
        'fetch', 'origin', $ValidationBranch
    )
}

Write-Host ''
Write-Host '==> Verify exact tested setup script'

$ActualSetupBlob = Get-GitText -Arguments @(
    '-C', $Bootstrap,
    'rev-parse',
    ($SetupCommit + ':tools/local/Setup-SupraChatWorkspace.ps1')
)

if ($ActualSetupBlob -ne $SetupBlob) {
    throw ("Wrong setup-script blob. Expected {0}; got {1}." -f $SetupBlob, $ActualSetupBlob)
}

Write-Host ('Setup blob: ' + $ActualSetupBlob)

Write-Host ''
Write-Host '==> Verify exact tested git-filter-repo installer'

$ActualInstallerBlob = Get-GitText -Arguments @(
    '-C', $Bootstrap,
    'rev-parse',
    ($InstallerCommit + ':tools/local/Install-GitFilterRepo.ps1')
)

if ($ActualInstallerBlob -ne $InstallerBlob) {
    throw ("Wrong installer blob. Expected {0}; got {1}." -f $InstallerBlob, $ActualInstallerBlob)
}

Write-Host ('Installer blob: ' + $ActualInstallerBlob)

$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ('suprachat-bootstrap-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null

$Setup = Join-Path $TempRoot 'Setup-SupraChatWorkspace.ps1'
$Installer = Join-Path $TempRoot 'Install-GitFilterRepo.ps1'

try {
    $SetupContent = @(& git -C $Bootstrap show ($SetupCommit + ':tools/local/Setup-SupraChatWorkspace.ps1'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not materialize the tested setup script.'
    }
    $SetupContent | Set-Content -LiteralPath $Setup -Encoding UTF8

    $InstallerContent = @(& git -C $Bootstrap show ($InstallerCommit + ':tools/local/Install-GitFilterRepo.ps1'))
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not materialize the tested git-filter-repo installer.'
    }
    $InstallerContent | Set-Content -LiteralPath $Installer -Encoding UTF8

    Write-Host ''
    Write-Host '==> Ensure git-filter-repo is installed'

    $filterRepo = Get-Command -Name 'git-filter-repo' -ErrorAction SilentlyContinue
    if (-not $filterRepo) {
        Invoke-WinPSFile -Path $Installer -Arguments @('-Mode','Install')
    }
    else {
        Write-Host ('git-filter-repo already available: ' + $filterRepo.Source)
    }

    Write-Host ''
    Write-Host '==> Verify git-filter-repo through Git'

    $gitFilterVersion = Get-GitText -Arguments @('filter-repo','--version')
    Write-Host ('git filter-repo --version: ' + $gitFilterVersion)

    Write-Host ''
    Write-Host '==> Run non-mutating workspace Plan'

    Invoke-WinPSFile -Path $Setup -Arguments @(
        '-Mode','Plan',
        '-Root',$Root
    )

    Write-Host ''
    Write-Host '==> Apply workspace setup'

    Invoke-WinPSFile -Path $Setup -Arguments @(
        '-Mode','Apply',
        '-Root',$Root
    )

    Write-Host ''
    Write-Host '==> Final local verification'

    $MigrationPath = Join-Path $Root 'suprachat-migrate'
    $PublicPath = Join-Path $Root 'suprachat'
    $PrivatePath = Join-Path $Root 'suprachat-private'

    foreach ($required in @($PublicPath,$PrivatePath,$MigrationPath)) {
        if (-not (Test-Path -LiteralPath (Join-Path $required '.git'))) {
            throw ('Expected Git repository is missing: ' + $required)
        }
    }

    $MigrationHead = Get-GitText -Arguments @('-C',$MigrationPath,'rev-parse','HEAD')
    if ($MigrationHead -ne $QualifiedMigrationHead) {
        throw ("Migration HEAD mismatch. Expected {0}; got {1}." -f $QualifiedMigrationHead, $MigrationHead)
    }

    Write-Host ''
    Write-Host 'PASS: SupraChat local workspace is fully prepared.'
    Write-Host ('Public product:   ' + $PublicPath)
    Write-Host ('Private DLE:      ' + $PrivatePath)
    Write-Host ('Migration source: ' + $MigrationPath)
    Write-Host ('Migration HEAD:   ' + $MigrationHead)
}
finally {
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
