#requires -Version 5.1
<#
.SYNOPSIS
Prepares the post-migration SupraChat workspace for local actor coworking.

.DESCRIPTION
Compatible with Windows PowerShell 5.1 and PowerShell 7.x.

Modes:
  Plan     - non-mutating; prints the intended sibling-repository workspace.
  Apply    - clones or fetches canonical public + private repositories, exports
             durable issue context when gh is available, and writes a local actor baton.
  SelfTest - non-mutating structural checks used by CI.

This script never resets, cleans, rebases, or force-checks out an existing worktree.
Existing local changes are preserved.
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

function Get-CommandPath {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,
        [switch]$Optional
    )

    $command = Get-Command -Name $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        if ($Optional) { return $null }
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
        $candidate = Join-Path -Path $HOME -ChildPath 'Projects/SemperSupra'
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
        PublicPath = Join-Path $WorkspaceRoot 'suprachat'
        PrivatePath = Join-Path $WorkspaceRoot 'suprachat-private'
        ContextPath = Join-Path $WorkspaceRoot '_suprachat-cowork-context'
        ManifestPath = Join-Path $WorkspaceRoot 'SUPRACHAT-COWORK.md'
        PromptPath = Join-Path $WorkspaceRoot 'SUPRACHAT-LOCAL-ACTOR.txt'
        PublicSsh = 'git@github.com:SemperSupra/suprachat.git'
        PrivateSsh = 'git@github.com:SemperSupra/suprachat-private.git'
    }
}

function Invoke-Git {
    param(
        [string]$GitPath,
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
            $saved = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $output = @(& $GitPath @Arguments 2>&1)
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $saved
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

function Get-GitText {
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
    $actual = Get-GitText -GitPath $GitPath -Arguments @('remote','get-url','origin') -WorkingDirectory $RepositoryPath
    if ($actual -ne $ExpectedOrigin) {
        throw "Unexpected origin for '$RepositoryPath'. Expected '$ExpectedOrigin'; actual '$actual'."
    }
}

function Ensure-Repository {
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
        Invoke-Git -GitPath $GitPath -Arguments @('clone',$SshUrl,$RepositoryPath)
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $RepositoryPath '.git'))) {
        throw "Path exists but is not a Git repository: $RepositoryPath"
    }

    Assert-Origin -GitPath $GitPath -RepositoryPath $RepositoryPath -ExpectedOrigin $SshUrl
    Invoke-Git -GitPath $GitPath -Arguments @('fetch','--prune','origin') -WorkingDirectory $RepositoryPath
}

function Test-GitHubSsh {
    param([string]$SshPath)

    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $SshPath -T -o BatchMode=yes -o StrictHostKeyChecking=accept-new git@github.com 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if (($text -notmatch 'successfully authenticated') -and ($text -notmatch 'Hi .+!')) {
        throw ("GitHub SSH authentication could not be confirmed (ssh exit {0}).{1}{2}" -f $exitCode, [Environment]::NewLine, $text)
    }
}

function Save-IssueContext {
    param(
        [string]$GhPath,
        [string]$Repository,
        [int]$Issue,
        [string]$Destination
    )

    $saved = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $GhPath issue view $Issue --repo $Repository --json number,title,url,state,body,comments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw ("Unable to export {0}#{1}.{2}{3}" -f $Repository, $Issue, [Environment]::NewLine, $text)
    }
    $text | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Get-RepoSnapshot {
    param(
        [string]$GitPath,
        [string]$RepositoryPath
    )

    return [ordered]@{
        Branch = Get-GitText -GitPath $GitPath -Arguments @('rev-parse','--abbrev-ref','HEAD') -WorkingDirectory $RepositoryPath
        Head = Get-GitText -GitPath $GitPath -Arguments @('rev-parse','HEAD') -WorkingDirectory $RepositoryPath
        Dirty = -not [string]::IsNullOrWhiteSpace((Get-GitText -GitPath $GitPath -Arguments @('status','--porcelain') -WorkingDirectory $RepositoryPath))
    }
}

function Write-CoworkFiles {
    param(
        [System.Collections.IDictionary]$Plan,
        [System.Collections.IDictionary]$Public,
        [System.Collections.IDictionary]$Private
    )

    $manifest = @(
        '# SupraChat local coworking workspace',
        '',
        ('Generated UTC: ' + [DateTime]::UtcNow.ToString('o')),
        '',
        '## Authority',
        '',
        '- Public product/source/CI: SemperSupra/suprachat',
        '- Private DLE/oracle/evidence: SemperSupra/suprachat-private',
        '- Execution/orchestration substrate: Agent Dispatch',
        '',
        '## Public working tree',
        '',
        $Plan.PublicPath,
        ('Branch: ' + $Public.Branch),
        ('HEAD: ' + $Public.Head),
        ('Dirty: ' + $Public.Dirty),
        '',
        '## Private DLE working tree',
        '',
        $Plan.PrivatePath,
        ('Branch: ' + $Private.Branch),
        ('HEAD: ' + $Private.Head),
        ('Dirty: ' + $Private.Dirty),
        '',
        '## Durable context',
        '',
        $Plan.ContextPath,
        '',
        'A local actor should read public AGENTS.md, private AGENTS.md, and the exported issue context.',
        'Reconcile live GitHub state before acting. Do not reconstruct chat history.',
        'Choose the highest READY bounded action, preserve human/automation/agent parity,',
        'and write material evidence plus the next baton back to the private DLE.',
        '',
        'Human SIWC/OAuth authorization remains a human boundary. Never copy credential material into DLE state.'
    ) -join [Environment]::NewLine

    $prompt = @(
        'Resume SupraChat from durable repository state.',
        ('Public working tree: ' + $Plan.PublicPath),
        ('Private DLE working tree: ' + $Plan.PrivatePath),
        ('Context directory: ' + $Plan.ContextPath),
        '',
        'Read both AGENTS.md files and the exported issue context first.',
        'Reconcile live GitHub state before acting.',
        'Continue the highest READY bounded action under the repository Way of Working.',
        'Preserve human UI/UX, automation DX, agent DX, accessibility, credential boundaries, and public/free GHA qualification.',
        'Do not use the human as a routine message relay; write material evidence and the next baton to the private DLE.'
    ) -join [Environment]::NewLine

    $manifest | Set-Content -LiteralPath $Plan.ManifestPath -Encoding UTF8
    $prompt | Set-Content -LiteralPath $Plan.PromptPath -Encoding UTF8
}

