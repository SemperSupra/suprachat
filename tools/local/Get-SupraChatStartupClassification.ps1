# Nonsecret diagnostic classifier for the opt-in Windows UIA smoke test.
# Never emit the captured exception message, stack, user path, or raw log.
function Get-SupraChatStartupClassification {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$Path)

    $classification = [ordered]@{
        LogPresent = $false
        FatalMarker = $false
        ExceptionType = 'UNAVAILABLE'
    }
    if (-not [System.IO.File]::Exists($Path)) {
        return [pscustomobject]$classification
    }
    $classification.LogPresent = $true

    try {
        $lines = @(Get-Content -LiteralPath $Path -TotalCount 80 -ErrorAction Stop)
    } catch {
        # Failure evidence stays classified, and cannot replace the first UIA failure.
        return [pscustomobject]$classification
    }

    $afterFatalMarker = $false
    foreach ($line in $lines) {
        if ($line -cmatch '^\S+\s+fatal-startup-exception$') {
            $classification.FatalMarker = $true
            $afterFatalMarker = $true
            continue
        }
        if ($afterFatalMarker -and
            $line -cmatch '^\S+\s+((?:System|Avalonia|Microsoft)\.[A-Za-z0-9_.]{1,160}Exception)(?::|$)') {
            $classification.ExceptionType = $Matches[1]
            break
        }
    }
    return [pscustomobject]$classification
}
