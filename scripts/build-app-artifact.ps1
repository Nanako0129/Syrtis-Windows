#Requires -Version 7.0

<#
.SYNOPSIS
  Build and verify one private, unsigned TokenBar App package.

.DESCRIPTION
  The command is intentionally fail-closed. It validates the repository-owned
  version contract, restores the locked .NET graph, builds the RID-specific Rust
  DLL with --locked plus the existing BuildTbNative target, publishes the WinUI
  App, and emits a version/RID-labelled ZIP with sanitized evidence. It never
  uploads or signs an artifact and never removes an existing caller path.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [Alias("EvidenceRoot")]
    [string]$OutputRoot,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Full", "Lite")]
    [string]$DeploymentMode = "Full"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "lib\MuiGuard.ps1")
. (Join-Path $PSScriptRoot "lib\RuntimeConfig.ps1")

$ExpectedSemanticVersion = "0.3.0"
$ExpectedAssemblyVersion = "0.3.0.0"
$ExpectedDotnetVersion = "10.0.301"
$ExpectedRustVersion = "1.96.1"

function Get-RepoProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Document,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($group in @($Document.Project.PropertyGroup)) {
        $node = $group.SelectSingleNode($Name)
        if ($null -ne $node) {
            return $node.InnerText
        }
    }

    throw "Directory.Build.props is missing required property '$Name'."
}

function Read-VersionContract {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Version contract is missing: Directory.Build.props"
    }

    try {
        $document = [xml](Get-Content -LiteralPath $Path -Raw)
    }
    catch {
        throw "Directory.Build.props is not valid XML: $($_.Exception.Message)"
    }

    $semantic = Get-RepoProperty -Document $document -Name "TbSemanticVersion"
    $assembly = Get-RepoProperty -Document $document -Name "TbAssemblyVersion"
    $product = Get-RepoProperty -Document $document -Name "TbProductName"
    $version = Get-RepoProperty -Document $document -Name "Version"
    $package = Get-RepoProperty -Document $document -Name "PackageVersion"
    $informational = Get-RepoProperty -Document $document -Name "InformationalVersion"
    $assemblyProperty = Get-RepoProperty -Document $document -Name "AssemblyVersion"
    $file = Get-RepoProperty -Document $document -Name "FileVersion"
    $revision = Get-RepoProperty -Document $document -Name "IncludeSourceRevisionInInformationalVersion"
    $lock = Get-RepoProperty -Document $document -Name "RestorePackagesWithLockFile"

    if ([string]::IsNullOrWhiteSpace($product)) {
        throw "Directory.Build.props product-name contract is empty."
    }
    if ($semantic -ne $ExpectedSemanticVersion -or $assembly -ne $ExpectedAssemblyVersion) {
        throw "Directory.Build.props version contract drifted: expected $ExpectedSemanticVersion / $ExpectedAssemblyVersion."
    }
    if ($version -ne '$(TbSemanticVersion)' -or $package -ne '$(TbSemanticVersion)' -or
        $informational -ne '$(TbSemanticVersion)') {
        throw "Directory.Build.props must derive Version, PackageVersion, and InformationalVersion from TbSemanticVersion."
    }
    if ($assemblyProperty -ne '$(TbAssemblyVersion)' -or $file -ne '$(TbAssemblyVersion)') {
        throw "Directory.Build.props must derive AssemblyVersion and FileVersion from TbAssemblyVersion."
    }
    if ($revision -ne "false" -or $lock -ne "true") {
        throw "Directory.Build.props must disable source-revision suffixes and enable NuGet lock files."
    }

    return [pscustomobject]@{
        ProductName = $product
        SemanticVersion = $semantic
        AssemblyVersion = $assembly
        Document = $document
    }
}

function Read-ManifestVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "App manifest is missing: $Path"
    }

    try {
        $manifest = [xml](Get-Content -LiteralPath $Path -Raw)
    }
    catch {
        throw "App manifest is not valid XML: $($_.Exception.Message)"
    }

    $identity = $manifest.SelectSingleNode("//*[local-name()='assemblyIdentity']")
    if ($null -eq $identity -or [string]::IsNullOrWhiteSpace($identity.version)) {
        throw "App manifest has no assemblyIdentity version."
    }

    return [string]$identity.version
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "PE file is missing: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or
        [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) {
        throw "Invalid DOS header: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "Invalid PE header: $Path"
    }

    return [uint16][BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Assert-NewOutputRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "OutputRoot cannot be empty."
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolved) {
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "OutputRoot is not a directory: $Path"
        }

        if (@(Get-ChildItem -LiteralPath $resolved -Force).Count -ne 0) {
            throw "OutputRoot must be an existing empty directory or a new path: $Path"
        }
    }
    else {
        New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    }

    return $resolved
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProject = Join-Path $repoRoot "src\TokenBar.App\TokenBar.App.csproj"
$smokeProject = Join-Path $repoRoot "src\TokenBar.Smoke\TokenBar.Smoke.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$manifestPath = Join-Path $repoRoot "src\TokenBar.App\app.manifest"
$versionContract = Read-VersionContract -Path $propsPath
$manifestVersion = Read-ManifestVersion -Path $manifestPath
if ($manifestVersion -ne $versionContract.AssemblyVersion) {
    throw "App manifest version '$manifestVersion' does not match Directory.Build.props numeric version '$($versionContract.AssemblyVersion)'."
}

