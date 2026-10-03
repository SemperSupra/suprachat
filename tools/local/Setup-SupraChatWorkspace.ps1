#requires -Version 5.1
<#
.SYNOPSIS
Sets up the local SupraChat workspace under ~/Projects/SemperSupra/SupraChat.

.DESCRIPTION
Compatible with Windows PowerShell 5.1 and PowerShell 7.x.

Modes:
  Plan     - non-mutating; shows the intended workspace and prerequisite state.
  Apply    - verifies GitHub SSH, clones/verifies repositories, pins the migration
             source to the qualified SHA, and exports issue context when gh is available.
  SelfTest - non-mutating internal behavioral checks used by CI.

The script never depends on the caller's current directory and never changes it.
All Git remotes use SSH.
#>

[CmdletBinding()]
param(
    [ValidateSet('Plan','Apply','SelfTest')]
    [string]$Mode = 'Plan',

    [string]$Root,

    [switch]$SkipIssueExport
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Org = 'SemperSupra'
$script:MigrationBranch = 'exp/suprachat-macos-seal-repair-qualification-20261002'
$script:QualifiedSourceSha = '627757867ac304c61e57af65e2fe5e32a4300afe'
$script:PublicIssue = 1
$script:PrivateIssue = 1

function Get-CommandPath {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,

        [switch]$Optional
    )

    $command = Get-Command -Name $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        if ($Optional) {
            return $null
        }
        throw "Required command '$Name' was not found on PATH."
    }

    if ($command.PSObject.Properties['Source'] -and $command.Source) {
        return [string]$command.Source
    }

    if ($command.PSObject.Properties['Path'] -and $command.Path) {
        return [string]$command.Path
    }

    return [string]$command.Name
}

function Get-WorkspaceRoot {
    param([string]$RequestedRoot)

    $candidate = $RequestedRoot
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = Join-Path -Path $HOME -ChildPath 'Projects/SemperSupra/SupraChat'
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($candidate)

    if ($expanded -eq '~') {
        $expanded = $HOME
    }
    elseif ($expanded.Length -ge 2 -and $expanded[0] -eq '~' -and
            ($expanded[1] -eq '/' -or $expanded[1] -eq [char]92)) {
        $expanded = Join-Path -Path $HOME -ChildPath $expanded.Substring(2)
    }

    return [IO.Path]::GetFullPath($expanded)
}

function Get-WorkspacePlan {
    param([string]$WorkspaceRoot)

    return [ordered]@{
        Root = $WorkspaceRoot
        PublicPath = Join-Path -Path $WorkspaceRoot -ChildPath 'suprachat'
        PrivatePath = Join-Path -Path $WorkspaceRoot -ChildPath 'suprachat-private'
        MigrationPath = Join-Path -Path $WorkspaceRoot -ChildPath 'suprachat-migrate'
        ContextPath = Join-Path -Path $WorkspaceRoot -ChildPath '_migration-context'
        ManifestPath = Join-Path -Path $WorkspaceRoot -ChildPath 'SUPRACHAT-WORKSPACE.md'
        PublicSsh = 'git@github.com:SemperSupra/suprachat.git'
        PrivateSsh = 'git@github.com:SemperSupra/suprachat-private.git'
        SourceSsh = 'git@github.com:SemperSupra/agent-dispatch.git'
    }
}

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host ('==> ' + $Text)
}

function Invoke-Git {
    param(
        [Parameter(Mandatory=$true)]
        [string]$GitPath,

        [Parameter(Mandatory=$true)]
        [string[]]$Arguments,

        [string]$WorkingDirectory,

        [switch]$Capture
    )

    $oldLocation = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            $oldLocation = Get-Location
            Set-Location -LiteralPath $WorkingDirectory
        }

        if ($Capture) {
            $savedErrorActionPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $output = @(& $GitPath @Arguments 2>&1)
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $savedErrorActionPreference
            }

            if ($exitCode -ne 0) {
                $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
                throw ("git {0} failed with exit code {1}.{2}{3}" -f ($Arguments -join ' '), $exitCode, [Environment]::NewLine, $text)
            }
            return ,$output
        }

        & $GitPath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw ("git {0} failed with exit code {1}." -f ($Arguments -join ' '), $LASTEXITCODE)
        }
    }
    finally {
        if ($oldLocation) {
            Set-Location -LiteralPath $oldLocation.Path
        }
    }
}

