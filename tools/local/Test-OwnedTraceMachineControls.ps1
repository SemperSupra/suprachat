param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$traceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$traceCli = Join-Path $traceRoot 'SupraChat.Automation/bin/Release/net10.0/SupraChat.Automation.dll'
$traceOperations = @('status', 'start', 'read', 'stop', 'export')
$traceCommands = @($traceOperations | ForEach-Object { @{ operation = $_ } | ConvertTo-Json -Compress })
$traceCommands += '{"operation":"read","params":{"session_id":"00000000-0000-0000-0000-000000000000"}}'
$traceLines = @($traceCommands | & $DotNet $traceCli trace-session)
if ($LASTEXITCODE -ne 0 -or $traceLines.Count -ne 6) { throw 'Trace automation framing failed.' }
$traceResults = @($traceLines | ForEach-Object { $_ | ConvertFrom-Json })
if ($traceResults[0].value.mode -ne 'OFF' -or $traceResults[1].value.mode -ne 'METADATA' -or
    $traceResults[2].value.scope -ne 'SINGLE_OPERATOR_PROCESS_LOCAL_OWNED_TRANSPORTS' -or
    $traceResults[3].value.mode -ne 'OFF' -or $traceResults[4].value.operation -ne 'export' -or
    !$traceResults[4].value.capture_gap -or $traceResults[5].ok) {
    throw 'Trace automation state/scope/foreign-session refusal failed.'
}
$traceRpc = @()
for ($traceIndex = 0; $traceIndex -lt $traceOperations.Count; $traceIndex++) {
    $traceRpc += @{ jsonrpc = '2.0'; id = $traceIndex; method = 'trace/' + $traceOperations[$traceIndex] } | ConvertTo-Json -Compress
}
$traceRpc += '{"jsonrpc":"2.0","id":5,"method":"trace/read","params":{"actor_principal":"forged"}}'
$traceAgentLines = @($traceRpc | & $DotNet $traceCli stdio)
if ($LASTEXITCODE -ne 0 -or $traceAgentLines.Count -ne 6) { throw 'Trace agent framing failed.' }
$traceAgentResults = @($traceAgentLines | ForEach-Object { $_ | ConvertFrom-Json })
if ($traceAgentResults[0].result.mode -ne 'OFF' -or $traceAgentResults[1].result.mode -ne 'METADATA' -or
    $traceAgentResults[2].result.scope -ne $traceResults[2].value.scope -or
    $traceAgentResults[3].result.mode -ne 'OFF' -or $traceAgentResults[4].result.operation -ne 'export' -or
    !$traceAgentResults[5].error) {
    throw 'Trace agent state/scope/authority refusal failed.'
}
'Owned trace CLI/agent semantic controls PASS (synthetic; no credentials).'