if ($Rid -eq "win-x64") {
    $platform = "x64"
    $rustTarget = "x86_64-pc-windows-msvc"
    $expectedMachine = [uint16]0x8664
}
else {
    $platform = "ARM64"
    $rustTarget = "aarch64-pc-windows-msvc"
    $expectedMachine = [uint16]0xaa64
}

$outputRootResolved = Assert-NewOutputRoot -Path $OutputRoot
$appAssemblyName = "{0}.App" -f $versionContract.ProductName
$appExecutableName = "$appAssemblyName.exe"
$appPriName = "$appAssemblyName.pri"
# Include DeploymentMode so Full and Lite private artifacts (ZIP/symbols/checksums)
# never collide when the same RID+version are built into one OutputRoot or release set.
$artifactName = "{0}-App-{1}-{2}-{3}" -f $versionContract.ProductName, $versionContract.SemanticVersion, $Rid, $DeploymentMode
$artifactRoot = Join-Path $outputRootResolved $artifactName
if (Test-Path -LiteralPath $artifactRoot) {
    throw "Artifact output already exists; choose a new OutputRoot: $artifactRoot"
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$publishRoot = Join-Path $artifactRoot "publish"
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Push-Location $repoRoot
try {
    $dotnetVersion = ((& dotnet --version) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $dotnetVersion -ne $ExpectedDotnetVersion) {
        throw "dotnet SDK drifted: expected $ExpectedDotnetVersion, got '$dotnetVersion'."
    }

    $rustVerbose = @(& rustc --version --verbose)
    if ($LASTEXITCODE -ne 0) {
        throw "rustc --version --verbose failed."
    }
    $rustRelease = ($rustVerbose | Where-Object { $_ -match '^release:\s*(.+)$' } |
        Select-Object -First 1) -replace '^release:\s*', ''
    $rustCommit = ($rustVerbose | Where-Object { $_ -match '^commit-hash:\s*(.+)$' } |
        Select-Object -First 1) -replace '^commit-hash:\s*', ''
    if ([string]::IsNullOrWhiteSpace($rustRelease) -or
        [string]::IsNullOrWhiteSpace($rustCommit) -or $rustRelease -ne $ExpectedRustVersion -or
        $rustCommit -eq "unknown") {
        throw "rustc toolchain drifted: expected release $ExpectedRustVersion with a concrete commit hash."
    }

    $gitStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect git checkout state."
    }
    if ($gitStatus.Count -ne 0) {
        throw "Artifact builds require a clean git checkout so gitSha identifies the exact source."
    }

    $gitSha = ((& git rev-parse --verify HEAD) | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Unable to resolve a sanitized 40-character git SHA."
    }

    Invoke-Captured -Command "cargo" -Arguments @(
        "build", "--release", "--locked", "--target", $rustTarget
    ) -FailureMessage "Cargo locked build failed"

    foreach ($lockPath in @(
        "src\TokenBar.App\packages.lock.json",
        "src\TokenBar.Smoke\packages.lock.json",
        "src\TokenBar.Core\packages.lock.json",
        "src\TokenBar.Interop\packages.lock.json"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $lockPath) -PathType Leaf)) {
            throw "Missing generated NuGet lock file: $lockPath"
        }
    }
    Invoke-Captured -Command "dotnet" -Arguments @(
        "restore", $appProject, "--locked-mode"
    ) -FailureMessage "Locked App restore failed"
    Invoke-Captured -Command "dotnet" -Arguments @(
        "restore", $smokeProject, "--locked-mode"
    ) -FailureMessage "Locked Smoke restore failed"

    # Keep the existing repository-owned Platform/RID mapping and verifier in
    # the build path. TbNativeCargoLocked makes that target's own Cargo
    # invocation fail closed instead of relying on the preceding build.
    Invoke-Captured -Command "dotnet" -Arguments @(
        "msbuild", $smokeProject, "-t:BuildTbNative", "-p:Configuration=Release",
        "-p:Platform=$platform", "-p:RuntimeIdentifier=$Rid",
        "-p:TbNativeCargoLocked=true", "-p:RestoreLockedMode=true"
    ) -FailureMessage "BuildTbNative failed"

    $selfContainedArg = if ($DeploymentMode -eq "Lite") { "--no-self-contained" } else { "--self-contained" }
    $selfContainedProp = if ($DeploymentMode -eq "Lite") { "-p:SelfContained=false" } else { "-p:SelfContained=true" }

    Invoke-Captured -Command "dotnet" -Arguments @(
        "publish", $appProject, "-c", "Release", "-r", $Rid, $selfContainedArg, $selfContainedProp,
        "-p:Platform=$platform", "-p:RuntimeIdentifier=$Rid", "--no-restore",
        "-o", $publishRoot
    ) -FailureMessage "Locked App publish failed"
}
finally {
    Pop-Location
}

