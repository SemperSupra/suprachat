#Requires -Version 7.0
<#
Ownership-aware reversal of SupraChat's project-local portable tools only.
Default is DRY RUN. Does not touch existing workspace, other .local folders,
browser profiles, Git worktrees, receipts, or machine-installed runtimes.
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$namespace = [IO.Path]::GetFullPath((Join-Path $project '.local/suprachat-qualification'))
$expected = [IO.Path]::GetFullPath((Join-Path $project '.local/suprachat-qualification'))
if ($namespace -ne $expected -or
    -not $namespace.StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CLEANUP_OUTSIDE_PROJECT_REFUSED'
}
if (-not (Test-Path -LiteralPath $namespace -PathType Container)) {
    Write-Host 'No SupraChat-owned tool namespace exists; cleanup is idempotently complete.'
    return
}
$item = Get-Item -LiteralPath $namespace
if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'CLEANUP_REPARSE_POINT_REFUSED'
}
$marker = Join-Path $namespace '.suprachat-tools-owned.json'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
    throw 'CLEANUP_OWNERSHIP_MARKER_MISSING'
}
$owner = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
if ($owner.schema -ne 'suprachat-owned-tools/v1' -or
    $owner.project_full_path -ne $project) {
    throw 'CLEANUP_OWNERSHIP_MISMATCH'
}
Write-Host "Owned namespace: $namespace"
if (-not $Apply) {
    Write-Host 'DRY RUN: no files changed. Invoke again with -Apply to remove only this namespace.'
    return
}
# No child junctions/symlinks are accepted in the owned tree.
$unsafe = @(Get-ChildItem -LiteralPath $namespace -Force -Recurse -ErrorAction Stop |
    Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
    Select-Object -First 1)
if ($unsafe.Count -gt 0) { throw 'CLEANUP_NESTED_REPARSE_POINT_REFUSED' }
Remove-Item -LiteralPath $namespace -Recurse -Force -ErrorAction Stop
Write-Host 'SupraChat-owned project-local tools removed. All other workspace files preserved.'
