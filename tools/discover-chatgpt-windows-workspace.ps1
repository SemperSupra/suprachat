#Requires -Version 7.0
<#
Read-only bounded discovery of previously installed portable SDK/runtime tools.
Never writes under the established workspace and never searches user profiles,
browser stores or unrelated files. Returns paths only to the local caller; the
outer actor emits only coarse provenance in the sharable summary.
#>
[CmdletBinding()]
param(
    [string]$WorkspaceRoot = (Join-Path $HOME 'Projects/SemperSupra/SupraChat')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($WorkspaceRoot)
$out = [ordered]@{
    schema = 'suprachat-workspace-tool-discovery/v1'
    workspace_present = (Test-Path -LiteralPath $root -PathType Container)
    dotnet_sdk10 = $null
    node22 = $null
    searched_directories = 0
    bounded = $true
    read_only = $true
    selected_dotnet_path = $null
    selected_node_path = $null
}
if (-not $out.workspace_present) { return [pscustomobject]$out }

# Deliberately constrained to tooling-bearing project directories. Do not
# recursively inspect source, private worktrees, accounts or hidden profiles.
$relativeRoots = @(
    '.local', '_tools', 'suprachat/.local', 'suprachat/_tools',
    '_dogfood/.local', '_dogfood/_tools',
    'suprachat-private/.local'
)
$visited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$rootPath = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$maxDirectories = 200

foreach ($relative in $relativeRoots) {
    $first = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $first.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
    if (-not (Test-Path -LiteralPath $first -PathType Container)) { continue }

    # Limit the search to the root plus three descendant directory levels.
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue([pscustomobject]@{ path = $first; depth = 0 })
    while ($queue.Count -gt 0 -and $out.searched_directories -lt $maxDirectories) {
        $entry = $queue.Dequeue()
        $path = $entry.path
        if (-not $visited.Add($path)) { continue }
        $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if ($null -eq $item -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $out.searched_directories++

        if (-not $out.selected_dotnet_path) {
            $candidate = Join-Path $path 'dotnet.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                try {
                    $sdkRows = @(& $candidate --list-sdks 2>$null)
                    if ($LASTEXITCODE -eq 0) {
                        $match = $sdkRows | Where-Object { $_ -match '^10[.]0[.][0-9]+\s' } | Select-Object -First 1
                        if ($match) {
                            $out.selected_dotnet_path = $candidate
                            $out.dotnet_sdk10 = ($match -split '\s+')[0]
                        }
                    }
                } catch { }
            }
        }
        if (-not $out.selected_node_path) {
            $candidate = Join-Path $path 'node.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                try {
                    $version = (& $candidate --version 2>$null | Out-String).Trim()
                    if ($LASTEXITCODE -eq 0 -and $version -match '^v(\d+)[.](\d+)[.](\d+)$' -and [int]$Matches[1] -ge 22) {
                        $out.selected_node_path = $candidate
                        $out.node22 = $version
                    }
                } catch { }
            }
        }
        if ($out.selected_node_path -and $out.selected_dotnet_path) { break }
        if ($entry.depth -ge 3) { continue }
        foreach ($child in @(Get-ChildItem -LiteralPath $path -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $queue.Enqueue([pscustomobject]@{ path = $child.FullName; depth = $entry.depth + 1 })
        }
    }
    if ($out.selected_node_path -and $out.selected_dotnet_path) { break }
}
[pscustomobject]$out