# The MIT licences of the vendored engine and the redistributed .NET packages
# require their notices to accompany the binary, not merely to exist in the
# repository. Copied here rather than in package-velopack.ps1 because that
# script packs this same publish directory, so one copy covers the portable
# ZIP, the nupkg, and the Setup.exe built from it.
$noticesSource = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $noticesSource -PathType Leaf)) {
    throw "Third-party notices are missing: THIRD-PARTY-NOTICES.md"
}
$licenseSource = Join-Path $repoRoot "LICENSE"
if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
    throw "License is missing: LICENSE"
}
Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $publishRoot "THIRD-PARTY-NOTICES.md") -Force
Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $publishRoot "LICENSE") -Force

$exePath = Join-Path $publishRoot $appExecutableName
$nativePath = Join-Path $publishRoot "tb_core_ffi.dll"
$priPath = Join-Path $publishRoot $appPriName
# Asserted, not assumed: a publish that silently dropped these would ship a
# binary carrying MIT code with no notice, and nothing else in this script
# looks for them.
foreach ($required in @("THIRD-PARTY-NOTICES.md", "LICENSE")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $required) -PathType Leaf)) {
        throw "Published output is missing the required notice file: $required"
    }
}
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Published App executable is missing: $appExecutableName"
}
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
    throw "Published native DLL is missing: tb_core_ffi.dll"
}
if (-not (Test-Path -LiteralPath $priPath -PathType Leaf)) {
    throw "Published PRI is missing: $appPriName"
}

$xbfFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter "*.xbf")
if ($xbfFiles.Count -eq 0) {
    throw "Published WinUI resources contain no XBF files."
}

$muiFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter "*.mui")
if ($muiFiles.Count -eq 0) {
    throw "Published output must retain .mui locale files; found zero."
}

