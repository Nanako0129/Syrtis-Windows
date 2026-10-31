#Requires -Version 7.0
# Unit test for scripts/lib/NativePdbGate.ps1 (shipped helper, real path).
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "..\lib\NativePdbGate.ps1")

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("tb-pdbgate-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $started = [datetime]::UtcNow
    $top = Join-Path $scratch "tb_core_ffi.pdb"
    $deps = Join-Path $scratch "deps\tb_core_ffi.pdb"
    New-Item -ItemType Directory -Path (Split-Path $deps) -Force | Out-Null

    function Set-Pdb([string]$Path, [datetime]$WrittenUtc, [string]$Content = "pdb") {
        Set-Content -LiteralPath $Path -Value $Content -Encoding ascii
        (Get-Item -LiteralPath $Path).LastWriteTimeUtc = $WrittenUtc
    }
    function Select-Pdb { Select-NativePdbWrittenAfter -Candidates @($top, $deps) -BuildStartedUtc $started }

    # Stale PDB whose timestamp is within 2 s of a DLL would have passed the old
    # gate; before the build start it must now be rejected.
    Set-Pdb $top $started.AddSeconds(-1)
    if ($null -ne (Select-Pdb)) { throw "PDB written before build start was accepted." }

    # Written after build start: accepted.
    Set-Pdb $top $started.AddSeconds(1)
    if ((Select-Pdb).FullName -ne (Get-Item -LiteralPath $top).FullName) { throw "Fresh top-level PDB rejected." }

    # Ordering: stale top-level must not hide a fresh deps/ PDB.
    Set-Pdb $top $started.AddHours(-1)
    Set-Pdb $deps $started.AddSeconds(1)
    if ((Select-Pdb).FullName -ne (Get-Item -LiteralPath $deps).FullName) { throw "Stale top-level PDB hid fresh deps/ PDB." }

    # Empty file is rejected even when fresh.
    Remove-Item -LiteralPath $deps -Force
    Set-Pdb $top $started.AddSeconds(1)
    Clear-Content -LiteralPath $top
    if ($null -ne (Select-Pdb)) { throw "Zero-byte PDB was accepted." }

    # Nothing present.
    Remove-Item -LiteralPath $top -Force
    if ($null -ne (Select-Pdb)) { throw "Missing PDBs produced a result." }

    Write-Output "native PDB gate tests passed"
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
