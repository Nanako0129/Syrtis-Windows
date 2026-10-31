#Requires -Version 7.0
# Native PDB freshness gate for the symbols archive (issue #34).

function Select-NativePdbWrittenAfter {
    <#
    .SYNOPSIS
      Return the first candidate PDB that is non-empty and was written at or
      after the build started; $null when none qualifies.
    .DESCRIPTION
      A PDB older than this build's start was not produced by this build's cargo
      run, so an incremental no-rewrite, a cache restore that keeps old write
      times, or a stale ignored target/ file all fail closed. Every candidate is
      examined, so a stale top-level PDB cannot hide a fresh deps/ one. This is
      still not an identity check (CodeView RSDS GUID/age); it is the cheaper
      gate issue #34 chose.
    #>
    param(
        [Parameter(Mandatory = $true)][string[]]$Candidates,
        [Parameter(Mandatory = $true)][datetime]$BuildStartedUtc
    )

    foreach ($candidate in $Candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $item = Get-Item -LiteralPath $candidate
        if ($item.Length -le 0) {
            continue
        }
        if ($item.LastWriteTimeUtc -lt $BuildStartedUtc.ToUniversalTime()) {
            Write-Host ("Skipping native PDB written before this build started ({0:o}): {1}" -f $item.LastWriteTimeUtc, $candidate)
            continue
        }
        return $item
    }
    return $null
}