# Required locale tags (checked-in manifest). Compare case-insensitively; reject
# duplicate entries after case normalization; fail with exact missing locale names.
$muiManifestPath = Join-Path $repoRoot "scripts\required-mui-locales.txt"
if (-not (Test-Path -LiteralPath $muiManifestPath -PathType Leaf)) {
    throw "Missing required MUI locale manifest: scripts/required-mui-locales.txt"
}
$rawLocales = @(
    Get-Content -LiteralPath $muiManifestPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith("#") }
)
if ($rawLocales.Count -lt 1) {
    throw "MUI locale manifest is empty."
}
$requiredLocales = [System.Collections.Generic.List[string]]::new()
$seenLocaleKeys = @{}
foreach ($locale in $rawLocales) {
    $key = $locale.ToLowerInvariant()
    if ($seenLocaleKeys.ContainsKey($key)) {
        throw "Duplicate MUI locale entry after case normalization: $locale"
    }
    $seenLocaleKeys[$key] = $true
    [void]$requiredLocales.Add($locale)
}
# Shared production guard (also driven by scripts/tests/test-required-mui-guard.ps1).
Assert-RequiredDesktopMuiLocales `
    -PublishRoot $publishRoot `
    -RequiredLocales @($requiredLocales) `
    -MuiFiles $muiFiles

# Forbidden payload / diagnostics / unused SDK binaries (must be absent from installer payload).
# This list mirrors the removals in TokenBar.App.csproj and must stay in step
# with them: an entry here without the matching strip fails the build on a
# correct payload, and a strip without an entry here goes unguarded.
# WinUIEdit.dll was in both and is now in neither - the XAML runtime loads it
# while building the visual tree, so removing it stopped the app from opening.
$forbiddenExact = @(
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "DirectML.dll",
    "Microsoft.Windows.Widgets.dll",
    "Microsoft.Windows.Widgets.Projection.dll",
    "Microsoft.Windows.Widgets.winmd",
    "mscordaccore.dll",
    "mscordbi.dll"
)
$publishFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$pdbCount = @($publishFiles | Where-Object { $_.Extension -eq ".pdb" }).Count
if ($pdbCount -ne 0) {
    throw "Published output must contain zero PDB files; found $pdbCount."
}
foreach ($name in $forbiddenExact) {
    $hits = @($publishFiles | Where-Object {
            [string]::Equals($_.Name, $name, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($hits.Count -gt 0) {
        throw "Forbidden file present in publish: $name"
    }
}
$prefixForbidden = @(
    @{ Prefix = "mscordaccore_"; Label = "mscordaccore_*" },
    @{ Prefix = "Microsoft.DiaSymReader.Native"; Label = "Microsoft.DiaSymReader.Native*" }
)
foreach ($rule in $prefixForbidden) {
    $hits = @($publishFiles | Where-Object {
            $_.Name.StartsWith($rule.Prefix, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($hits.Count -gt 0) {
        throw "Forbidden file present in publish: $($rule.Label)"
    }
}

# Deployment-mode structural checks (Lite must not ship a self-contained runtime).
$coreClr = Join-Path $publishRoot "coreclr.dll"
$liteFrameworkFamily = $null
$liteVelopackFramework = $null
if ($DeploymentMode -eq "Lite") {
    if (Test-Path -LiteralPath $coreClr -PathType Leaf) {
        throw "Lite publish must not include bundled runtime coreclr.dll."
    }
    $runtimeConfigName = "{0}.runtimeconfig.json" -f $appAssemblyName
    $runtimeConfigPath = Join-Path $publishRoot $runtimeConfigName
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
        throw "Lite publish is missing runtimeconfig.json: $runtimeConfigName"
    }
    $liteFrameworkFamily = Get-RuntimeConfigFrameworkFamily -Path $runtimeConfigPath
    $liteVelopackFramework = Get-VelopackFrameworkSpecFromFamily -Family $liteFrameworkFamily -Rid $Rid
}
else {
    if (-not (Test-Path -LiteralPath $coreClr -PathType Leaf)) {
        throw "Full self-contained publish is missing coreclr.dll."
    }
}

$assetCounts = [ordered]@{
    "Assets/anim-cat2" = 5
    "Assets/anim-cat2-light" = 5
    "Assets/anim-parrot" = 10
    "Assets/anim-parrot-light" = 10
}
foreach ($asset in $assetCounts.GetEnumerator()) {
    $assetDirectory = Join-Path $publishRoot $asset.Key
    if (-not (Test-Path -LiteralPath $assetDirectory -PathType Container)) {
        throw "Required asset directory is missing: $($asset.Key)"
    }
    for ($index = 0; $index -lt $asset.Value; $index++) {
        $assetPath = Join-Path $assetDirectory ("frame-{0:D2}.png" -f $index)
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Required asset file is missing: $($asset.Key)/frame-$('{0:D2}' -f $index).png"
        }
    }
}

$exeMachine = Get-PeMachine -Path $exePath
$nativeMachine = Get-PeMachine -Path $nativePath
if ($exeMachine -ne $expectedMachine -or $nativeMachine -ne $expectedMachine) {
    throw "PE architecture mismatch for ${Rid}: expected 0x$('{0:X4}' -f $expectedMachine), exe=0x$('{0:X4}' -f $exeMachine), native=0x$('{0:X4}' -f $nativeMachine)."
}

$nativeSourcePath = Join-Path $repoRoot ("target\{0}\release\tb_core_ffi.dll" -f $rustTarget)
if (-not (Test-Path -LiteralPath $nativeSourcePath -PathType Leaf)) {
    throw "RID-specific native source is missing: $rustTarget/tb_core_ffi.dll"
}
$nativeHash = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
$nativeSourceHash = (Get-FileHash -LiteralPath $nativeSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nativeHash -ne $nativeSourceHash) {
    throw "Published native DLL does not match the BuildTbNative source bytes."
}

# The native DLL must not depend on the Visual C++ Redistributable. A fresh
# Windows 11 ships ucrtbase.dll (satisfying api-ms-win-crt-*) but not
# vcruntime140.dll, so an import of it made every P/Invoke fail with
# 0x8007007E on a clean machine while the app opened normally and spun
# forever with no error surfaced (issue #36). .cargo/config.toml links the
# CRT statically to prevent that; this asserts the result rather than trusting
# the setting, because RUSTFLAGS in the environment silently overrides the
# config file and nothing else here would notice.
#
# This scans the file's bytes for the import name rather than walking the PE
# import directory. An imported DLL's name is stored as a plain ASCII string
# there, so the scan cannot miss a real import; it could in principle fire on
# the name appearing as unrelated data, which for this library it does not.
# CI runners and dev boxes all carry the redistributable, so this is the only
# thing standing between a reintroduced dependency and a user seeing it.
$nativeBytes = [System.IO.File]::ReadAllBytes($nativeSourcePath)
$nativeAscii = [System.Text.Encoding]::ASCII.GetString($nativeBytes)
$forbiddenCrtImports = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
$foundCrtImports = @($forbiddenCrtImports | Where-Object {
    $nativeAscii.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($foundCrtImports.Count -gt 0) {
    throw ("tb_core_ffi.dll depends on the Visual C++ Redistributable ({0}), " +
        "which a clean Windows install does not have. Expected a statically " +
        "linked CRT — check that .cargo/config.toml applies and that RUSTFLAGS " +
        "is not overriding it." -f ($foundCrtImports -join ', '))
}
Write-Output "Native CRT: statically linked (no VC++ redistributable imports)"

$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($fileVersionInfo.FileVersion -ne $ExpectedAssemblyVersion) {
    throw "Published App FileVersion '$($fileVersionInfo.FileVersion)' does not match $ExpectedAssemblyVersion."
}
if ($fileVersionInfo.ProductVersion -ne $ExpectedSemanticVersion) {
    throw "Published App ProductVersion '$($fileVersionInfo.ProductVersion)' does not match $ExpectedSemanticVersion."
}

$inventory = @(
    Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Sort-Object FullName |
        ForEach-Object {
            $relative = Get-RelativePath -Root $publishRoot -Path $_.FullName
            [pscustomobject]@{
                path = $relative
                bytes = [int64]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)

$zipName = "$artifactName.zip"
$zipPath = Join-Path $artifactRoot $zipName
if (Test-Path -LiteralPath $zipPath) {
    throw "Artifact ZIP already exists: $zipName"
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishRoot,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashName = "$zipName.sha256"
$hashPath = Join-Path $artifactRoot $hashName
Set-Content -LiteralPath $hashPath -Value "$zipHash  $zipName" -Encoding ascii -NoNewline
$zipBytes = [int64](Get-Item -LiteralPath $zipPath).Length

# Mode+RID publish-zip budgets (bytes). Measured baselines + ~5% headroom.
# A budget increase requires an explicit source edit and explanation.
$publishZipBudgetByModeRid = @{
    # Full: post-strip (#29) measured baselines + ~5%
    "Full|win-x64"   = [int64]78691000   # measured 74943809 (2026-08-05 clean Full) + ~5%
    "Full|win-arm64" = [int64]74928327   # measured 71360311 (fork hosted Full arm64-cross 2026-08-05) + ~5%
    # Lite: hosted measures (#30) + ~5%
    "Lite|win-x64"   = [int64]45759677   # measured 43580644 + ~5%
    "Lite|win-arm64" = [int64]43506642   # measured 41434897 + ~5%
}
$budgetKey = "{0}|{1}" -f $DeploymentMode, $Rid
if (-not $publishZipBudgetByModeRid.ContainsKey($budgetKey)) {
    throw "No publish-zip size budget configured for $budgetKey."
}
$maxZipBytes = [int64]$publishZipBudgetByModeRid[$budgetKey]
if ($zipBytes -gt $maxZipBytes) {
    $over = $zipBytes - $maxZipBytes
    $measuredMiB = [math]::Round($zipBytes / 1MB, 3)
    $budgetMiB = [math]::Round($maxZipBytes / 1MB, 3)
    $overMiB = [math]::Round($over / 1MB, 3)
    throw ("Publish zip exceeds size budget for {0}/{1}: measured={2} bytes ({3} MiB), budget={4} bytes ({5} MiB), over by {6} bytes ({7} MiB)." -f `
        $DeploymentMode, $Rid, $zipBytes, $measuredMiB, $maxZipBytes, $budgetMiB, $over, $overMiB)
}

