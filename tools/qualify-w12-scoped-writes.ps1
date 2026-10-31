param(
    [string]$StorePath = "$env:TEMP/w12-scoped-$([guid]::NewGuid().ToString('N')).sqlite",
    [string]$ReceiptPath = "$env:TEMP/w12-receipt-$([guid]::NewGuid().ToString('N')).json"
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cli = Join-Path $root 'SupraChat.Automation/bin/Release/net10.0/SupraChat.Automation.dll'
$dotnet = (Get-Command dotnet).Source

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$bearer = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$keyBytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($keyBytes)
$signingKeyBase64 = [Convert]::ToBase64String($keyBytes)

function Create-Grant([string]$actor, [string]$audience, [string]$session, [string[]]$scopes, [int]$ttl = 300) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $claims = @{
        Schema = 'suprachat-w12-host-metadata-grant-rdte/v1'
        ActorId = $actor
        Audience = $audience
        SessionId = $session
        Scopes = $scopes
        IssuedAt = $now
        ExpiresAt = $now + $ttl
        GrantId = [guid]::NewGuid().ToString('N')
    }
    $claimsJson = $claims | ConvertTo-Json -Compress
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes($claimsJson)
    $payloadB64 = [Convert]::ToBase64String($payloadBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
    
    $hmac = [Security.Cryptography.HMACSHA256]::new($keyBytes)
    $sigBytes = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payloadB64))
    $sigB64 = [Convert]::ToBase64String($sigBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
    return "$payloadB64.$sigB64"
}

$hostEnv = @{
    SUPRACHAT_LEDGER_TOKEN = $bearer
    SUPRACHAT_LEDGER_STORE = $StorePath
    SUPRACHAT_W6_DISPOSABLE_ONLY = 'yes'
    SUPRACHAT_W9_DISPOSABLE_ONLY = 'yes'
    SUPRACHAT_W9_CREATE_ONCE = 'no'
    SUPRACHAT_W12_SCOPED_WRITES = 'synthetic'
    SUPRACHAT_W12_SIGNING_KEY = $signingKeyBase64
}

function Launch-Host([bool]$initial = $false) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $dotnet
    $psi.Arguments = ('exec "{0}" session-ledger-host --port {1}' -f $cli, $port)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    
    foreach ($k in $hostEnv.Keys) {
        $psi.EnvironmentVariables[$k] = $hostEnv[$k]
    }
    if ($initial) {
        $psi.EnvironmentVariables['SUPRACHAT_W9_CREATE_ONCE'] = 'yes'
    }

    $p = [Diagnostics.Process]::Start($psi)
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    $ready = $false
    while ([datetime]::UtcNow -lt $deadline) {
        if ($p.HasExited) {
            $err = $p.StandardError.ReadToEnd()
            $out = $p.StandardOutput.ReadToEnd()
            throw "HOST_EXITED_EARLY: code $($p.ExitCode); err: $err; out: $out"
        }
        try {
            $h = Invoke-RestMethod "http://127.0.0.1:$port/v1/health" -Headers @{Authorization = "Bearer $bearer"} -TimeoutSec 2
            if ($h.schema -eq 'suprachat-w6-loopback-session-host/v1') {
                $ready = $true
                break
            }
        } catch { Start-Sleep -Milliseconds 150 }
    }
    if (!$ready) { throw "HOST_TIMEOUT" }
    return $p
}

function Request-Host([string]$method, [string]$path, $body = $null, [string]$grant = $null, [string]$token = $bearer) {
    $headers = @{ Authorization = "Bearer $token" }
    if ($grant) { $headers['X-SupraChat-Actor-Capability'] = $grant }
    
    $uri = "http://127.0.0.1:$port$path"
    $bodyJson = if ($body) { $body | ConvertTo-Json -Compress } else { $null }
    
    try {
        $params = @{
            Uri = $uri
            Method = $method
            Headers = $headers
            TimeoutSec = 5
        }
        if ($bodyJson) {
            $params['Body'] = $bodyJson
            $params['ContentType'] = 'application/json'
        }
        $resp = Invoke-RestMethod @params
        return @{ StatusCode = 200; Body = $resp }
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 500 }
        $raw = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        $body = try { $raw | ConvertFrom-Json } catch { $raw }
        return @{ StatusCode = $status; Body = $body }
    }
}

$proc = $null
$stages = [Collections.Generic.List[string]]::new()

