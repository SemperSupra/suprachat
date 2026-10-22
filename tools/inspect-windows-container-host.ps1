#Requires -Version 7.0
<#
Read-only Windows containerization census. Does not enable features, install
Docker, edit policy, access credentials, start a VM, or inspect browser data.
For an existing Win11 developer workstation or disposable GHA Windows runner.
#>
[CmdletBinding()]
param([string]$Output)
$ErrorActionPreference = 'Stop'
$r = [ordered]@{
    schema = 'suprachat-windows-container-host-census/v1'
    observation = 'read-only'
    window_os = $IsWindows
    os_caption = $null
    os_build = $null
    os_arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    docker_cli_present = $false
    docker_daemon_reachable = $false
    docker_daemon_os = $null
    podman_cli_present = [bool](Get-Command podman -ErrorAction SilentlyContinue)
    nerdctl_cli_present = [bool](Get-Command nerdctl -ErrorAction SilentlyContinue)
    wsl_command_present = [bool](Get-Command wsl.exe -ErrorAction SilentlyContinue)
    wsl_distro_count = $null
    hypervisor_present = $null
    virtualization_firmware_enabled = $null
    windows_features = [ordered]@{}
    linux_image_usable_now = $false
    windows_image_usable_now = $false
    docker_desktop_install_scope = 'unknown'
    authorization_to_install_or_enable = $false
}
if ($IsWindows) {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $r.os_caption = [string]$os.Caption
        $r.os_build = [string]$os.BuildNumber
    } catch { }
    try {
        $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $r.hypervisor_present = [bool]$system.HypervisorPresent
    } catch { }
    try {
        $cpus = @(Get-CimInstance Win32_Processor -ErrorAction Stop)
        if ($cpus.Count -gt 0) {
            $r.virtualization_firmware_enabled = [bool]($cpus | Where-Object { $_.VirtualizationFirmwareEnabled }).Count
        }
    } catch { }
    foreach ($feature in @('Microsoft-Hyper-V-All','Containers','VirtualMachinePlatform',
            'Microsoft-Windows-Subsystem-Linux','HypervisorPlatform')) {
        try {
            $row = @(Get-CimInstance Win32_OptionalFeature -Filter "Name='$feature'" -ErrorAction Stop) |
                Select-Object -First 1
            if ($row) {
                $r.windows_features[$feature] = switch ([int]$row.InstallState) {
                    1 { 'enabled' }
                    2 { 'disabled' }
                    default { 'unknown' }
                }
            } else { $r.windows_features[$feature] = 'not-observed' }
        } catch { $r.windows_features[$feature] = 'not-observed' }
    }
    if ($r.wsl_command_present) {
        try {
            $raw = @(& wsl.exe --list --quiet 2>$null)
            if ($LASTEXITCODE -eq 0) {
                $r.wsl_distro_count = @($raw | Where-Object { ($_ -replace [char]0, '').Trim() }).Count
            }
        } catch { }
    }
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $r.docker_cli_present = $true
        try {
            $osType = ((& docker info --format '{{.OSType}}' 2>$null) | Out-String).Trim().ToLowerInvariant()
            if ($LASTEXITCODE -eq 0 -and $osType -in @('linux','windows')) {
                $r.docker_daemon_reachable = $true
                $r.docker_daemon_os = $osType
            }
        } catch { }
    }
    $r.linux_image_usable_now = $r.docker_daemon_reachable -and $r.docker_daemon_os -eq 'linux'
    $r.windows_image_usable_now = $r.docker_daemon_reachable -and $r.docker_daemon_os -eq 'windows'
}
$receipt = [pscustomobject]$r
$json = $receipt | ConvertTo-Json -Depth 6
$json | Write-Output
if ($Output) {
    $destination = [IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Set-Content -LiteralPath $destination -Value $json -Encoding utf8
}