# Separate version/RID-bound symbols/support artifact (NOT part of Setup.exe/nupkg).
# Fail closed: never accept empty/partial archives. Capture path:
#   1) MSBuild TbTrimUnusedPublishPayload copies stripped PDBs/DAC/DBI/DiaSymReader
#      into obj/.../tb-support-staging BEFORE removing them from ResolvedFileToPublish.
#   2) Native Rust/MSVC PDBs under target/<rustTarget>/release (recursive).
#   3) Required: managed App PDB, tb_core_ffi.pdb; DAC/DBI when the capture manifest
#      or runtime-pack layout indicates they were part of this publish.
$symbolsStaging = Join-Path $artifactRoot "symbols-staging"
if (Test-Path -LiteralPath $symbolsStaging) {
    Remove-Item -LiteralPath $symbolsStaging -Recurse -Force
}
New-Item -ItemType Directory -Path $symbolsStaging | Out-Null

function Copy-SymbolFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][hashtable]$CopiedNames
    )
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        return
    }
    $destName = [System.IO.Path]::GetFileName($SourcePath)
    $key = $destName.ToLowerInvariant()
    if ($CopiedNames.ContainsKey($key)) {
        return
    }
    Copy-Item -LiteralPath $SourcePath -Destination (Join-Path $symbolsStaging $destName) -Force
    $CopiedNames[$key] = $true
}

