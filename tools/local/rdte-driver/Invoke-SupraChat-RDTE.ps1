#requires -Version 5.1
<#
SupraChat model-free Windows RDTE driver. DLE: SemperSupra/suprachat-private#2.
Copy this exact file into $HOME\Downloads. It never depends on current directory.
This source is deliberately an operator-side runner, not a browser control agent.
#>
[CmdletBinding()]
param(
    [ValidateSet('Plan','Apply','Verify','Publish','UiReadOnly','Undo','SelfTest')]
    [string]$Mode = 'Plan',
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$SourceSha = '27460bd291d57072799f1ff70d5727de6392a9ed',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{1,39}$')]
    [string]$RunLabel = 'model-a-hil-01',
    [string]$SandboxRoot,
    [switch]$InstallDisposableDotNet,
    [switch]$InstallDisposableGo,
    [switch]$SkipGo,
    [switch]$Repeat,
    [ValidateRange(1,2147483647)][int]$TargetProcessId,
    [ValidateRange(1,4)][int]$UiSamples = 3
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$script:Schema = 'suprachat-local-rdte-driver/v1'
$script:KickoffMarker = 'SUPRACHAT-RDTE-DRIVER/V1'
$script:ResultMarker = 'SUPRACHAT-RDTE-RESULT/V1'
$script:DleRepo = 'SemperSupra/suprachat-private'
$script:DleIssue = 2
$script:PublicRepo = 'SemperSupra/suprachat'
$script:DotNetSdkVersion = '10.0.100'
$script:GoVersion = 'go1.23.12'
$script:SavedEnvironment = @{}

function Assert-AbsoluteSafePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) {
        throw 'ABSOLUTE_DIRECTORY_REQUIRED'
    }
    $full = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $HOME '.local\share\suprachat-rdte'))
    if (-not $full.StartsWith($allowed + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'OUTSIDE_DISPOSABLE_ROOT_REFUSED' }
    if ($full.Length -le ($allowed.Length + 8)) { throw 'BROAD_DELETE_BOUNDARY_REFUSED' }
    return $full
}
function Test-ReparseComponents([string]$Path) {
    $current = $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'REPARSE_POINT_BOUNDARY_REFUSED'
            }
        }
        $parent = Split-Path -Path $current -Parent
        if ($parent -eq $current -or -not $parent) { break }
        $current = $parent
    }
}
function Get-FileDigest([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Save-JsonAtomic([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 16
    $tmp = $Path + '.pending'
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($tmp, $json + [Environment]::NewLine, $utf8)
    if (Test-Path -LiteralPath $Path) {
        [IO.File]::Replace($tmp,$Path,$null)
    } else {
        [IO.File]::Move($tmp,$Path)
    }
}
function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'RECEIPT_NOT_FOUND' }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}
function Download-Uri([string]$Url, [string]$FilePath) {
    $uri = [Uri]::new($Url)
    if ($uri.Scheme -ne 'https') { throw 'HTTPS_ONLY_DOWNLOAD' }
    $allowed = @('api.github.com','codeload.github.com','builds.dotnet.microsoft.com',
        'dotnetcli.blob.core.windows.net','go.dev','dl.google.com')
    if ($uri.Host -notin $allowed) { throw 'UNAPPROVED_DOWNLOAD_ORIGIN' }
    Invoke-WebRequest -Uri $uri.AbsoluteUri -UseBasicParsing -OutFile $FilePath -Headers @{
        'User-Agent' = 'SupraChat-RDTE-Disposable/1.0'; 'Accept' = 'application/octet-stream'
    } -MaximumRedirection 4 | Out-Null
    if (-not (Test-Path -LiteralPath $FilePath)) { throw 'DOWNLOAD_FAILED' }
}
function Assert-ZipEntries([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::OpenRead($ZipPath)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read)
        try {
            foreach ($entry in $zip.Entries) {
                $n = $entry.FullName.Replace('\','/')
                if ($n.StartsWith('/') -or $n.Contains(':') -or $n.Contains('../') -or
                    $n -match '(^|/)\.\.(/|$)' -or $n -match '^\.{1,2}$') {
                    throw 'ARCHIVE_PATH_TRAVERSAL_REFUSED'
                }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
function Expand-ZipSafely([string]$ZipPath, [string]$Destination) {
    Assert-ZipEntries $ZipPath
    if (Test-Path -LiteralPath $Destination) { throw 'EXTRACTION_DESTINATION_ALREADY_EXISTS' }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $Destination
}
function Get-DleComments {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        $cmd = if ($gh.Source) { $gh.Source } else { $gh.Path }
        $all = New-Object System.Collections.ArrayList
        $complete = $false
        for ($page=1; $page -le 10; $page++) {
            $endpoint = 'repos/' + $script:DleRepo + '/issues/' + $script:DleIssue +
                '/comments?per_page=100&page=' + $page
            $output = & $cmd api $endpoint 2>$null
            if ($LASTEXITCODE -ne 0) { break }
            $items = @(($output -join "`n") | ConvertFrom-Json)
            foreach ($item in $items) { [void]$all.Add($item) }
            if ($items.Count -lt 100) { $complete = $true; break }
        }
        if ($complete) { return @($all.ToArray()) }
    }
    $token = [Environment]::GetEnvironmentVariable('GH_TOKEN','Process')
    if (-not $token) { $token = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN','Process') }
    if (-not $token) { throw 'PRIVATE_DLE_AUTH_REQUIRED_GH_OR_TOKEN' }
    $headers = @{ Authorization = 'Bearer ' + $token; 'User-Agent' = 'SupraChat-RDTE-Disposable/1.0';
        Accept = 'application/vnd.github+json' }
    $results = New-Object System.Collections.ArrayList
    for ($page=1; $page -le 10; $page++) {
        $url = 'https://api.github.com/repos/' + $script:DleRepo + '/issues/' +
            $script:DleIssue + '/comments?per_page=100&page=' + $page
        $items = @(Invoke-RestMethod -Uri $url -Headers $headers -Method Get -ErrorAction Stop)
        foreach ($item in $items) { [void]$results.Add($item) }
        if ($items.Count -lt 100) { return @($results.ToArray()) }
    }
    throw 'DLE_PAGINATION_INCOMPLETE'
}
function Assert-Kickoff($Comments,[string]$Sha,[string]$DriverDigest) {
    $required = $script:KickoffMarker + ' SOURCE_SHA=' + $Sha + ' DRIVER_SHA256=' + $DriverDigest
    foreach ($item in @($Comments)) {
        if ([string]$item.body -and ([string]$item.body).Contains($required)) { return }
    }
    throw 'EXACT_DLE_KICKOFF_NOT_PUBLISHED'
}
function Post-DleOnce([string]$CommentText,[string]$UniqueMarker) {
    $comments = @(Get-DleComments)
    if (@($comments | Where-Object { $_.body -and ([string]$_.body).Contains($UniqueMarker) }).Count -gt 0) {
        return 'ALREADY_PUBLISHED'
    }
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        $ghcmd = if ($gh.Source) { $gh.Source } else { $gh.Path }
        $pending = Join-Path $script:WorkingRoot 'dle-comment.pending.md'
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($pending,$CommentText,$utf8)
        try {
            & $ghcmd issue comment $script:DleIssue --repo $script:DleRepo --body-file $pending 1>$null 2>$null
            if ($LASTEXITCODE -eq 0) {
                if (@(Get-DleComments | Where-Object { $_.body -and ([string]$_.body).Contains($UniqueMarker) }).Count -gt 0) {
                    return 'PUBLISHED'
                }
                throw 'DLE_POST_READBACK_FAILED'
            }
        } finally { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
    }
    $token = [Environment]::GetEnvironmentVariable('GH_TOKEN','Process')
    if (-not $token) { $token = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN','Process') }
    if (-not $token) { throw 'DLE_POST_AUTH_REQUIRED' }
    $h = @{ Authorization='Bearer '+$token; 'User-Agent'='SupraChat-RDTE-Disposable/1.0';
        Accept='application/vnd.github+json' }
    $url = 'https://api.github.com/repos/' + $script:DleRepo + '/issues/' + $script:DleIssue + '/comments'
    $body = @{ body=$CommentText } | ConvertTo-Json -Compress
    $null = Invoke-RestMethod -Uri $url -Method Post -Headers $h -Body $body -ContentType 'application/json'
    if (@(Get-DleComments | Where-Object { $_.body -and ([string]$_.body).Contains($UniqueMarker) }).Count -eq 0) {
        throw 'DLE_POST_READBACK_FAILED'
    }
    return 'PUBLISHED'
}
function Set-PrivateTestEnvironment {
    $variables = @('OPENAI_API_KEY','OPENAI_ADMIN_KEY','CONTROL_PLANE_API_KEY','GH_TOKEN',
        'GITHUB_TOKEN','SUPRACHAT_BUNDLE_PASSPHRASE','ANTHROPIC_API_KEY','OPENROUTER_API_KEY',
        'AZURE_OPENAI_API_KEY','GOOGLE_API_KEY')
    foreach ($name in $variables) {
        $script:SavedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$null,'Process')
    }
    $script:SavedEnvironment['DOTNET_CLI_HOME'] = $env:DOTNET_CLI_HOME
    $script:SavedEnvironment['NUGET_PACKAGES'] = $env:NUGET_PACKAGES
    $script:SavedEnvironment['DOTNET_CLI_TELEMETRY_OPTOUT'] = $env:DOTNET_CLI_TELEMETRY_OPTOUT
    $script:SavedEnvironment['DOTNET_NOLOGO'] = $env:DOTNET_NOLOGO
    $script:SavedEnvironment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
    $script:SavedEnvironment['GOCACHE'] = $env:GOCACHE
    $script:SavedEnvironment['GOMODCACHE'] = $env:GOMODCACHE
    $script:SavedEnvironment['GOTOOLCHAIN'] = $env:GOTOOLCHAIN
    $script:SavedEnvironment['NUGET_HTTP_CACHE_PATH'] = $env:NUGET_HTTP_CACHE_PATH
    $script:SavedEnvironment['NUGET_PLUGINS_CACHE_PATH'] = $env:NUGET_PLUGINS_CACHE_PATH
    $script:SavedEnvironment['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE
    $script:SavedEnvironment['TEMP'] = $env:TEMP
    $script:SavedEnvironment['TMP'] = $env:TMP
    $env:DOTNET_CLI_HOME = Join-Path $script:WorkingRoot 'cache\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $script:WorkingRoot 'cache\nuget'
    $env:GOCACHE = Join-Path $script:WorkingRoot 'cache\go-build'
    $env:GOMODCACHE = Join-Path $script:WorkingRoot 'cache\go-modules'
    $env:GOTOOLCHAIN = 'local'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $script:WorkingRoot 'cache\nuget-http'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $script:WorkingRoot 'cache\nuget-plugins'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true'
    $env:TEMP = Join-Path $script:WorkingRoot 'cache\temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_NOLOGO='1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
    foreach ($p in @($env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:GOCACHE,$env:GOMODCACHE,$env:NUGET_HTTP_CACHE_PATH,$env:NUGET_PLUGINS_CACHE_PATH,$env:TEMP)) {
        New-Item -ItemType Directory -Path $p -Force | Out-Null
    }
}
function Restore-TestEnvironment {
    foreach ($entry in $script:SavedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process')
    }
    $script:SavedEnvironment.Clear()
}
function Get-DotNetTool {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $exe = if ($cmd.Source) {$cmd.Source} else {$cmd.Path}
        $versions = @(& $exe --list-sdks 2>$null)
        if (@($versions | Where-Object { $_ -match '^10\.' }).Count -gt 0) { return $exe }
    }
    $local = Join-Path $script:WorkingRoot 'tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    if (-not $InstallDisposableDotNet) { throw 'DOTNET_10_SDK_UNAVAILABLE_ENABLE_DISPOSABLE_INSTALL' }
    $url = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $releaseIndex = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent'='SupraChat-RDTE/1.0' }
    $match = @($releaseIndex.releases | Where-Object { $_.sdk.version -eq $script:DotNetSdkVersion })
    if ($match.Count -ne 1) { throw 'PINNED_DOTNET_SDK_NOT_IN_OFFICIAL_RELEASE_INDEX' }
    $assets = @($match[0].sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -match '\.zip$' })
    if ($assets.Count -ne 1 -or $assets[0].hash -notmatch '^[a-fA-F0-9]{64}$') { throw 'DOTNET_ASSET_HASH_MISSING' }
    $asset = $assets[0]
    $hostName = ([Uri]$asset.url).Host
    if ($hostName -notin @('builds.dotnet.microsoft.com','dotnetcli.blob.core.windows.net')) {
        throw 'DOTNET_DOWNLOAD_HOST_REFUSED'
    }
    $archive = Join-Path $script:WorkingRoot 'dotnet-sdk.zip'
    Download-Uri $asset.url $archive
    if ((Get-FileDigest $archive) -ne $asset.hash.ToLowerInvariant()) { throw 'DOTNET_ARCHIVE_SHA256_MISMATCH' }
    Expand-ZipSafely $archive (Join-Path $script:WorkingRoot 'tools\dotnet')
    Remove-Item -LiteralPath $archive -Force
    return $local
}
function Get-GoTool {
    if ($SkipGo) { return $null }
    $cmd = Get-Command go -ErrorAction SilentlyContinue
    if ($cmd) {
        $existing = if ($cmd.Source) { $cmd.Source } else {$cmd.Path}
        $reported = (& $existing version 2>$null) -join ' '
        if ($reported -match 'go1\.(2[3-9]|[3-9][0-9])\.') { return $existing }
    }
    $local = Join-Path $script:WorkingRoot 'tools\go\go\bin\go.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    if (-not $InstallDisposableGo) { return $null }
    $list = @(Invoke-RestMethod -Uri 'https://go.dev/dl/?mode=json&include=all' -Headers @{
        'User-Agent'='SupraChat-RDTE/1.0' })
    $release = @($list | Where-Object { $_.version -eq $script:GoVersion })
    if ($release.Count -ne 1) { throw 'PINNED_GO_VERSION_NOT_PUBLISHED' }
    $asset = @($release[0].files | Where-Object { $_.filename -eq ($script:GoVersion + '.windows-amd64.zip') })
    if ($asset.Count -ne 1 -or $asset[0].sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'GO_ASSET_SHA256_MISSING' }
    $archive = Join-Path $script:WorkingRoot 'go-sdk.zip'
    Download-Uri ('https://go.dev/dl/' + $asset[0].filename) $archive
    if ((Get-FileDigest $archive) -ne $asset[0].sha256.ToLowerInvariant()) { throw 'GO_ARCHIVE_SHA256_MISMATCH' }
    Expand-ZipSafely $archive (Join-Path $script:WorkingRoot 'tools\go')
    Remove-Item -LiteralPath $archive -Force
    return $local
}
function Get-PinnedSource([string]$Sha) {
    $existing = Join-Path $script:WorkingRoot 'source'
    $owner = Join-Path $existing '.rdte-source-sha'
    if (Test-Path -LiteralPath $existing) {
        if (-not (Test-Path -LiteralPath $owner) -or
            (Get-Content -LiteralPath $owner -Raw).Trim() -ne $Sha) { throw 'UNOWNED_OR_WRONG_SOURCE_SNAPSHOT' }
        return $existing
    }
    $uri = 'https://api.github.com/repos/' + $script:PublicRepo + '/commits/' + $Sha
    $gitCommit = Invoke-RestMethod -Uri $uri -Headers @{ 'User-Agent'='SupraChat-RDTE/1.0' }
    if ($gitCommit.sha -ne $Sha) { throw 'SOURCE_COMMIT_NOT_EXACT' }
    $archive = Join-Path $script:WorkingRoot 'public-source.zip'
    Download-Uri ('https://api.github.com/repos/' + $script:PublicRepo + '/zipball/' + $Sha) $archive
    $stage = Join-Path $script:WorkingRoot 'source-extracted'
    Expand-ZipSafely $archive $stage
    $roots = @(Get-ChildItem -LiteralPath $stage -Directory -Force)
    if ($roots.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $roots[0].FullName 'SupraChat\SupraChat.csproj'))) {
        throw 'SOURCE_ARCHIVE_SHAPE_REFUSED'
    }
    Move-Item -LiteralPath $roots[0].FullName -Destination $existing
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($owner,$Sha,$utf8)
    Remove-Item -LiteralPath $stage -Force -Recurse
    Remove-Item -LiteralPath $archive -Force
    return $existing
}
function Invoke-ProcessCheck([string]$Name,[string]$Executable,[string]$Arguments,[string]$WorkingDirectory,[int]$TimeoutSeconds) {
    $logs = Join-Path $script:WorkingRoot 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $stdout = Join-Path $logs ($Name + '.stdout.txt')
    $stderr = Join-Path $logs ($Name + '.stderr.txt')
    foreach ($file in @($stdout,$stderr)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
    $started = [DateTime]::UtcNow
    $p = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
            return [ordered]@{name=$Name;status='TIMEOUT';exit_code=-1;seconds=$TimeoutSeconds;
                stdout_sha256=$(if(Test-Path -LiteralPath $stdout){Get-FileDigest $stdout}else{$null});
                stderr_sha256=$(if(Test-Path -LiteralPath $stderr){Get-FileDigest $stderr}else{$null})}
        }
        $exit = $p.ExitCode
        return [ordered]@{name=$Name;status=$(if ($exit -eq 0) {'PASS'} else {'FAIL'});
            exit_code=$exit;seconds=[int]([DateTime]::UtcNow-$started).TotalSeconds;
            stdout_sha256=$(if(Test-Path -LiteralPath $stdout){Get-FileDigest $stdout}else{$null});
            stderr_sha256=$(if(Test-Path -LiteralPath $stderr){Get-FileDigest $stderr}else{$null})}
    } finally { $p.Dispose() }
}
function Invoke-DeterministicSuites([string]$Source,[string]$DotNet,[string]$Go) {
    $tests = New-Object System.Collections.ArrayList
    $dotnetCases = @(
        @{Name='app-build';Args='build SupraChat/SupraChat.csproj -c Release'},
        @{Name='core-contracts';Args='run --project SupraChat.ContractTests/SupraChat.ContractTests.csproj -c Release'},
        @{Name='workbench-contracts';Args='run --project SupraChat.WorkbenchContractTests/SupraChat.WorkbenchContractTests.csproj -c Release'},
        @{Name='ledger-projection';Args='run --project SupraChat.LedgerProjectionTests/SupraChat.LedgerProjectionTests.csproj -c Release'},
        @{Name='automation-build';Args='build SupraChat.Automation/SupraChat.Automation.csproj -c Release'},
        @{Name='automation-capabilities';Args='run --project SupraChat.Automation/SupraChat.Automation.csproj -c Release -- capabilities'},
        @{Name='automation-accessibility';Args='run --project SupraChat.Automation/SupraChat.Automation.csproj -c Release -- accessibility'}
    )
    foreach ($case in $dotnetCases) {
        $result = Invoke-ProcessCheck -Name $case.Name -Executable $DotNet -Arguments $case.Args `
            -WorkingDirectory $Source -TimeoutSeconds 900
        [void]$tests.Add($result)
        if ($result.status -ne 'PASS' -and $case.Name -eq 'app-build') { break }
    }
    if ($Go) {
        foreach ($module in @('suprachat-provision','oec-tunnel-bootstrap','oec-installer-admission',
                 'oec-install-handoff','oec-cloud-preflight','ephemeral-tunnel-lab')) {
            $dir = Join-Path $Source ('tools\' + $module)
            if (-not (Test-Path -LiteralPath (Join-Path $dir 'go.mod'))) { continue }
            $result = Invoke-ProcessCheck -Name ('go-' + $module) -Executable $Go `
                -Arguments 'test -shuffle=on -count=2 ./...' -WorkingDirectory $dir -TimeoutSeconds 900
            [void]$tests.Add($result)
        }
    } else {
        [void]$tests.Add([ordered]@{name='go-toolchain';status='NOT_RUN_TOOL_NOT_AVAILABLE';exit_code=$null;seconds=0})
    }
    return @($tests.ToArray())
}
function Invoke-UiReadOnly([string]$Source,[int]$TargetId,[int]$Samples) {
    if ($TargetId -le 0) { throw 'EXPLICIT_SUPRACHAT_PROCESS_ID_REQUIRED' }
    $p = Get-Process -Id $TargetId -ErrorAction Stop
    if ($p.ProcessName -ne 'SupraChat' -or $p.HasExited) { throw 'NON_SUPRACHAT_PROCESS_REFUSED' }
    $scriptPath = Join-Path $Source 'tools\oec-uia-apartments\Inspect-ApartmentUIA.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) { throw 'UIA_STRUCTURAL_PROBE_MISSING' }
    $powershell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell)) {
        $powershell=(Get-Command powershell.exe -ErrorAction Stop).Source
    }
    $cmdargs = '-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "' +
        $scriptPath + '" -ProcessId ' + $TargetId + ' -Samples ' + $Samples
    $check = Invoke-ProcessCheck -Name 'ui-structure-only' -Executable $powershell -Arguments $cmdargs `
        -WorkingDirectory $Source -TimeoutSeconds 120
    if ($check.status -ne 'PASS') { throw 'UIA_STRUCTURAL_PROBE_FAILED' }
    $jsonPath = Join-Path $script:WorkingRoot 'logs\ui-structure-only.stdout.txt'
    $raw = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    if ($raw.schema -ne 'suprachat-uia-apartment-differential/v1' -or $raw.captured_sensitive_content -ne $false) {
        throw 'UNEXPECTED_UIA_EVIDENCE_SHAPE'
    }
    $samples = @($raw.samples)
    if ($samples.Count -lt 1 -or $samples.Count -gt 4) { throw 'UIA_SAMPLE_BOUND_REFUSED' }
    $target = @($samples | Where-Object { $_.root_valid -and $_.findall_target -and $_.walker_target }).Count
    return [ordered]@{
        name='ui-structure-only';status=$(if ($target -gt 0) {'PASS'} else {'FAIL'});
        samples=$samples.Count;root_valid=@($samples | Where-Object { $_.root_valid }).Count;
        tab_sessions_both_views=$target;structure_evidence_sha256=(Get-FileDigest $jsonPath);
        contains_user_content=$false;
        account_identity_verified=$false;authenticated_ui_status='NOT_ESTABLISHED_BY_STRUCTURAL_UIA'
    }
}
function Get-ResultsDigest($Results) {
    $json = $Results | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$json)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Assert-LocalResults($Summary) {
    if ($Summary.tests_digest_sha256 -ne (Get-ResultsDigest @($Summary.results))) {
        throw 'LOCAL_RESULT_DIGEST_DRIFT'
    }
    foreach ($item in @($Summary.results)) {
        if ($item.name -eq 'go-toolchain') { continue }
        if ($item.name -eq 'ui-structure-only') {
            $path = Join-Path $script:WorkingRoot 'logs\ui-structure-only.stdout.txt'
            if (-not (Test-Path -LiteralPath $path) -or
                (Get-FileDigest $path) -ne $item.structure_evidence_sha256) {
                throw 'LOCAL_UIA_EVIDENCE_DRIFT'
            }
            continue
        }
        foreach ($stream in @('stdout','stderr')) {
            $path = Join-Path $script:WorkingRoot ('logs\'+$item.name+'.'+$stream+'.txt')
            $expected = [string]$item.($stream+'_sha256')
            if (-not (Test-Path -LiteralPath $path) -or
                (Get-FileDigest $path) -ne $expected) {
                throw 'LOCAL_TEST_EVIDENCE_DRIFT'
            }
        }
    }
}
function Get-Plan {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $go = Get-Command go -ErrorAction SilentlyContinue
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    return [ordered]@{
        schema=$script:Schema;mode=$Mode;source_sha=$SourceSha;run_label=$RunLabel;
        driver_sha256=$script:SelfDigest;working_root=$script:WorkingRoot;
        tools_disposable_only=$true;existing_dotnet_available=[bool]$dotnet;existing_go_available=[bool]$go;
        existing_gh_available=[bool]$gh;private_dle_auth='REQUIRES gh auth or GH_TOKEN/GITHUB_TOKEN';
        model_inference_required=$false;chatgpt_login_automated=$false;
        app_profile_isolation='NOT_SUPPORTED_BY_ACCEPTED_SOURCE';
        local_appdata_webview_untouched=$true;
        actions=@('READ private DLE kickoff','DOWNLOAD exact public commit into sandbox',
          'BUILD and TEST deterministic suites','VERIFY allowlisted outcomes',
          'POST metadata-only result and next baton to private DLE');
        no_global_installs=$true;no_repo_writes=$true;no_profile_cookies_or_prompts=$true
    }
}

# The entrypoint always resolves from user home and the script's actual path.
if ($env:OS -ne 'Windows_NT') { throw 'WINDOWS_ONLY_LOCAL_RDTE' }
$SourceSha = $SourceSha.ToLowerInvariant()
if (-not $PSCommandPath -or -not (Test-Path -LiteralPath $PSCommandPath)) { throw 'SCRIPT_PATH_REQUIRED' }
$script:SelfDigest = Get-FileDigest $PSCommandPath
$defaultRoot = Join-Path $HOME ('.local\share\suprachat-rdte\' + $RunLabel + '-' + $SourceSha.Substring(0,12))
if (-not $SandboxRoot) { $SandboxRoot = $defaultRoot }
$script:WorkingRoot = Assert-AbsoluteSafePath $SandboxRoot
Test-ReparseComponents $script:WorkingRoot
$manifestPath = Join-Path $script:WorkingRoot 'manifest.json'
$ownerPath = Join-Path $script:WorkingRoot '.suprachat-rdte-owned'
$summaryPath = Join-Path $script:WorkingRoot 'summary.json'
$ownerValue = $script:Schema + '|' + $RunLabel + '|' + $SourceSha + '|' + $script:SelfDigest

if ($Mode -eq 'Plan') {
    Get-Plan | ConvertTo-Json -Depth 7
    return
}
if ($Mode -eq 'SelfTest') {
    if (-not $script:WorkingRoot.StartsWith([IO.Path]::GetFullPath((Join-Path $HOME '.local\share\suprachat-rdte')),
            [StringComparison]::OrdinalIgnoreCase)) { throw 'SELFTEST_BOUNDARY_FAIL' }
    if ($script:SelfDigest -notmatch '^[a-f0-9]{64}$') { throw 'SELFTEST_DIGEST_FAIL' }
    [ordered]@{schema=$script:Schema;selftest='PASS';network_used=$false;files_changed=$false} |
        ConvertTo-Json -Compress
    return
}
if ($Mode -eq 'Undo') {
    if (-not (Test-Path -LiteralPath $script:WorkingRoot)) {
        'UNDO_ALREADY_CLEAN_NO_CHANGES'; return
    }
    if (-not (Test-Path -LiteralPath $ownerPath) -or
        (Get-Content -LiteralPath $ownerPath -Raw).Trim() -ne $ownerValue) {
        throw 'UNOWNED_SANDBOX_REFUSED'
    }
    # A crashed or partial run must first publish a sanitized interrupted baton.
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw 'INTERRUPTED_RUN_MUST_PUBLISH_BEFORE_UNDO'
    }
    $summary = Read-Json $summaryPath
    Assert-LocalResults $summary
    if ($summary.dle_post -ne 'PUBLISHED' -and $summary.dle_post -ne 'ALREADY_PUBLISHED') {
        throw 'DLE_RESULT_NOT_PUBLISHED_UNDO_REFUSED'
    }
    $dle = @(Get-DleComments)
    Assert-Kickoff $dle $SourceSha $script:SelfDigest
    if (@($dle | Where-Object { $_.body -and ([string]$_.body).Contains([string]$summary.result_marker) }).Count -eq 0) {
        throw 'DLE_RESULT_READBACK_REQUIRED_BEFORE_UNDO'
    }
    Test-ReparseComponents $script:WorkingRoot
    Remove-Item -LiteralPath $script:WorkingRoot -Recurse -Force
    'UNDO_COMPLETED_ONLY_OWNED_DISPOSABLE_ROOT'
    return
}
if (-not (Test-Path -LiteralPath $script:WorkingRoot)) {
    if ($Mode -ne 'Apply' -and $Mode -ne 'UiReadOnly') { throw 'NO_PREVIOUS_RUN' }
    New-Item -ItemType Directory -Path $script:WorkingRoot -Force | Out-Null
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($ownerPath, $ownerValue, $utf8)
} elseif (-not (Test-Path -LiteralPath $ownerPath) -or
    (Get-Content -LiteralPath $ownerPath -Raw).Trim() -ne $ownerValue) {
    throw 'SANDBOX_OWNERSHIP_MISMATCH_REFUSED'
}
$lockPath = Join-Path $script:WorkingRoot '.exclusive.lock'
$lock = [IO.FileStream]::new($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $comments = @(Get-DleComments)
    Assert-Kickoff $comments $SourceSha $script:SelfDigest
    if ($Mode -eq 'Verify' -or $Mode -eq 'Publish') {
        if ($Mode -eq 'Publish' -and -not (Test-Path -LiteralPath $summaryPath) -and
            (Test-Path -LiteralPath $manifestPath)) {
            $marker = $script:ResultMarker + ' RUN_LABEL=' + $RunLabel + ' SOURCE_SHA=' +
                $SourceSha + ' DRIVER_SHA256=' + $script:SelfDigest
            $comment = '## Local SupraChat interrupted RDTE return baton' + "`n`n" + $marker +
                "`n`n**Outcome:** INCOMPLETE_UNVERIFIED. Test phase interrupted before a safe receipt. " +
                'No test or account success claimed. Preserve DLE failure state; reconcile before retry.'
            Save-JsonAtomic $summaryPath ([ordered]@{schema=$script:Schema;run_label=$RunLabel;
                source_sha=$SourceSha;driver_sha256=$script:SelfDigest;outcome='INCOMPLETE_UNVERIFIED';
                results=@();tests_digest_sha256=(Get-ResultsDigest @());
                result_marker=$marker;dle_comment=$comment;dle_post='PENDING'})
        }
        $summary = Read-Json $summaryPath
        if ($summary.driver_sha256 -ne $script:SelfDigest -or $summary.source_sha -ne $SourceSha) {
            throw 'SUMMARY_DRIVER_OR_SOURCE_DRIFT'
        }
        Assert-LocalResults $summary
        $sourceMarker = Join-Path $script:WorkingRoot 'source\.rdte-source-sha'
        if ($summary.outcome -ne 'INCOMPLETE_UNVERIFIED' -and
            (-not (Test-Path -LiteralPath $sourceMarker) -or
            (Get-Content -LiteralPath $sourceMarker -Raw).Trim() -ne $SourceSha)) {
            throw 'LOCAL_SOURCE_PROVENANCE_DRIFT'
        }
        $marker = [string]$summary.result_marker
        if ($Mode -eq 'Publish' -or $summary.dle_post -notin @('PUBLISHED','ALREADY_PUBLISHED')) {
            $comment = [string]$summary.dle_comment
            $post = Post-DleOnce $comment $marker
            $summary.dle_post = $post
            Save-JsonAtomic $summaryPath $summary
        }
        if (@(Get-DleComments | Where-Object { $_.body -and ([string]$_.body).Contains($marker) }).Count -eq 0) {
            throw 'DLE_BIDIRECTIONAL_READBACK_MISSING'
        }
        [ordered]@{schema=$script:Schema;run_label=$RunLabel;
            phase='VERIFY';dle_post='CONFIRMED';outcome=$summary.outcome;results=$summary.results} |
            ConvertTo-Json -Depth 6
        return
    }
    if ((Test-Path -LiteralPath $summaryPath) -and -not $Repeat) {
        $prior = Read-Json $summaryPath
        if ($prior.source_sha -ne $SourceSha -or $prior.driver_sha256 -ne $script:SelfDigest) {
            throw 'EXISTING_RUN_PROVENANCE_MISMATCH'
        }
        Assert-LocalResults $prior
        $id = [string]$prior.result_marker
        if ($prior.dle_post -notin @('PUBLISHED','ALREADY_PUBLISHED') -or
            @($comments | Where-Object { $_.body -and ([string]$_.body).Contains($id) }).Count -eq 0) {
            throw 'PRIOR_RESULT_REQUIRES_PUBLISH_NOT_REEXECUTION'
        }
        [ordered]@{schema=$script:Schema;run_label=$RunLabel;
            outcome=$prior.outcome;dle_post='ALREADY_PUBLISHED';idempotent_replay=$true;
            no_tests_repeated=$true} | ConvertTo-Json -Compress
        return
    }
    if ((Test-Path -LiteralPath $summaryPath) -and $Repeat) {
        throw 'REPEAT_REQUIRES_NEW_RUN_LABEL_TO_PRESERVE_CUSTODY'
    }
    if (Test-Path -LiteralPath $manifestPath) {
        throw 'INTERRUPTED_PRIOR_RUN_USE_PUBLISH_THEN_UNDO_OR_NEW_LABEL'
    }
    $manifest = [ordered]@{schema=$script:Schema;run_label=$RunLabel;source_sha=$SourceSha;
        driver_sha256=$script:SelfDigest;phase='OBSERVED';prepared_utc=[DateTime]::UtcNow.ToString('o');
        account_browser_launched=$false;credentials_read=$false;local_appdata_modified=$false;
        public_worktree_modified=$false}
    Save-JsonAtomic $manifestPath $manifest
    $source = Get-PinnedSource $SourceSha
    $manifest.phase='PLANNED'; Save-JsonAtomic $manifestPath $manifest
    $results = @()
    if ($Mode -eq 'Apply') {
        $manifest.phase='APPLYING'; Save-JsonAtomic $manifestPath $manifest
        Set-PrivateTestEnvironment
        try {
            $dotnet=Get-DotNetTool
            $go=Get-GoTool
            $results = @(Invoke-DeterministicSuites $source $dotnet $go)
        } finally { Restore-TestEnvironment }
    } elseif ($Mode -eq 'UiReadOnly') {
        $manifest.phase='READONLY_UIA'; Save-JsonAtomic $manifestPath $manifest
        $results=@(Invoke-UiReadOnly $source $TargetProcessId $UiSamples)
    } else { throw 'INVALID_PHASE' }
    $failed = @($results | Where-Object { $_.status -eq 'FAIL' -or $_.status -eq 'TIMEOUT' })
    $outcome = if ($failed.Count -gt 0) {'FAIL'} else {'PASS_WITH_NOT_RUN_EXPLICIT'}
    if (@($results | Where-Object { $_.status -eq 'PASS' }).Count -eq $results.Count) { $outcome='PASS' }
    $marker = $script:ResultMarker + ' RUN_LABEL=' + $RunLabel + ' SOURCE_SHA=' + $SourceSha +
        ' DRIVER_SHA256=' + $script:SelfDigest
    $names = @($results | ForEach-Object { $_.name + ':' + $_.status }) -join ', '
    $testsDigest = Get-ResultsDigest $results
    $comment = @(
        '## Local SupraChat model-free RDTE return baton',
        '',
        $marker,
        '',
        ('**Outcome:** ' + $outcome),
        ('**Source:** `' + $SourceSha + '`; **driver:** `' + $script:SelfDigest + '`'),
        ('**Test summaries:** ' + $names),
        ('**Local test-result digest (SHA256):** `' + $testsDigest + '`'),
        '',
        'No login automation, credential extraction, cookies, conversation content, screenshots, or raw UIA tree.',
        'Local authorized app state was neither created nor modified by this driver.',
        'Testing used an exact public source snapshot and user-owned disposable .local storage.',
        '',
        '**Local -> remote handoff:** inspect these deterministic statuses, reconcile against public source and private DLE,',
        'triage any failed check in the owning issue, and supply the next scoped test/corrective baton.',
        '**Remote -> local handoff:** next invocation must read the current private DLE kickoff before running.',
        'No acceptance, merge, release, live MCP connector creation or authenticated account test implied.'
    ) -join "`n"
    $summary = [ordered]@{schema=$script:Schema;run_label=$RunLabel;source_sha=$SourceSha;
        driver_sha256=$script:SelfDigest;mode=$Mode;outcome=$outcome;
        results=$results;tests_digest_sha256=$testsDigest;
        result_marker=$marker;dle_comment=$comment;dle_post='PENDING';
        run_utc=[DateTime]::UtcNow.ToString('o')}
    Save-JsonAtomic $summaryPath $summary
    $post=Post-DleOnce $comment $marker
    $summary.dle_post=$post; Save-JsonAtomic $summaryPath $summary
    $manifest.phase='VERIFIED_WITH_DLE_READBACK';Save-JsonAtomic $manifestPath $manifest
    [ordered]@{schema=$script:Schema;run_label=$RunLabel;outcome=$outcome;
        dle_post=$post;test_statuses=@($results | ForEach-Object { $_.name+':'+$_.status });
        evidence_root=$script:WorkingRoot} | ConvertTo-Json -Depth 8
    if ($outcome -eq 'FAIL') { throw 'RDTE_TEST_FAILURE_EVIDENCE_POSTED' }
} finally {
    $lock.Dispose()
}