function Get-GitOutputText {
    param(
        [string]$GitPath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    $output = Invoke-Git -GitPath $GitPath -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Capture
    return (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
}

function Assert-Origin {
    param(
        [string]$GitPath,
        [string]$RepositoryPath,
        [string]$ExpectedOrigin
    )

    $actual = Get-GitOutputText -GitPath $GitPath -Arguments @('remote','get-url','origin') -WorkingDirectory $RepositoryPath
    if ($actual -ne $ExpectedOrigin) {
        throw ("Unexpected origin for '{0}'. Expected '{1}'; actual '{2}'." -f $RepositoryPath, $ExpectedOrigin, $actual)
    }
}

function Ensure-Clone {
    param(
        [string]$GitPath,
        [string]$RepositoryPath,
        [string]$SshUrl
    )

    if (Test-Path -LiteralPath $RepositoryPath) {
        if (-not (Test-Path -LiteralPath (Join-Path -Path $RepositoryPath -ChildPath '.git'))) {
            throw "Path exists but is not a Git repository: $RepositoryPath"
        }

        Assert-Origin -GitPath $GitPath -RepositoryPath $RepositoryPath -ExpectedOrigin $SshUrl
        Invoke-Git -GitPath $GitPath -Arguments @('fetch','--prune','origin') -WorkingDirectory $RepositoryPath
        return
    }

    $parent = Split-Path -Parent $RepositoryPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Invoke-Git -GitPath $GitPath -Arguments @('clone',$SshUrl,$RepositoryPath)
    Assert-Origin -GitPath $GitPath -RepositoryPath $RepositoryPath -ExpectedOrigin $SshUrl
}

function Ensure-MigrationClone {
    param(
        [string]$GitPath,
        [string]$RepositoryPath,
        [string]$SshUrl
    )

    if (-not (Test-Path -LiteralPath $RepositoryPath)) {
        $parent = Split-Path -Parent $RepositoryPath
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        Invoke-Git -GitPath $GitPath -Arguments @(
            'clone',
            '--single-branch',
            '--branch',
            $script:MigrationBranch,
            $SshUrl,
            $RepositoryPath
        )
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path -Path $RepositoryPath -ChildPath '.git'))) {
        throw "Migration path exists but is not a Git repository: $RepositoryPath"
    }

    Assert-Origin -GitPath $GitPath -RepositoryPath $RepositoryPath -ExpectedOrigin $SshUrl

    $dirty = Get-GitOutputText -GitPath $GitPath -Arguments @('status','--porcelain') -WorkingDirectory $RepositoryPath
    if (-not [string]::IsNullOrWhiteSpace($dirty)) {
        throw "Migration repository has local changes. Refusing to overwrite them: $RepositoryPath"
    }

    Invoke-Git -GitPath $GitPath -Arguments @('fetch','origin',$script:MigrationBranch) -WorkingDirectory $RepositoryPath

    $type = Get-GitOutputText -GitPath $GitPath -Arguments @('cat-file','-t',$script:QualifiedSourceSha) -WorkingDirectory $RepositoryPath
    if ($type -ne 'commit') {
        throw "Qualified source SHA is not available as a commit: $($script:QualifiedSourceSha)"
    }

    Invoke-Git -GitPath $GitPath -Arguments @('checkout','--detach',$script:QualifiedSourceSha) -WorkingDirectory $RepositoryPath

    $head = Get-GitOutputText -GitPath $GitPath -Arguments @('rev-parse','HEAD') -WorkingDirectory $RepositoryPath
    if ($head -ne $script:QualifiedSourceSha) {
        throw "Migration source HEAD mismatch. Expected $($script:QualifiedSourceSha); actual $head."
    }
}

function Test-GitHubSsh {
    param([string]$SshPath)

    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $SshPath -T -o BatchMode=yes -o StrictHostKeyChecking=accept-new git@github.com 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

    if (($text -notmatch 'successfully authenticated') -and ($text -notmatch 'Hi .+!')) {
        throw ("GitHub SSH authentication could not be confirmed (ssh exit {0}).{1}{2}" -f $exitCode, [Environment]::NewLine, $text)
    }

    Write-Host $text.Trim()
}

function Test-GitFilterRepo {
    param([string]$GitPath)

    $command = Get-Command -Name 'git-filter-repo' -ErrorAction SilentlyContinue

    return [ordered]@{
        Available = [bool]$command
        Detail = if ($command) { [string]$command.Source } else { 'not found on PATH' }
    }
}