$copiedSymbolNames = @{}
$capturedFromPublishStrip = [System.Collections.Generic.List[string]]::new()

# (1) RID-scoped intermediate dir written by TbTrimUnusedPublishPayload.
$tfm = "net10.0-windows10.0.19041.0"
$msbuildSupportDir = Join-Path $repoRoot (
    "src\TokenBar.App\obj\{0}\Release\{1}\{2}\tb-support-staging" -f $platform, $tfm, $Rid)
if (-not (Test-Path -LiteralPath $msbuildSupportDir -PathType Container)) {
    throw "MSBuild support staging missing after publish: $msbuildSupportDir"
}
$capturedListPath = Join-Path $msbuildSupportDir "CAPTURED.txt"
if (-not (Test-Path -LiteralPath $capturedListPath -PathType Leaf)) {
    throw "MSBuild support capture manifest missing: CAPTURED.txt (staging must be recreated each publish)"
}
# Only trust names listed in this publish's CAPTURED.txt — never orphan files left in obj/.
$capturedNames = @(
    Get-Content -LiteralPath $capturedListPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not [string]::Equals($_, "CAPTURED.txt", [StringComparison]::OrdinalIgnoreCase) }
)
foreach ($name in $capturedNames) {
    $src = Join-Path $msbuildSupportDir $name
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        throw "CAPTURED.txt lists '$name' but file is missing from fresh support staging."
    }
    Copy-SymbolFile -SourcePath $src -CopiedNames $copiedSymbolNames
    [void]$capturedFromPublishStrip.Add($name)
}

# (2) Native Rust/MSVC PDBs next to the locked Cargo release for this RID.
# tb_core_ffi.pdb freshness. Name-only acceptance can archive a stale ignored
# target/.../tb_core_ffi.pdb that cargo did not rewrite while the publish still
# hash-matched a newer tb_core_ffi.dll — an archive that cannot symbolicate the
# binary it claims to describe.
#
# This is a timestamp heuristic, NOT an identity check. Proving a PDB belongs to
# a DLL means comparing the CodeView RSDS GUID and age in the DLL's debug
# directory against the PDB's own signature, and nothing here reads either. It
# therefore fails closed against the named case above and stays open wherever
# timestamps pass while the PDB is stale: an incremental cargo run that rewrites
# neither file, a CI cache or artifact restore that resets write times, and
# filesystem granularity coarse enough to collapse distinct writes. The
# candidate loop also stops at the first acceptance, so a plausible-looking
# top-level PDB wins before deps/ is examined.
#
# Issue #34 records a fix cheaper than PE parsing: gate on "written after this
# build started", which the script already knows and which closes every case
# above.
$nativeReleaseDir = Join-Path $repoRoot ("target\{0}\release" -f $rustTarget)
if (-not (Test-Path -LiteralPath $nativeReleaseDir -PathType Container)) {
    throw "Native release directory missing for symbols capture: target/$rustTarget/release"
}
$nativeDllItem = Get-Item -LiteralPath $nativeSourcePath
# Prefer the exact sibling of the hash-verified DLL, then deps/.
$nativePdbCandidates = @(
    (Join-Path $nativeReleaseDir "tb_core_ffi.pdb"),
    (Join-Path $nativeReleaseDir "deps\tb_core_ffi.pdb")
)
# Tolerance for filesystem write-time jitter between the linker emitting the PDB
# and the DLL. Not a matching contract — see the heuristic note above.
$nativePdbMaxOlderThanDllSeconds = 2
$freshNativePdbPath = $null
$freshNativePdbMeta = $null
foreach ($candidate in $nativePdbCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }
    $pdbItem = Get-Item -LiteralPath $candidate
    if ($pdbItem.Length -le 0) {
        continue
    }
    $olderBySeconds = ($nativeDllItem.LastWriteTimeUtc - $pdbItem.LastWriteTimeUtc).TotalSeconds
    if ($olderBySeconds -gt $nativePdbMaxOlderThanDllSeconds) {
        Write-Output ("Skipping stale native PDB older than verified DLL by {0:N1}s: {1}" -f $olderBySeconds, $candidate)
        continue
    }
    $freshNativePdbPath = $pdbItem.FullName
    $pdbLocation = if ($candidate -like "*\deps\tb_core_ffi.pdb" -or $candidate -like "*/deps/tb_core_ffi.pdb") {
        "target/$rustTarget/release/deps/tb_core_ffi.pdb"
    } else {
        "target/$rustTarget/release/tb_core_ffi.pdb"
    }
    $freshNativePdbMeta = [ordered]@{
        relative = $pdbLocation
        bytes = [int64]$pdbItem.Length
        dllLastWriteTimeUtc = $nativeDllItem.LastWriteTimeUtc.ToString("o")
        pdbLastWriteTimeUtc = $pdbItem.LastWriteTimeUtc.ToString("o")
        pdbOlderThanDllSeconds = [math]::Round($olderBySeconds, 3)
        maxOlderThanDllSeconds = $nativePdbMaxOlderThanDllSeconds
    }
    break
}
if ($null -eq $freshNativePdbPath) {
    $msg = "No fresh tb_core_ffi.pdb for the verified native DLL under target/{0}/release. PDB must exist next to the cargo-built DLL (or deps/) and must not be older than the DLL by more than {1}s (refuse stale ignored target PDBs)." -f $rustTarget, $nativePdbMaxOlderThanDllSeconds
    throw $msg
}
Copy-SymbolFile -SourcePath $freshNativePdbPath -CopiedNames $copiedSymbolNames

