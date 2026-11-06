#requires -Version 5.1
# Deterministic native Windows PowerShell 5.1 contract tests; no network, secrets, or filesystem writes.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReceiptContract.psm1') -Force

$script:assertions = 0
function Assert-Equal([object]$actual,[object]$expected,[string]$message) {
    if ($actual -cne $expected) { throw ("ASSERT: " + $message + "; actual=" + $actual + "; expected=" + $expected) }
    $script:assertions++
}
function Assert-Throws([scriptblock]$action,[string]$message) {
    $didThrow = $false
    try { & $action | Out-Null } catch { $didThrow = $true }
    if (-not $didThrow) { throw ("ASSERT: expected failure for " + $message) }
    $script:assertions++
}

$marker = 'SUPRA-RECEIPT/V1 RUN=fixture SHA=0000'
$body = $marker + "`n`nStatus: CENSUS_COMPLETE`nSHA256: " + ('a' * 64)
$valid = [pscustomobject]@{id=201;body=$body}
$embedded = [pscustomobject]@{id=202;body=('## Plan'+"`n"+$marker+"`nExample, not a result")}
$malformed = [pscustomobject]@{id=203;body=($marker+"`nStatus: COMPLETE")}
$conflict = [pscustomobject]@{id=204;body=($body + 'CHANGED')}

Assert-Equal (Get-SupraReceipt -Comments @($embedded) -Marker $marker -ExpectedBody $body) $null 'embedded marker must be ignored'
Assert-Equal (Get-SupraReceipt -Comments @($embedded,$valid) -Marker $marker -ExpectedBody $body).id 201 'exact receipt must be found'
Assert-Throws { Get-SupraReceipt -Comments @($malformed) -Marker $marker -ExpectedBody $body } 'malformed first line'
Assert-Throws { Get-SupraReceipt -Comments @($valid,$valid) -Marker $marker -ExpectedBody $body } 'duplicate receipts'
Assert-Throws { Get-SupraReceipt -Comments @($conflict) -Marker $marker -ExpectedBody $body } 'same marker different evidence'
Assert-Throws { Get-SupraReceipt -Comments @($valid) -Marker $marker -ExpectedBody ($body+'DIFFERENT') } 'different expected evidence'
Assert-Throws { Get-SupraReceipt -Comments @($valid) -Marker $marker -ExpectedBody ($marker+"`nStatus: COMPLETE") } 'invalid expected envelope'
Assert-Equal (Get-SupraReceipt -Comments @([pscustomobject]@{id=208;body=($marker+' EXTRA'+"`n`nStatus: COMPLETE")}) -Marker $marker -ExpectedBody $body) $null 'prefix matching must be rejected'
Assert-Equal (Get-SupraReceipt -Comments @([pscustomobject]@{id=209;body=("PREAMBLE`n"+$body)}) -Marker $marker -ExpectedBody $body) $null 'leading preamble must be ignored'
Assert-Equal (Get-SupraReceipt -Comments @([pscustomobject]@{id=210;body=($body.Replace("`n","`r`n"))}) -Marker $marker -ExpectedBody $body).id 210 'CRLF transport normalization'
# This is the exact class of PowerShell 5.1 bug in V6: regex writes automatic $Matches.
function Invoke-KnownBroken {
    $matches = New-Object System.Collections.ArrayList
    if ('Status: COMPLETE' -cmatch '^Status: (COMPLETE|INCOMPLETE)$') {
        [void]$matches.Add('receipt')
    }
}
Assert-Throws { Invoke-KnownBroken } 'known V6 $Matches collision reproduces on native host'
# The corrected module must still complete after a regex predicate populates automatic $Matches.
Assert-Equal (Get-SupraReceipt -Comments @($valid) -Marker $marker -ExpectedBody $body).id 201 'corrected collection survives regex matching'

$state = @{comments=(New-Object System.Collections.ArrayList);posts=0;reads=0;ambiguous=$false;emit=$true}
$read = {
    $state.reads++
    return @($state.comments.ToArray())
}.GetNewClosure()
$post = {
    param($payload)
    $state.posts++
    if ($state.emit) { [void]$state.comments.Add([pscustomobject]@{id=300;body=$payload}) }
    if ($state.ambiguous) { throw 'synthetic POST response lost after server committed' }
}.GetNewClosure()

$result = Invoke-SupraReceiptDelivery -Marker $marker -Body $body -ReadComments $read -PostComment $post
Assert-Equal $result.status 'PUBLISHED' 'fresh post with independent readback'
Assert-Equal $state.posts 1 'one POST only'
$result = Invoke-SupraReceiptDelivery -Marker $marker -Body $body -ReadComments $read -PostComment $post
Assert-Equal $result.status 'ALREADY_VERIFIED' 'idempotent repeated execution'
Assert-Equal $state.posts 1 'NOOP never POSTs twice'

$state.comments.Clear()
$state.ambiguous=$true
$result=Invoke-SupraReceiptDelivery -Marker $marker -Body $body -ReadComments $read -PostComment $post
Assert-Equal $result.status 'POST_UNCERTAIN_BUT_VERIFIED' 'ambiguous POST readback'
Assert-Equal $state.posts 2 'single attempted ambiguous POST'
$state.comments.Clear()
$state.emit=$false
Assert-Throws {
    Invoke-SupraReceiptDelivery -Marker $marker -Body $body -ReadComments $read -PostComment $post
} 'uncertain POST without receipt must fail closed'
Assert-Equal $state.posts 3 'ambiguous POST must never be retried'

Write-Host ('NATIVE PS5.1 RECEIPT CONTRACT PASS; assertions=' + $script:assertions)
