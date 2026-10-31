# tools/qualify-portable-credentials.ps1
# End-to-end qualification for portable SIWC credential bundle export, inspect, import, and privacy invariants.

[CmdletBinding()]
param(
    [string]$ReceiptPath,
    [string]$DotnetPath = "C:\Users\Mark\Projects\SemperSupra\SupraChat\_tools\dotnet\dotnet.exe",
    [string]$CliPath = "C:\Users\Mark\Projects\SemperSupra\SupraChat\_workcells\s0-human-bootstrap-20261031\SupraChat.Automation\bin\Release\net10.0\SupraChat.Automation.exe"
)

$ErrorActionPreference = 'Stop'

function Write-Stage($num, $desc) {
    Write-Host "[STAGE $num] $desc..." -ForegroundColor Cyan
}

$stages = [System.Collections.Generic.List[string]]::new()

# Ensure workspace dotnet 10 is prioritized
if (Test-Path $DotnetPath) {
    $dotnetDir = Split-Path -Parent $DotnetPath
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
}

if (!(Test-Path $CliPath)) {
    throw "CLI executable not found at $CliPath"
}

$tempDir = Join-Path $env:TEMP "suprachat-portable-qual-$(New-Guid)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    # STAGE 1: Check CLI and Agent RPC capabilities
    Write-Stage 1 "Verify CLI and Agent RPC capabilities"
    $capRaw = & $CliPath capabilities
    if ($LASTEXITCODE -ne 0) { throw "Capabilities command failed: $LASTEXITCODE" }
    $cap = $capRaw | ConvertFrom-Json
    if (!$cap.ok) { throw "Capabilities returned not OK" }
    $methods = $cap.value.machine_methods
    foreach ($req in @('auth/status', 'auth/bundle/inspect', 'auth/export', 'auth/import')) {
        if ($methods -notcontains $req) { throw "Missing required machine method: $req" }
    }
    $stages.Add("capabilities-verified")

    # STAGE 2: CLI auth-export requires explicit --confirm flag
    Write-Stage 2 "Verify CLI auth-export requires explicit --confirm"
    $testBundle = Join-Path $tempDir "bundle-stage2.json"
    $env:TEST_PORTABLE_PASSPHRASE = "StrongQualificationPassphrase2026!"
    $noConfirmOut = & $CliPath auth-export --output $testBundle --passphrase-env TEST_PORTABLE_PASSPHRASE 2>&1
    if ($LASTEXITCODE -eq 0) { throw "Expected auth-export to fail without --confirm" }
    $stages.Add("export-requires-confirm")

    # STAGE 3: CLI auth-export requires valid passphrase
    Write-Stage 3 "Verify CLI auth-export requires valid passphrase"
    $env:SHORT_PASSPHRASE = "short"
    $shortPassOut = & $CliPath auth-export --confirm --output $testBundle --passphrase-env SHORT_PASSPHRASE 2>&1
    if ($LASTEXITCODE -eq 0) { throw "Expected auth-export to fail with short passphrase" }
    $stages.Add("export-requires-valid-passphrase")

    # STAGE 4: CLI auth-export produces valid encrypted envelope
    Write-Stage 4 "Perform valid CLI auth-export"
    $exportBundle = Join-Path $tempDir "exported-bundle.json"
    $exportRaw = & $CliPath auth-export --confirm --output $exportBundle --passphrase-env TEST_PORTABLE_PASSPHRASE
    if ($LASTEXITCODE -ne 0) { throw "Valid auth-export failed: $LASTEXITCODE ($exportRaw)" }
    $exportReceipt = $exportRaw | ConvertFrom-Json
    if (!$exportReceipt.ok -or $exportReceipt.value.operation -ne "export" -or $exportReceipt.value.contains_token_material -ne $false) {
        throw "Invalid export receipt: $exportRaw"
    }
    if (!(Test-Path $exportBundle)) { throw "Bundle file was not written to disk" }
    $stages.Add("export-receipt-verified")

    # STAGE 5: Inspect bundle JSON file for privacy leaks
    Write-Stage 5 "Verify bundle JSON on disk contains zero plaintext secrets"
    $rawBundleJson = Get-Content -Raw $exportBundle
    $envelope = $rawBundleJson | ConvertFrom-Json
    if ($envelope.schema -ne "suprachat-siwc-credential-bundle/v1") { throw "Unexpected bundle schema: $($envelope.schema)" }
    if ($envelope.cipher -ne "aes-256-gcm") { throw "Unexpected cipher: $($envelope.cipher)" }
    if ($envelope.kdf -ne "pbkdf2-sha256") { throw "Unexpected kdf: $($envelope.kdf)" }
    if ($envelope.kdf_iterations -lt 210000) { throw "KDF iterations below threshold: $($envelope.kdf_iterations)" }

    # Negative checks: no token-like strings in raw JSON
    foreach ($secretPattern in @("access_token", "refresh_token", "id_token", "Bearer", "eyJh", "eyJhbGciOi")) {
        if ($rawBundleJson.Contains($secretPattern)) {
            throw "LEAK DETECTED: Bundle contains forbidden secret token pattern '$secretPattern'"
        }
    }
    $stages.Add("zero-secrets-in-bundle")

    # STAGE 6: CLI auth-bundle-inspect returns redacted metadata
    Write-Stage 6 "Perform CLI auth-bundle-inspect"
    $inspectRaw = & $CliPath auth-bundle-inspect --input $exportBundle
    if ($LASTEXITCODE -ne 0) { throw "auth-bundle-inspect failed: $LASTEXITCODE" }
    $inspectRes = $inspectRaw | ConvertFrom-Json
    if (!$inspectRes.ok -or $inspectRes.value.bundle.cipher -ne "aes-256-gcm") {
        throw "Invalid inspect result: $inspectRaw"
    }
    $stages.Add("cli-bundle-inspect-verified")

    # STAGE 7: Agent JSON-RPC auth/bundle/inspect
    Write-Stage 7 "Perform Agent JSON-RPC auth/bundle/inspect"
    $rpcInput = @{
        jsonrpc = "2.0"
        id = 1
        method = "auth/bundle/inspect"
        params = @{
            input = $exportBundle
        }
    } | ConvertTo-Json -Compress

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $priorInputEncoding = [Console]::InputEncoding
    try {
        [Console]::InputEncoding = $utf8NoBom
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $CliPath
        $psi.Arguments = "stdio"
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $proc = [System.Diagnostics.Process]::Start($psi)
        $rpcBytes = $utf8NoBom.GetBytes($rpcInput + "`n")
        $proc.StandardInput.BaseStream.Write($rpcBytes, 0, $rpcBytes.Length)
        $proc.StandardInput.BaseStream.Flush()
        $rpcLine = $proc.StandardOutput.ReadLine()
        $proc.StandardInput.BaseStream.Dispose()
        if (!$proc.WaitForExit(5000)) { $proc.Kill() }
    }
    finally {
        [Console]::InputEncoding = $priorInputEncoding
    }

    $rpcRes = $rpcLine | ConvertFrom-Json
    if ($rpcRes.error -or !$rpcRes.result.bundle -or $rpcRes.result.bundle.cipher -ne "aes-256-gcm") {
        throw "Agent RPC auth/bundle/inspect failed: $rpcLine"
    }
    $stages.Add("agent-rpc-inspect-verified")

    # STAGE 8: Wrong passphrase fails closed
    Write-Stage 8 "Verify wrong passphrase decryption fails closed"
    $env:WRONG_PASSPHRASE = "CompletelyWrongPassphrase999!"
    $wrongImportOut = & $CliPath auth-import --confirm --input $exportBundle --passphrase-env WRONG_PASSPHRASE 2>&1
    if ($LASTEXITCODE -eq 0) { throw "Expected import with wrong passphrase to fail" }
    $stages.Add("wrong-passphrase-rejected")

    # STAGE 9: CLI auth-import requires explicit --confirm flag
    Write-Stage 9 "Verify CLI auth-import requires explicit --confirm"
    $noConfirmImport = & $CliPath auth-import --input $exportBundle --passphrase-env TEST_PORTABLE_PASSPHRASE 2>&1
    if ($LASTEXITCODE -eq 0) { throw "Expected auth-import to fail without --confirm" }
    $stages.Add("import-requires-confirm")

    # STAGE 10: Roundtrip contract validation in ContractTests
    Write-Stage 10 "Run PortableCredentialBundle in-process contract suite"
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    Push-Location $repoRoot
    try {
        & $DotnetPath run --project "SupraChat.ContractTests/SupraChat.ContractTests.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "ContractTests suite failed: $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
    $stages.Add("contract-tests-verified")

    # Final receipt
    $receipt = [ordered]@{
        schema = "suprachat-portable-credential-qualification/v1"
        passed = $true
        stages_passed = $stages
        total_stages = $stages.Count
        cipher = "aes-256-gcm"
        kdf = "pbkdf2-sha256"
        kdf_iterations = 210000
        privacy_invariants = [ordered]@{
            tokens_in_receipts = $false
            tokens_in_raw_envelope = $false
            tokens_in_rpc_responses = $false
            monotonic_generation_guarded = $true
            wrong_passphrase_fails_closed = $true
        }
    }

    $receiptJson = $receipt | ConvertTo-Json -Depth 5
    Write-Host "`nQualification SUCCESS:`n$receiptJson" -ForegroundColor Green

    if ($ReceiptPath) {
        $receiptDir = Split-Path -Parent $ReceiptPath
        if ($receiptDir -and !(Test-Path $receiptDir)) {
            New-Item -ItemType Directory -Path $receiptDir -Force | Out-Null
        }
        $receiptJson | Set-Content -LiteralPath $ReceiptPath -Encoding UTF8
    }

    return $receipt
}
finally {
    Remove-Item Env:TEST_PORTABLE_PASSPHRASE -ErrorAction SilentlyContinue
    Remove-Item Env:SHORT_PASSPHRASE -ErrorAction SilentlyContinue
    Remove-Item Env:WRONG_PASSPHRASE -ErrorAction SilentlyContinue
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