# Other MSVC/Rust PDBs under the same release tree (unique basenames only).
Get-ChildItem -LiteralPath $nativeReleaseDir -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue |
    ForEach-Object {
        if ([string]::Equals($_.Name, "tb_core_ffi.pdb", [StringComparison]::OrdinalIgnoreCase)) {
            # Already accepted via freshness check above; never re-copy a stale sibling.
            return
        }
        Copy-SymbolFile -SourcePath $_.FullName -CopiedNames $copiedSymbolNames
    }

$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsStaging -File | Sort-Object Name)
if ($symbolFiles.Count -lt 1) {
    throw "Symbols staging is empty; refuse to emit a partial/empty symbols archive."
}

$symbolNameSet = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($f in $symbolFiles) {
    [void]$symbolNameSet.Add($f.Name)
}

# Fail closed: managed App PDB + native FFI PDB are always required.
$appPdbName = "{0}.pdb" -f $appAssemblyName
if (-not $symbolNameSet.Contains($appPdbName)) {
    throw "Symbols archive missing required managed App PDB: $appPdbName"
}
if (-not $symbolNameSet.Contains("tb_core_ffi.pdb")) {
    throw "Symbols archive missing required native PDB: tb_core_ffi.pdb (expected under target/$rustTarget/release)"
}

# DAC/DBI: required when the publish strip capture listed them (runtime pack had them).
$dacRequiredIfCaptured = @("mscordaccore.dll", "mscordbi.dll")
foreach ($name in $dacRequiredIfCaptured) {
    $wasCaptured = $false
    foreach ($c in $capturedFromPublishStrip) {
        if ([string]::Equals($c, $name, [StringComparison]::OrdinalIgnoreCase)) {
            $wasCaptured = $true
            break
        }
    }
    # Self-contained Full publishes always ship these in the runtime pack before strip;
    # require them whenever we have any strip capture list OR coreclr was published.
    $coreClrPresentInPublish = Test-Path -LiteralPath (Join-Path $publishRoot "coreclr.dll") -PathType Leaf
    if ($wasCaptured -or $coreClrPresentInPublish) {
        if (-not $symbolNameSet.Contains($name)) {
            throw "Symbols archive missing required diagnostics binary from runtime pack: $name"
        }
    }
}

