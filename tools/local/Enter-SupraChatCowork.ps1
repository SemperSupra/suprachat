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

function Get-BatchBaton {
    param([System.Collections.IDictionary]$Plan)

    # Deliberately small-model-friendly; all task detail lives in durable DLE.
    return (@(
        'Resume SupraChat from GitHub DLE, not this conversation.',
        ('Public: ' + $Plan.PublicPath),
        ('Private: ' + $Plan.PrivatePath),
        'Read both AGENTS.md, public docs/PARALLEL_WORKING.md, private issue #2, and the current private worksets/parallel-batches-r48.json (or linked pending PR).',
        'Reconcile live #195, #19/#38/#39, PR heads, Actions receipts, and existing dirty worktrees before actions.',
        'Select 2-4 independent READY batches (disjoint write paths, per-task branch and oracle); never serialize by habit. max-parallel=2 per public GHA workflow.',
        'For each weaker actor, assign one bounded file-path allowlist, objective, fixture, one negative case, and exact PASS oracle. No scope expansion or recursion.',
        'Locally emit small task cards with: python tools/qualification/validate_parallel_workset.py ../suprachat-private/worksets/parallel-batches-r48.json --cards (read-only; no worker starts).',
        'Preserve one session across human/automation/agent, accessible UX, and verified human-owner highest-priority intervention; a UI/voice event is not an auth token.',
        'Dependencies are sequential: W8 owner identity -> W9 durable control/epoch -> real tool fence/cancel HIL -> independent review -> owner-controlled merge/release.',
        'Do not spend, run private GHA, copy credentials, reset dirty trees, or merge/release without workset approval.',
        'Reconcile every independent lane with PR/head/CI/evidence and write next batons to private issue #2. Do not ask the human to relay text.'
    ) -join [Environment]::NewLine)
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
        'Public architecture umbrella: #7; concurrent human control #195; bounded #19/#38/#39/#51. Read live acceptance gates.',
        'Active private execution/DLE authority: SemperSupra/suprachat-private#2.',
        'A local actor must read both AGENTS.md files, docs/PARALLEL_WORKING.md, private parallel-batches-r48.json and live issue context.',
        'Reconcile live GitHub state before acting. Do not reconstruct chat history.',
        'Choose the maximal safe set of independent READY batches; preserve human/automation/agent parity,',
        'and write exact PR/head/CI receipts for each independent lane plus the next baton to the private DLE.',
        '',
        'Human SIWC/OAuth authorization remains a human boundary. Never copy credential material into DLE state.'
    ) -join [Environment]::NewLine

    $prompt = Get-BatchBaton -Plan $Plan

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

    $defaultRoot = Get-WorkspaceRoot -RequestedRoot $null
    $expectedSuffix = [IO.Path]::Combine('Projects','SemperSupra','SupraChat')
    if (-not $defaultRoot.EndsWith($expectedSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add("Default root must end in '$expectedSuffix'; got '$defaultRoot'.")
    }

    $baton = Get-BatchBaton -Plan $plan
    if ($baton -notmatch 'independent READY batches') { $failures.Add('Batch mandate absent from local actor baton.') }
    if ($baton -notmatch 'parallel-batches-r48.json') { $failures.Add('Private workset pointer absent.') }
    if ($baton -notmatch 'max-parallel=2') { $failures.Add('Public CI concurrency limit absent.') }
    if ($baton -notmatch 'human-owner highest-priority') { $failures.Add('Owner-preemption rule absent.') }
    if ($baton -notmatch 'per-task branch and oracle') { $failures.Add('Task isolation rule absent.') }

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

# On case-insensitive filesystems, a legacy sibling checkout named "suprachat"
# aliases the intended container directory "SupraChat". Refuse to nest the
# canonical public/private repositories inside an existing repository root.
$rootGitMarker = Join-Path $plan.Root '.git'
if (Test-Path -LiteralPath $rootGitMarker) {
    throw ("Workspace root '{0}' resolves to an existing Git repository. " +
           "On Windows this can happen when a legacy sibling checkout named 'suprachat' " +
           "collides case-insensitively with the intended container 'SupraChat'. " +
           "Rename or relocate the legacy checkout first; this script will not nest " +
           "canonical repositories inside another repository.") -f $plan.Root
}

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
    foreach ($publicIssue in @(7, 9, 14, 19, 38, 39, 51, 195)) {
        Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat' -Issue $publicIssue -Destination (Join-Path $plan.ContextPath "active-public-issue-$publicIssue.json")
    }
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat' -Issue 4 -Destination (Join-Path $plan.ContextPath 'provenance-public-dogfood-issue-4.json')
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat-private' -Issue 2 -Destination (Join-Path $plan.ContextPath 'active-private-execution-issue-2.json')
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat' -Issue 1 -Destination (Join-Path $plan.ContextPath 'provenance-public-migration-issue-1.json')
    Save-IssueContext -GhPath $ghPath -Repository 'SemperSupra/suprachat-private' -Issue 1 -Destination (Join-Path $plan.ContextPath 'provenance-private-migration-issue-1.json')
}

$publicSnapshot = Get-RepoSnapshot -GitPath $gitPath -RepositoryPath $plan.PublicPath
$privateSnapshot = Get-RepoSnapshot -GitPath $gitPath -RepositoryPath $plan.PrivatePath
Write-CoworkFiles -Plan $plan -Public $publicSnapshot -Private $privateSnapshot

Write-Host ''
Write-Host 'PASS: SupraChat local coworking workspace is reconciled without modifying existing worktrees.'
Write-Host ('Actor baton: ' + $plan.PromptPath)