function Invoke-SelfTest {
    $root = Get-WorkspaceRoot -RequestedRoot (Join-Path ([IO.Path]::GetTempPath()) 'SupraChat Cowork Root With Spaces')
    $plan = Get-WorkspacePlan -WorkspaceRoot $root
    $failures = New-Object System.Collections.Generic.List[string]

    if ((Split-Path -Leaf $plan.PublicPath) -ne 'suprachat') { $failures.Add('Public path wrong.') }
    if ((Split-Path -Leaf $plan.PrivatePath) -ne 'suprachat-private') { $failures.Add('Private path wrong.') }
    if ($plan.PublicSsh -ne 'git@github.com:SemperSupra/suprachat.git') { $failures.Add('Public remote wrong.') }
    if ($plan.PrivateSsh -ne 'git@github.com:SemperSupra/suprachat-private.git') { $failures.Add('Private remote wrong.') }
    if (-not [IO.Path]::IsPathRooted($plan.Root)) { $failures.Add('Root is not absolute.') }

    if ($failures.Count -gt 0) {
        throw ('SelfTest failed: ' + ($failures -join ' '))
    }

    [ordered]@{
        schema = 'suprachat-local-cowork-selftest/v1'
        powershell = $PSVersionTable.PSVersion.ToString()
        root = $plan.Root
        public = $plan.PublicPath
        private = $plan.PrivatePath
        status = 'PASS'
    } | ConvertTo-Json -Depth 4
}

$workspaceRoot = Get-WorkspaceRoot -RequestedRoot $Root
$plan = Get-WorkspacePlan -WorkspaceRoot $workspaceRoot

if ($Mode -eq 'SelfTest') {
    Invoke-SelfTest
    exit 0
}

Write-Host ''
Write-Host '==> SupraChat post-migration coworking plan'
Write-Host ('Root:       ' + $plan.Root)
Write-Host ('Public:     ' + $plan.PublicPath)
Write-Host ('Private:    ' + $plan.PrivatePath)
Write-Host ('Context:    ' + $plan.ContextPath)
Write-Host ('Manifest:   ' + $plan.ManifestPath)
Write-Host ('Actor baton:' + ' ' + $plan.PromptPath)

$gitPath = Get-CommandPath -Name 'git'
$sshPath = Get-CommandPath -Name 'ssh'
$ghPath = Get-CommandPath -Name 'gh' -Optional
$codexPath = Get-CommandPath -Name 'codex' -Optional

Write-Host ''
Write-Host '==> Prerequisites'
Write-Host ('git:   ' + $gitPath)
Write-Host ('ssh:   ' + $sshPath)
Write-Host ('gh:    ' + $(if ($ghPath) { $ghPath } else { 'NOT FOUND (issue export unavailable)' }))
Write-Host ('codex: ' + $(if ($codexPath) { $codexPath } else { 'NOT FOUND (workspace still usable)' }))

if ($Mode -eq 'Plan') {
    Write-Host ''
    Write-Host 'Plan complete. No repository, file, remote, or authentication state was changed.'
    exit 0
}

Test-GitHubSsh -SshPath $sshPath

if (-not (Test-Path -LiteralPath $plan.Root)) {
    New-Item -ItemType Directory -Path $plan.Root -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $plan.ContextPath)) {
    New-Item -ItemType Directory -Path $plan.ContextPath -Force | Out-Null
}

Write-Host ''
Write-Host '==> Canonical public repository'
Ensure-Repository -GitPath $gitPath -RepositoryPath $plan.PublicPath -SshUrl $plan.PublicSsh

Write-Host ''
Write-Host '==> Private DLE repository'
Ensure-Repository -GitPath $gitPath -RepositoryPath $plan.PrivatePath -SshUrl $plan.PrivateSsh

if (-not $SkipIssueExport -and $ghPath) {
    Write-Host ''
    Write-Host '==> Durable issue context'
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat' -Issue 1 -Destination (Join-Path $plan.ContextPath 'public-issue-1.json')
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat-private' -Issue 1 -Destination (Join-Path $plan.ContextPath 'private-issue-1.json')
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat-private' -Issue 2 -Destination (Join-Path $plan.ContextPath 'private-issue-2.json')
}

$publicSnapshot = Get-RepoSnapshot -GitPath $gitPath -RepositoryPath $plan.PublicPath
$privateSnapshot = Get-RepoSnapshot -GitPath $gitPath -RepositoryPath $plan.PrivatePath
Write-CoworkFiles -Plan $plan -Public $publicSnapshot -Private $privateSnapshot

Write-Host ''
Write-Host 'PASS: SupraChat local coworking workspace is reconciled without modifying existing worktrees.'
Write-Host ('Actor baton: ' + $plan.PromptPath)