# Every file listed in the MSBuild capture manifest must appear in the staging set.
foreach ($name in $capturedFromPublishStrip) {
    if ([string]::Equals($name, "CAPTURED.txt", [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    if (-not $symbolNameSet.Contains($name)) {
        throw "Symbols archive missing captured stripped support file: $name"
    }
}

$symbolInventory = @(
    $symbolFiles | ForEach-Object {
        [pscustomobject]@{
            path = $_.Name
            bytes = [int64]$_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
$symbolsMeta = [ordered]@{
    schema = "tokenbar-symbols-support.v1"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    gitSha = $gitSha.ToLowerInvariant()
    rid = $Rid
    platform = $platform
    rustTarget = $rustTarget
    capturedFromPublishStrip = @($capturedFromPublishStrip)
    note = "Support/diagnostics archive for matching crash dumps. Not installed by Setup.exe or nupkg. Release operators archive this alongside the release transaction; PR CI uploads only sanitized symbols-evidence.json (never the symbols ZIP)."
    files = $symbolInventory
}
$symbolsMetaPath = Join-Path $symbolsStaging "SYMBOLS-MANIFEST.json"
($symbolsMeta | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $symbolsMetaPath -Encoding utf8
$symbolsZipName = "{0}-Symbols-{1}-{2}-{3}.zip" -f $versionContract.ProductName, $versionContract.SemanticVersion, $Rid, $DeploymentMode
$symbolsZipPath = Join-Path $artifactRoot $symbolsZipName
if (Test-Path -LiteralPath $symbolsZipPath) {
    throw "Symbols archive already exists: $symbolsZipName"
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $symbolsStaging,
    $symbolsZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
$symbolsZipHash = (Get-FileHash -LiteralPath $symbolsZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$symbolsZipBytes = [int64](Get-Item -LiteralPath $symbolsZipPath).Length
Set-Content -LiteralPath (Join-Path $artifactRoot "$symbolsZipName.sha256") -Value "$symbolsZipHash  $symbolsZipName" -Encoding ascii -NoNewline

# Prove ZIP members match the fail-closed inventory (no silent drop on zip).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($symbolsZipPath)
try {
    $zipNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
        [void]$zipNames.Add($entry.Name)
    }
    foreach ($name in $symbolNameSet) {
        if (-not $zipNames.Contains($name)) {
            throw "Symbols ZIP is missing staged support file: $name"
        }
    }
    if (-not $zipNames.Contains("SYMBOLS-MANIFEST.json")) {
        throw "Symbols ZIP is missing SYMBOLS-MANIFEST.json"
    }
}
finally {
    $zip.Dispose()
}

# Drop staging tree; keep only the zip + sha256 + sanitized evidence next to the app artifact.
Remove-Item -LiteralPath $symbolsStaging -Recurse -Force
# Sanitized symbols evidence (hashes/sizes only) for PR CI upload surfaces — never the ZIP.
$symbolsEvidence = [ordered]@{
    schema = "tokenbar-symbols-evidence.v1"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    gitSha = $gitSha.ToLowerInvariant()
    rid = $Rid
    archive = $symbolsZipName
    archiveSha256 = $symbolsZipHash
    archiveBytes = $symbolsZipBytes
    fileCount = [int]$symbolInventory.Count
    requiredAppPdb = $appPdbName
    requiredNativePdb = "tb_core_ffi.pdb"
    nativePdbFreshness = $freshNativePdbMeta
    capturedFromPublishStrip = @($capturedFromPublishStrip)
    files = $symbolInventory
}
($symbolsEvidence | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $artifactRoot "symbols-evidence.json") -Encoding utf8


$evidence = [ordered]@{
    schema = "phase10-app-evidence.v1"
    status = "structure-version-pe-hash"
    productName = $versionContract.ProductName
    semanticVersion = $versionContract.SemanticVersion
    assemblyVersion = $versionContract.AssemblyVersion
    informationalVersion = $ExpectedSemanticVersion
    manifestVersion = $manifestVersion
    deploymentMode = $DeploymentMode
    liteFrameworkFamily = $liteFrameworkFamily
    liteVelopackFramework = $liteVelopackFramework
    muiCount = [int]$muiFiles.Count
    requiredMuiLocales = [int]$requiredLocales.Count
    pdbCount = [int]$pdbCount
    forbiddenAbsent = $true
    maxZipBytesBudget = [int64]$maxZipBytes
    zipBytes = [int64]$zipBytes
    symbolsArchive = $symbolsZipName
    symbolsArchiveSha256 = $symbolsZipHash
    symbolsArchiveBytes = [int64]$symbolsZipBytes
    rid = $Rid
    platform = $platform
    expectedPeMachine = ("0x{0:X4}" -f $expectedMachine)
    gitSha = $gitSha.ToLowerInvariant()
    dotnetVersion = $dotnetVersion
    rustc = [ordered]@{
        release = $rustRelease
        commitHash = $rustCommit
    }
    native = [ordered]@{
        target = $rustTarget
        source = "target/$rustTarget/release/tb_core_ffi.dll"
        sha256 = $nativeHash
    }
    artifact = [ordered]@{
        zip = $zipName
        sha256 = $zipHash
        bytes = [int64](Get-Item -LiteralPath $zipPath).Length
        reproducibility = "not-claimed"
    }
    inventory = $inventory
    startupSmoke = [ordered]@{
        command = "$appExecutableName --startup-smoke <new-sentinel-path>"
        hostGate = "explicitly authorized non-disposable active user profile with outbound network blocked; not isolated"
        hostedCi = "structure/version/PE/hash checks only; no interactive WinUI startup claim"
    }
}

$evidenceJson = $evidence | ConvertTo-Json -Depth 10
if ($evidenceJson -match '(?i)([A-Z]:[\\/]|/Users/|/home/|\\Users\\)') {
    throw "Evidence contains an absolute path; refusing to write evidence.json."
}
$evidenceName = "evidence.json"
$evidencePath = Join-Path $artifactRoot $evidenceName
Set-Content -LiteralPath $evidencePath -Value $evidenceJson -Encoding utf8

Write-Output "Phase 10 App artifact verified: $zipName"
Write-Output "Evidence: $evidenceName; checksum: $hashName; symbols: $symbolsZipName"