try {
    Write-Host "Launching initial W12 host on port $port..."
    $proc = Launch-Host -initial $true
    
    # Stage 1: Health check reveals scoped write enforcement
    $h = (Request-Host 'GET' '/v1/health').Body
    if ($h.synthetic_scoped_write_enforcement -ne $true -or
        $h.common_bearer_grants_mutation -ne $false) {
        throw "HEALTH_ADVERTISEMENT_MISMATCH"
    }
    $stages.Add("health-scoped-write-advertised")
    Write-Host "  ✓ health advertising synthetic_scoped_write_enforcement"

    # Stage 2: Common bearer mutation without grant is rejected (403)
    $unauthOpen = Request-Host 'POST' '/v1/sessions/open' @{
        idempotencyKey = 'open-op-0000001'
        audience = 'human'
        title = 'Session without grant'
    }
    if ($unauthOpen.StatusCode -ne 403) { throw "COMMON_BEARER_MUTATION_NOT_REJECTED: $($unauthOpen.StatusCode)" }
    $stages.Add("common-bearer-mutation-denied")
    Write-Host "  ✓ common bearer mutation denied without grant (403)"

    # Stage 3: Spoofed audience in body vs grant claims is rejected (403)
    $grantActorHuman = Create-Grant 'actor-01' 'human' 'create-only' @('session.create')
    $spoofedOpen = Request-Host 'POST' '/v1/sessions/open' @{
        idempotencyKey = 'open-op-0000002'
        audience = 'agent' # claiming agent, but grant says human
        title = 'Spoofed session'
    } -grant $grantActorHuman
    if ($spoofedOpen.StatusCode -ne 403) { throw "SPOOFED_AUDIENCE_NOT_REJECTED" }
    $stages.Add("spoofed-audience-denied")
    Write-Host "  ✓ spoofed body audience denied (403)"

    # Stage 4: Valid create-only grant opens session
    $validOpen = Request-Host 'POST' '/v1/sessions/open' @{
        idempotencyKey = 'open-op-0000003'
        audience = 'human'
        title = 'First Scoped Session'
    } -grant $grantActorHuman
    if ($validOpen.StatusCode -ne 200 -or !$validOpen.Body.applied) { throw "VALID_OPEN_FAILED" }
    $sidA = $validOpen.Body.session.id
    $stages.Add("valid-open-applied")
    Write-Host "  ✓ session opened with signed grant: $sidA"

    # Stage 5: Mutation on sidA with valid session.mutate grant succeeds
    $grantMutateA = Create-Grant 'actor-agent' 'agent' $sidA @('session.mutate')
    $mutA = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000001'
        audience = 'agent'
        sessionId = $sidA
        expectedVersion = 0
        operation = 'set-title'
        value = 'Agent Updated Title'
    } -grant $grantMutateA
    if ($mutA.StatusCode -ne 200 -or !$mutA.Body.applied -or $mutA.Body.session.version -ne 1) {
        throw "VALID_MUTATE_FAILED"
    }
    $stages.Add("valid-mutate-applied")
    Write-Host "  ✓ session mutated to version 1 with actor grant"

    # Stage 6: Wrong session grant is rejected
    $sidB_fake = [guid]::NewGuid().ToString('N')
    $wrongSessionMut = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000002'
        audience = 'agent'
        sessionId = $sidB_fake
        expectedVersion = 0
        operation = 'set-title'
        value = 'Illegal Session'
    } -grant $grantMutateA
    if ($wrongSessionMut.StatusCode -ne 403) { throw "WRONG_SESSION_NOT_REJECTED" }
    $stages.Add("wrong-session-denied")
    Write-Host "  ✓ wrong session capability rejected (403)"

    # Stage 7: Cross-actor idempotency isolation
    # Another actor uses same idempotency key 'mut-op-0000001' on sidA
    $grantOtherAgent = Create-Grant 'actor-agent-2' 'agent' $sidA @('session.mutate')
    $crossKeyMut = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000001' # identical key
        audience = 'agent'
        sessionId = $sidA
        expectedVersion = 0 # version 0 is stale now, should return 409 conflict, NOT replay of actor 1!
        operation = 'set-title'
        value = 'Other Agent Title'
    } -grant $grantOtherAgent
    if ($crossKeyMut.StatusCode -ne 409 -or $crossKeyMut.Body.isReplay -ne $false) {
        throw "CROSS_ACTOR_KEY_NOT_ISOLATED"
    }
    $stages.Add("cross-actor-idempotency-isolated")
    Write-Host "  ✓ cross-actor idempotency keys isolated by actor ID"

    # Stage 8: Self-revocation of own grant
    $revokeResp = Request-Host 'POST' '/v1/grants/revoke-self' @{
        sessionId = $sidA
        audience = 'agent'
    } -grant $grantMutateA
    if ($revokeResp.StatusCode -ne 200 -or $revokeResp.Body.status -ne 'REVOKED') {
        throw "SELF_REVOCATION_FAILED"
    }
    $stages.Add("self-revocation-applied")
    Write-Host "  ✓ self-revocation returned REVOKED"

    # Stage 9: Using revoked grant now fails with 403
    $useRevoked = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000003'
        audience = 'agent'
        sessionId = $sidA
        expectedVersion = 1
        operation = 'set-title'
        value = 'Revoked Post'
    } -grant $grantMutateA
    if ($useRevoked.StatusCode -ne 403) { throw "REVOKED_GRANT_NOT_REJECTED: $($useRevoked.StatusCode)" }
    $stages.Add("revoked-grant-rejected")
    Write-Host "  ✓ mutation with revoked grant rejected (403)"

    # Stage 10: Second revoke call returns ALREADY_REVOKED
    $secondRevoke = Request-Host 'POST' '/v1/grants/revoke-self' @{
        sessionId = $sidA
        audience = 'agent'
    } -grant $grantMutateA
    if ($secondRevoke.StatusCode -ne 200 -or $secondRevoke.Body.status -ne 'ALREADY_REVOKED') {
        throw "ALREADY_REVOKED_FAILED"
    }
    $stages.Add("already-revoked-handled")
    Write-Host "  ✓ duplicate self-revocation returns ALREADY_REVOKED"

    # Stage 11: Kill process and restart against same SQLite store
    Write-Host "Stopping host process..."
    $proc.Kill()
    $proc.WaitForExit(5000)
    $proc = $null

    Write-Host "Restarting host on same store $StorePath..."
    $proc = Launch-Host -initial $false

    # Stage 12: Verify revoked grant is STILL rejected after host restart
    $useRevokedAfterRestart = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000004'
        audience = 'agent'
        sessionId = $sidA
        expectedVersion = 1
        operation = 'set-title'
        value = 'Post-Restart Revoked Attempt'
    } -grant $grantMutateA
    if ($useRevokedAfterRestart.StatusCode -ne 403) {
        throw "REVOKED_GRANT_NOT_DURABLE"
    }
    $stages.Add("durable-revocation-preserved-after-restart")
    Write-Host "  ✓ durable revocation preserved across restart (403)"

    # Stage 13: New grant for same actor can mutate
    $grantMutateA_v2 = Create-Grant 'actor-agent' 'agent' $sidA @('session.mutate')
    $mutA_v2 = Request-Host 'POST' '/v1/sessions/change' @{
        idempotencyKey = 'mut-op-0000005'
        audience = 'agent'
        sessionId = $sidA
        expectedVersion = 1
        operation = 'set-title'
        value = 'Recovered Agent Title'
    } -grant $grantMutateA_v2
    if ($mutA_v2.StatusCode -ne 200 -or !$mutA_v2.Body.applied -or $mutA_v2.Body.session.version -ne 2) {
        throw "NEW_GRANT_AFTER_REVOKE_FAILED"
    }
    $stages.Add("new-grant-after-revoke-progress")
    Write-Host "  ✓ new grant for same actor successfully progressed session to version 2"

    $receipt = @{
        schema = 'suprachat-w12-host-scoped-write-rdte/v1'
        result = 'PASS'
        stages = $stages
        signed_actor_grants_enforced = $true
        common_bearer_write_bypass_denied = $true
        same_operation_key_actor_isolated = $true
        restart_receipts_bound_to_signed_actor = $true
        os_peer_authenticated = $false
        trusted_issuer_enrolled = $false
        durable_self_revocation_available = $true
        durable_cross_actor_revocation_available = $false
        real_provider_calls = 0
        terminal_commands = 0
    }
    [IO.File]::WriteAllText($ReceiptPath, ($receipt | ConvertTo-Json -Depth 4))
    Write-Host "`nQUALIFICATION_SUCCESS: All $($stages.Count) stages passed!"
    Write-Host "Receipt saved to $ReceiptPath"
}
finally {
    if ($proc -and !$proc.HasExited) {
        $proc.Kill()
    }
    if (Test-Path $StorePath) {
        Remove-Item $StorePath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path "$StorePath-wal") {
        Remove-Item "$StorePath-wal" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path "$StorePath-shm") {
        Remove-Item "$StorePath-shm" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path "$StorePath.w9owner") {
        Remove-Item "$StorePath.w9owner" -Force -ErrorAction SilentlyContinue
    }
}
