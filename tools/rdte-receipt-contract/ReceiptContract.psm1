#requires -Version 5.1
# Public-safe, dependency-free receipt contract. No project credentials or endpoints.
Set-StrictMode -Version 2.0

function Normalize-SupraReceiptText {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    return ($Text.Replace("`r`n", "`n"))
}

function Get-SupraReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Comments,
        [Parameter(Mandatory = $true)][string]$Marker,
        [Parameter(Mandatory = $true)][string]$ExpectedBody
    )

    $expected = Normalize-SupraReceiptText -Text $ExpectedBody
    $wantedLines = @($expected.Split([char]10))
    if ($wantedLines.Count -lt 3 -or $wantedLines[0] -cne $Marker -or
        $wantedLines[1] -cne '' -or
        $wantedLines[2] -cnotmatch '^Status: (CENSUS_COMPLETE|COMPLETE|INCOMPLETE|STRUCTURAL_ID_FOUND|STRUCTURAL_ID_NOT_FOUND)$') {
        throw 'Expected receipt is not a valid first-line result envelope.'
    }

    # Do not call this collection $matches: PowerShell reserves $Matches for -match.
    $receiptCandidates = New-Object System.Collections.ArrayList
    foreach ($entry in $Comments) {
        if ($null -eq $entry -or $null -eq $entry.body) { continue }
        $body = Normalize-SupraReceiptText -Text ([string]$entry.body)
        $lines = @($body.Split([char]10))
        if ($lines.Count -eq 0 -or $lines[0] -cne $Marker) { continue }
        if ($lines.Count -lt 3 -or $lines[1] -cne '' -or
            $lines[2] -cnotmatch '^Status: (CENSUS_COMPLETE|COMPLETE|INCOMPLETE|STRUCTURAL_ID_FOUND|STRUCTURAL_ID_NOT_FOUND)$') {
            throw 'Malformed matching first-line receipt; manual reconciliation required.'
        }
        [void]$receiptCandidates.Add($entry)
    }
    if ($receiptCandidates.Count -gt 1) {
        throw 'Duplicate matching first-line receipts; manual reconciliation required.'
    }
    if ($receiptCandidates.Count -eq 0) { return $null }
    $found = $receiptCandidates[0]
    if ((Normalize-SupraReceiptText -Text ([string]$found.body)) -cne $expected) {
        throw 'Matching first-line receipt contains different evidence; no retry.'
    }
    return $found
}

function Invoke-SupraReceiptDelivery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Marker,
        [Parameter(Mandatory = $true)][string]$Body,
        [Parameter(Mandatory = $true)][scriptblock]$ReadComments,
        [Parameter(Mandatory = $true)][scriptblock]$PostComment
    )
    $before = @(& $ReadComments)
    $prior = Get-SupraReceipt -Comments $before -Marker $Marker -ExpectedBody $Body
    if ($null -ne $prior) {
        return [pscustomobject]@{status='ALREADY_VERIFIED';receipt=$prior;attempted_post=$false}
    }
    $postUncertain = $false
    try {
        & $PostComment $Body | Out-Null
    } catch {
        # A timed-out POST may still have created the remote effect.
        # Reconcile exactly once; never post again from this invocation.
        $postUncertain = $true
    }
    $after = @(& $ReadComments)
    $verified = Get-SupraReceipt -Comments $after -Marker $Marker -ExpectedBody $Body
    if ($null -eq $verified) {
        throw 'Remote result not confirmed after one POST; preserve uncertainty, do not retry.'
    }
    $resultStatus = if ($postUncertain) { 'POST_UNCERTAIN_BUT_VERIFIED' } else { 'PUBLISHED' }
    return [pscustomobject]@{status=$resultStatus;receipt=$verified;attempted_post=$true}
}

Export-ModuleMember -Function Get-SupraReceipt,Invoke-SupraReceiptDelivery