function Save-IssueContext {
    param(
        [string]$GhPath,
        [string]$Repository,
        [int]$Issue,
        [string]$Destination
    )

    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $GhPath issue view $Issue --repo $Repository --json number,title,url,state,body,comments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

    if ($exitCode -ne 0) {
        throw ("Unable to export {0}#{1}.{2}{3}" -f $Repository, $Issue, [Environment]::NewLine, $text)
    }

    $text | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Write-WorkspaceManifest {
    param(
        [System.Collections.IDictionary]$Plan,
        [string]$Path
    )

    $content = @(
        '# SupraChat local workspace',
        '',
        ('Generated UTC: ' + [DateTime]::UtcNow.ToString('o')),
        '',
        '## Public product repository',
        '',
        $Plan.PublicPath,
        '',
        ('Remote: ' + $Plan.PublicSsh),
        '',
        '## Private DLE / evidence repository',
        '',
        $Plan.PrivatePath,
        '',
        ('Remote: ' + $Plan.PrivateSsh),
        '',
        '## Qualified migration source',
        '',
        $Plan.MigrationPath,
        '',
        ('Remote: ' + $Plan.SourceSsh),
        ('Source branch: ' + $script:MigrationBranch),
        ('Qualified source SHA: ' + $script:QualifiedSourceSha),
        'Independent green qualification: SemperSupra/agent-dispatch#126 run 37003842653',
        '',
        '## Authority',
        '',
        'Migration DLE: SemperSupra/suprachat-private#1',
        'Public migration tracker: SemperSupra/suprachat#1',
        '',
        'Do not switch canonical source authority until SemperSupra/suprachat reproduces',
        'the green Windows/Linux/macOS qualification from its own CI.'
    ) -join [Environment]::NewLine

    $content | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-SelfTest {
    $testRoot = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath 'SupraChat Compatibility Root With Spaces'
    $selfRoot = Get-WorkspaceRoot -RequestedRoot $testRoot
    $plan = Get-WorkspacePlan -WorkspaceRoot $selfRoot
    $failures = New-Object System.Collections.Generic.List[string]

    if (-not [IO.Path]::IsPathRooted($plan.Root)) {
        $failures.Add('Workspace root is not absolute.')
    }
    if ((Split-Path -Leaf $plan.PublicPath) -ne 'suprachat') {
        $failures.Add('Public repo path is wrong.')
    }
    if ((Split-Path -Leaf $plan.PrivatePath) -ne 'suprachat-private') {
        $failures.Add('Private repo path is wrong.')
    }
    if ((Split-Path -Leaf $plan.MigrationPath) -ne 'suprachat-migrate') {
        $failures.Add('Migration repo path is wrong.')
    }
    if ($plan.PublicSsh -ne 'git@github.com:SemperSupra/suprachat.git') {
        $failures.Add('Public SSH remote is wrong.')
    }
    if ($plan.PrivateSsh -ne 'git@github.com:SemperSupra/suprachat-private.git') {
        $failures.Add('Private SSH remote is wrong.')
    }
    if ($plan.SourceSsh -ne 'git@github.com:SemperSupra/agent-dispatch.git') {
        $failures.Add('Source SSH remote is wrong.')
    }
    if ($script:QualifiedSourceSha -notmatch '^[0-9a-f]{40}$') {
        $failures.Add('Qualified source SHA is malformed.')
    }

    if ($failures.Count -gt 0) {
        throw ('SelfTest failed: ' + ($failures -join ' '))
    }

    $result = [ordered]@{
        schema = 'suprachat-workspace-selftest/v1'
        powershell = $PSVersionTable.PSVersion.ToString()
        edition = if ($PSVersionTable.ContainsKey('PSEdition')) { [string]$PSVersionTable.PSEdition } else { 'Desktop' }
        root = $plan.Root
        public = $plan.PublicPath
        private = $plan.PrivatePath
        migration = $plan.MigrationPath
        status = 'PASS'
    }

    $result | ConvertTo-Json -Depth 4
}

$workspaceRoot = Get-WorkspaceRoot -RequestedRoot $Root
$plan = Get-WorkspacePlan -WorkspaceRoot $workspaceRoot

$rootGitMarker = Join-Path $plan.Root '.git'
if (Test-Path -LiteralPath $rootGitMarker) {
    throw ("Workspace root '{0}' resolves to an existing Git repository. " +
           "On Windows a legacy sibling checkout named 'suprachat' can collide " +
           "case-insensitively with the intended container 'SupraChat'. " +
           "Relocate the legacy checkout before running setup.") -f $plan.Root
}

if ($Mode -eq 'SelfTest') {
    Invoke-SelfTest
    exit 0
}

Write-Section 'SupraChat workspace plan'
Write-Host ('Root:       ' + $plan.Root)
Write-Host ('Public:     ' + $plan.PublicPath)
Write-Host ('Private:    ' + $plan.PrivatePath)
Write-Host ('Migration:  ' + $plan.MigrationPath)
Write-Host ('Context:    ' + $plan.ContextPath)
Write-Host ('Manifest:   ' + $plan.ManifestPath)

$gitPath = Get-CommandPath -Name 'git'
$sshPath = Get-CommandPath -Name 'ssh'
$ghPath = Get-CommandPath -Name 'gh' -Optional
$codexPath = Get-CommandPath -Name 'codex' -Optional
$filterRepo = Test-GitFilterRepo -GitPath $gitPath

Write-Section 'Prerequisites'
Write-Host ('git:         ' + $gitPath)
Write-Host ('ssh:         ' + $sshPath)
Write-Host ('gh:          ' + $(if ($ghPath) { $ghPath } else { 'NOT FOUND (issue export unavailable)' }))
Write-Host ('codex:       ' + $(if ($codexPath) { $codexPath } else { 'NOT FOUND' }))
Write-Host ('filter-repo: ' + $(if ($filterRepo.Available) { 'available' } else { 'NOT FOUND (required for migration extraction)' }))

if ($Mode -eq 'Plan') {
    Write-Host ''
    Write-Host 'Plan complete. No files, repositories, remotes, or authentication state were changed.'
    Write-Host 'Run again with -Mode Apply to create/verify the workspace.'
    exit 0
}

Write-Section 'GitHub SSH authentication'
Test-GitHubSsh -SshPath $sshPath

Write-Section 'Creating workspace root'
if (-not (Test-Path -LiteralPath $plan.Root)) {
    New-Item -ItemType Directory -Path $plan.Root -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $plan.ContextPath)) {
    New-Item -ItemType Directory -Path $plan.ContextPath -Force | Out-Null
}

Write-Section 'Public product repository'
Ensure-Clone -GitPath $gitPath -RepositoryPath $plan.PublicPath -SshUrl $plan.PublicSsh

Write-Section 'Private DLE repository'
Ensure-Clone -GitPath $gitPath -RepositoryPath $plan.PrivatePath -SshUrl $plan.PrivateSsh

Write-Section 'Qualified migration source'
Ensure-MigrationClone -GitPath $gitPath -RepositoryPath $plan.MigrationPath -SshUrl $plan.SourceSsh

if (-not $SkipIssueExport) {
    Write-Section 'Migration issue context'
    if ($ghPath) {
        Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat-private' -Issue $script:PrivateIssue -Destination (Join-Path -Path $plan.ContextPath -ChildPath 'suprachat-private-issue-1.json')
        Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat' -Issue $script:PublicIssue -Destination (Join-Path -Path $plan.ContextPath -ChildPath 'suprachat-public-issue-1.json')
    }
    else {
        Write-Warning 'gh was not found; issue context export was skipped.'
    }
}

Write-Section 'Workspace manifest'
Write-WorkspaceManifest -Plan $plan -Path $plan.ManifestPath

Write-Section 'Final verification'
Assert-Origin -GitPath $gitPath -RepositoryPath $plan.PublicPath -ExpectedOrigin $plan.PublicSsh
Assert-Origin -GitPath $gitPath -RepositoryPath $plan.PrivatePath -ExpectedOrigin $plan.PrivateSsh
Assert-Origin -GitPath $gitPath -RepositoryPath $plan.MigrationPath -ExpectedOrigin $plan.SourceSsh

$migrationHead = Get-GitOutputText -GitPath $gitPath -Arguments @('rev-parse','HEAD') -WorkingDirectory $plan.MigrationPath
if ($migrationHead -ne $script:QualifiedSourceSha) {
    throw 'Migration source is not pinned to the qualified SHA.'
}

Write-Host ''
Write-Host 'PASS: SupraChat workspace is set up and verified.'
Write-Host ('Product working tree: ' + $plan.PublicPath)
Write-Host ('Private DLE:          ' + $plan.PrivatePath)
Write-Host ('Migration source:     ' + $plan.MigrationPath)
Write-Host ('Migration HEAD:       ' + $migrationHead)

if (-not $filterRepo.Available) {
    Write-Warning 'git filter-repo is not installed. The repositories are ready, but install git-filter-repo before executing the history extraction in suprachat-private#1.'
}
