#Requires -Version 7.0

<#
.SYNOPSIS
  Build and verify one private, unsigned Velopack release package.

.DESCRIPTION
  The command is intentionally fail-closed. It delegates App structure and
  version checks to build-app-artifact.ps1, restores the repository-owned vpk
  tool, packs one RID-specific channel, and verifies the resulting nuspec and
  application entry. It never signs, uploads, or publishes an artifact.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Full", "Lite")]
    [string]$DeploymentMode = "Full"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "lib\RuntimeConfig.ps1")

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

function Read-PackProperties {
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

    $product = Get-RepoProperty -Document $document -Name "TbProductName"
    $semantic = Get-RepoProperty -Document $document -Name "TbSemanticVersion"
    $publisher = Get-RepoProperty -Document $document -Name "TbPublisher"
    if ([string]::IsNullOrWhiteSpace($product) -or [string]::IsNullOrWhiteSpace($semantic) `
            -or [string]::IsNullOrWhiteSpace($publisher)) {
        throw "Directory.Build.props packaging properties cannot be empty."
    }

    return [pscustomobject]@{
        ProductName = $product
        SemanticVersion = $semantic
        Publisher = $publisher
    }
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

function Get-NuspecNodes {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $path = "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='{0}']" -f $Name
    return @($Document.SelectNodes($path))
}

function Get-NuspecValue {
    param(
        [Parameter(Mandatory = $true)][xml]$Document,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $nodes = @(Get-NuspecNodes -Document $Document -Name $Name)
    if ($nodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($nodes[0].InnerText)) {
        throw "Velopack nuspec metadata must contain exactly one non-empty '$Name' element."
    }

    return $nodes[0].InnerText.Trim()
}

function Get-VelopackFrameworkSpec {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$AppAssemblyName,
        [Parameter(Mandatory = $true)][string]$Rid
    )

    $runtimeConfigPath = Join-Path $PublishRoot ("{0}.runtimeconfig.json" -f $AppAssemblyName)
    $family = Get-RuntimeConfigFrameworkFamily -Path $runtimeConfigPath
    return Get-VelopackFrameworkSpecFromFamily -Family $family -Rid $Rid
}

function Assert-VelopackPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$PackId,
        [Parameter(Mandatory = $true)][string]$ProductName,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$SemanticVersion,
        [Parameter(Mandatory = $true)][string]$MainExe,
        [Parameter(Mandatory = $true)][string]$MachineArchitecture,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$DeploymentMode,
        [Parameter(Mandatory = $false)][string]$ExpectedRuntimeDependency = ""
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Velopack package is missing: $PackagePath"
    }

    $archive = $null
    $reader = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
        $nuspecEntries = @($archive.Entries | Where-Object {
                -not $_.FullName.EndsWith("/") -and $_.FullName -match '(?i)\.nuspec$'
            })
        if ($nuspecEntries.Count -ne 1) {
            throw "Expected exactly one nuspec entry, found $($nuspecEntries.Count)."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        $nuspecText = $reader.ReadToEnd()
        $document = [xml]$nuspecText
        $reader.Dispose()
        $reader = $null

        $expected = [ordered]@{
            id = $PackId
            title = $ProductName
            authors = $Publisher
            version = $SemanticVersion
            mainExe = $MainExe
            machineArchitecture = $MachineArchitecture
            channel = $Channel
        }
        foreach ($item in $expected.GetEnumerator()) {
            $actual = Get-NuspecValue -Document $document -Name $item.Key
            if ($actual -cne [string]$item.Value) {
                throw "Velopack nuspec '$($item.Key)' mismatch: expected '$($item.Value)', got '$actual'."
            }
        }

        $runtimeNodes = @(Get-NuspecNodes -Document $document -Name "runtimeDependencies")
        if ($DeploymentMode -eq "Lite") {
            if ([string]::IsNullOrWhiteSpace($ExpectedRuntimeDependency)) {
                throw "Lite package validation requires ExpectedRuntimeDependency from runtimeconfig.json."
            }
            if ($runtimeNodes.Count -ne 1 -or
                [string]::IsNullOrWhiteSpace($runtimeNodes[0].InnerText)) {
                throw "Lite package must contain exactly one non-empty runtimeDependencies element."
            }
            $runtimeDependency = $runtimeNodes[0].InnerText.Trim()
            if ($runtimeDependency -cne $ExpectedRuntimeDependency) {
                throw "Velopack nuspec 'runtimeDependencies' mismatch: expected '$ExpectedRuntimeDependency', got '$runtimeDependency'."
            }
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($ExpectedRuntimeDependency)) {
                throw "Full package validation must not receive a Lite runtime prerequisite."
            }
            if ($runtimeNodes.Count -ne 0) {
                throw "Full package must omit runtimeDependencies entirely; found $($runtimeNodes.Count) element(s)."
            }
        }

        $appEntry = "lib/app/$MainExe"
        $matchingEntries = @($archive.Entries | Where-Object { $_.FullName -ceq $appEntry })
        if ($matchingEntries.Count -ne 1) {
            throw "Velopack package is missing required application entry '$appEntry'."
        }
    }
    catch {
        throw "Velopack package validation failed for '$PackagePath': $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$buildScript = Join-Path $repoRoot "scripts\build-app-artifact.ps1"
$outputRootResolved = Assert-NewOutputRoot -Path $OutputRoot
$packProperties = Read-PackProperties -Path $propsPath
# Keep the update identity stable across product renames so installed clients
# continue to find the same release lineage.
$packId = "Nyanako.Syrtis"
$appExecutableName = "{0}.App.exe" -f $packProperties.ProductName
$machineArchitecture = if ($Rid -eq "win-x64") { "x64" } else { "arm64" }
$requiredLiteFramework = if ($Rid -eq "win-x64") {
    "net10-x64-runtime"
} else {
    "net10-arm64-runtime"
}
$channel = if ($DeploymentMode -eq "Lite") { "$Rid-lite" } else { $Rid }

# Keep build artifacts separate from channel-scoped release files so the
# publish directory passed to vpk remains the one verified by the build step.
$buildOutputRoot = Join-Path $outputRootResolved "app-artifact"
# Called directly rather than through Invoke-Captured: that helper exists for
# native commands and checks $LASTEXITCODE, which a PowerShell script does not
# set. Splatting an argument array at a script also binds "-Rid" positionally
# instead of as a parameter name. This script throws on failure and
# $ErrorActionPreference is Stop, so a failure propagates on its own.
& $buildScript -Rid $Rid -OutputRoot $buildOutputRoot -DeploymentMode $DeploymentMode

# Must match scripts/build-app-artifact.ps1 naming (includes DeploymentMode).
$artifactName = "{0}-App-{1}-{2}-{3}" -f $packProperties.ProductName, $packProperties.SemanticVersion, $Rid, $DeploymentMode
$publishRoot = Join-Path $buildOutputRoot (Join-Path $artifactName "publish")
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Verified publish directory is missing: $publishRoot"
}

$appAssemblyName = "{0}.App" -f $packProperties.ProductName
$frameworkSpec = ""
if ($DeploymentMode -eq "Lite") {
    $frameworkSpec = Get-VelopackFrameworkSpec `
        -PublishRoot $publishRoot `
        -AppAssemblyName $appAssemblyName `
        -Rid $Rid
    if ($frameworkSpec -cne $requiredLiteFramework) {
        throw "Lite runtimeconfig must resolve to '$requiredLiteFramework', got '$frameworkSpec'."
    }
    Write-Output "Lite Velopack framework from runtimeconfig.json: $frameworkSpec"
}

$releasesRoot = Join-Path $outputRootResolved "releases"
New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null

# Release notes are baked into the nuspec at pack time and cannot be
# backfilled, so a version with no notes file packs exactly as it did before
# this existed -- "no notes" is a first-class state, not an error path, and
# every version published so far is in it.
#
# The path is derived here rather than passed in. This script is invoked with
# the same three arguments by ci.yml on every push and by release.yml on a tag,
# and neither passes a tag; deriving from $packProperties.SemanticVersion, which
# this script already computes, is what keeps the two workflows from drifting.
$releaseNotesPath = Join-Path $repoRoot (".github\release-notes\v{0}.md" -f $packProperties.SemanticVersion)
$hasReleaseNotes = Test-Path -LiteralPath $releaseNotesPath -PathType Leaf
if ($hasReleaseNotes) {
    Write-Output "Release notes found: $releaseNotesPath"
}
else {
    Write-Output "No release notes file for $($packProperties.SemanticVersion); packing without --releaseNotes."
}

Push-Location $repoRoot
try {
    Invoke-Captured -Command "dotnet" -Arguments @(
        "tool", "restore"
    ) -FailureMessage "Local .NET tool restore failed"

    $vpkArgs = @(
        "vpk", "pack",
        "--packId", $packId,
        "--packVersion", $packProperties.SemanticVersion,
        "--packDir", $publishRoot,
        "--mainExe", $appExecutableName,
        "--packTitle", $packProperties.ProductName,
        "--packAuthors", $packProperties.Publisher,
        "--runtime", $Rid,
        "--channel", $channel,
        "--outputDir", $releasesRoot
    )
    if ($DeploymentMode -eq "Lite") {
        $vpkArgs += @("--framework", $frameworkSpec)
    }
    if ($hasReleaseNotes) {
        $vpkArgs += @("--releaseNotes", $releaseNotesPath)
    }

    Invoke-Captured -Command "dotnet" -Arguments $vpkArgs -FailureMessage "Velopack pack failed"
}
finally {
    Pop-Location
}

$packageName = "{0}-{1}-{2}-full.nupkg" -f $packId, $packProperties.SemanticVersion, $channel
$packagePath = Join-Path $releasesRoot $packageName
Assert-VelopackPackage -PackagePath $packagePath -PackId $packId `
    -ProductName $packProperties.ProductName -Publisher $packProperties.Publisher `
    -SemanticVersion $packProperties.SemanticVersion -MainExe $appExecutableName `
    -MachineArchitecture $machineArchitecture -Channel $channel `
    -DeploymentMode $DeploymentMode -ExpectedRuntimeDependency $frameworkSpec

# Stated conditionally, beside the pack-id/version/mainExe/architecture/channel
# assertions above: IF a notes file was found for this version, the produced
# feed must carry non-empty notes for it. The client reads
# releases.{channel}.json from the release assets -- NOT the GitHub release
# body, which release.yml feeds to `gh release --notes-file` and which
# Velopack's GithubSource never reads -- so this is the only place the dialog's
# changelog can come from. Red the moment --releaseNotes stops reaching vpk
# pack, on every PR rather than only at tag time.
if ($hasReleaseNotes) {
    $feedPath = Join-Path $releasesRoot ("releases.{0}.json" -f $channel)
    if (-not (Test-Path -LiteralPath $feedPath -PathType Leaf)) {
        throw "Velopack release feed is missing: $feedPath"
    }

    $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    $feedAssets = @($feed.Assets | Where-Object {
            $_.Version -ceq $packProperties.SemanticVersion -and $_.Type -ceq "Full"
        })
    if ($feedAssets.Count -ne 1) {
        throw "Expected exactly one Full asset for $($packProperties.SemanticVersion) in $feedPath, found $($feedAssets.Count)."
    }

    $feedNotes = $feedAssets[0].NotesMarkdown
    if ([string]::IsNullOrWhiteSpace($feedNotes)) {
        throw "Release notes were supplied from '$releaseNotesPath' but '$feedPath' carries no notesMarkdown for $($packProperties.SemanticVersion)."
    }

    Write-Output ("Release feed notes verified: {0} characters in {1}" -f $feedNotes.Length, (Split-Path -Leaf $feedPath))

    # vpk embeds the notes in the package's nuspec, and every client rejects a
    # nuspec larger than UpdateFlow.MaxNuspecBytes (UpdateFlow.cs:307-311).
    # Those two limits are both 65,536 and they are NOT the same limit: the
    # parser's is characters of notes, the client's is bytes of the whole
    # nuspec including XML escaping. A release body approaching the first
    # therefore produces packages no client will install -- and packaging would
    # still succeed, so the failure would appear only on users' machines, as an
    # update that downloads and then silently does nothing.
    #
    # Assert the real constraint rather than guessing a margin for the notes.
    # NuspecMaxBytesMatchesUpdateFlow pins this constant against the C# one.
    $maxNuspecBytes = 65536
    $maxCompressionRatio = 100
    $nupkg = @(Get-ChildItem -LiteralPath $releasesRoot -File -Filter "*-full.nupkg")
    if ($nupkg.Count -ne 1) {
        throw "Expected exactly one full nupkg in $releasesRoot, found $($nupkg.Count)."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg[0].FullName)
    try {
        $nuspec = @($zip.Entries | Where-Object { $_.FullName -like "*.nuspec" })
        if ($nuspec.Count -ne 1) {
            throw "Expected exactly one nuspec in $($nupkg[0].Name), found $($nuspec.Count)."
        }

        # Mirror ValidateNuspec's condition WHOLE. It is three checks, not one:
        # uncompressed size, compressed size, and a compression ratio. A first
        # pass here implemented only the size check, and highly compressible
        # notes -- repeated or generated text -- pass that while blowing the
        # ratio, so packaging succeeded and every client discarded the download.
        # Roughly 60 KB of repeated text compresses to under 600 bytes.
        $entryLength = $nuspec[0].Length
        $entryCompressed = $nuspec[0].CompressedLength
        $reason = $null
        if ($entryLength -lt 1 -or $entryLength -gt $maxNuspecBytes) {
            $reason = "uncompressed size $entryLength is outside 1..$maxNuspecBytes"
        }
        elseif ($entryCompressed -lt 1 -or $entryCompressed -gt $maxNuspecBytes) {
            $reason = "compressed size $entryCompressed is outside 1..$maxNuspecBytes"
        }
        elseif ($entryLength -gt $entryCompressed * $maxCompressionRatio) {
            $reason = ("compression ratio {0:N1} exceeds $maxCompressionRatio " -f
                ($entryLength / $entryCompressed))
        }

        if ($null -ne $reason) {
            throw ("The packaged nuspec is one the client rejects: $reason. " +
                "The release notes are embedded in it, so shorten or vary " +
                "'$releaseNotesPath'. Every client would download this package " +
                "and then discard it.")
        }

        Write-Output ("Nuspec verified: {0} bytes, {1} compressed, ratio {2:N1} of {3}" -f
            $entryLength, $entryCompressed, ($entryLength / $entryCompressed), $maxCompressionRatio)
    }
    finally {
        $zip.Dispose()
    }
}

$releaseFiles = @(Get-ChildItem -LiteralPath $releasesRoot -File | Sort-Object Name)
Write-Output "Phase 11 Velopack package verified: $packageName"
Write-Output "Release files:"
foreach ($file in $releaseFiles) {
    Write-Output ("  {0} ({1} bytes)" -f $file.Name, $file.Length)
}

# Measured per-mode/per-RID size budgets for nupkg and Setup.exe (bytes).
# ~5% above clean pinned measurements; increase requires explicit source edit.
$nupkgSetupBudgetByModeRid = @{
    "Full|win-x64|nupkg" = [int64]86355526
    "Full|win-x64|setup" = [int64]91040173
    "Full|win-arm64|nupkg" = [int64]82419463
    "Full|win-arm64|setup" = [int64]87104109
    "Lite|win-x64|nupkg" = [int64]47811988
    "Lite|win-x64|setup" = [int64]52496634
    "Lite|win-arm64|nupkg" = [int64]45559013
    "Lite|win-arm64|setup" = [int64]50243660
}
function Assert-SizeBudget {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][int64]$Measured,
        [Parameter(Mandatory = $true)][int64]$Budget
    )
    if ($Measured -gt $Budget) {
        $over = $Measured - $Budget
        $measuredMiB = [math]::Round($Measured / 1MB, 3)
        $budgetMiB = [math]::Round($Budget / 1MB, 3)
        $overMiB = [math]::Round($over / 1MB, 3)
        throw ("{0} exceeds size budget for {1}/{2}: measured={3} bytes ({4} MiB), budget={5} bytes ({6} MiB), over by {7} bytes ({8} MiB)." -f `
            $Label, $DeploymentMode, $Rid, $Measured, $measuredMiB, $Budget, $budgetMiB, $over, $overMiB)
    }
}
$nupkgBytes = [int64](Get-Item -LiteralPath $packagePath).Length
$nupkgKey = "{0}|{1}|nupkg" -f $DeploymentMode, $Rid
if (-not $nupkgSetupBudgetByModeRid.ContainsKey($nupkgKey)) {
    throw "No nupkg size budget for $nupkgKey"
}
Assert-SizeBudget -Label "nupkg" -Measured $nupkgBytes -Budget ([int64]$nupkgSetupBudgetByModeRid[$nupkgKey])

# Pin the channel-scoped Setup identity (same contract as write-package-evidence.ps1).
$expectedSetupName = "{0}-{1}-Setup.exe" -f $packId, $channel
$setupPath = Join-Path $releasesRoot $expectedSetupName
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Velopack releases missing expected Setup.exe: $expectedSetupName"
}

# Build the install wizard with THIS INVOCATION's Velopack Setup.exe embedded,
# and replace the Setup.exe in $releasesRoot with that wrapper -- one file,
# same name, before the size-budget assertion below, so that assertion
# measures the file that actually ships. A per-channel intermediate directory
# keeps this pack's wrapper build isolated from any other channel packed into
# the same $outputRootResolved.
#
# The SHA-256 of the just-produced Velopack Setup.exe is captured BEFORE the
# build, from $setupPath, which nothing has touched yet.
$velopackSetupSha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash

$wrapperProject = Join-Path $repoRoot "src\Syrtis.Installer\Syrtis.Installer.csproj"
$wrapperBuildDir = Join-Path $outputRootResolved ("installer-wrapper\{0}" -f $channel)
Invoke-Captured -Command "dotnet" -Arguments @(
    "build", $wrapperProject,
    "-c", "Release",
    "-p:SyrtisPayloadPath=$setupPath",
    "-o", $wrapperBuildDir
) -FailureMessage "Install wizard wrapper build failed"

$wrapperExePath = Join-Path $wrapperBuildDir "Syrtis.Installer.exe"
if (-not (Test-Path -LiteralPath $wrapperExePath -PathType Leaf)) {
    throw "Install wizard wrapper build did not produce: $wrapperExePath"
}

# Nothing else in the pipeline would catch a stale or cross-channel payload:
# write-package-evidence.ps1 validates the Setup.exe by name and size only,
# and (measured) the Full/win-x64 Setup at 83,721,388 bytes fits under the
# Full/win-arm64 budget of 87,104,109 -- so an arm64-named installer carrying
# the x64 payload would pass every existing gate. Load the just-built wrapper
# by reflection, from the intermediate directory (never from $setupPath, so
# nothing here locks the file this script is about to overwrite), and refuse
# to ship unless its embedded payload is byte-for-byte this invocation's own
# Velopack Setup.exe.
#
# The read happens in a CHILD PROCESS, and that is not incidental. Every
# wrapper this script builds has the same assembly identity
# ("Syrtis.Installer, Version=<repo version>"), and Assembly.LoadFrom loads
# into the process-wide default context. Packing two RIDs from one PowerShell
# session therefore failed the second one -- measured on the host:
#
#   Could not load file or assembly 'Syrtis.Installer, Version=0.2.2.0,
#   Culture=neutral, PublicKeyToken=null'. Assembly with same name is
#   already loaded
#
# A guard whose whole purpose is to catch a cross-channel payload was
# rejecting a correct package for a reason that had nothing to do with it.
# A fresh process cannot collide with anything; a collectible
# AssemblyLoadContext would also work and is more moving parts for the same
# answer.
$readPayloadHash = @'
param([string]$WrapperPath)
$ErrorActionPreference = "Stop"
$asm = [Reflection.Assembly]::LoadFrom($WrapperPath)
$stream = $asm.GetManifestResourceStream("Syrtis.Installer.payload.setup.exe")
if ($null -eq $stream) { exit 2 }
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $bytes = $sha.ComputeHash($stream) } finally { $sha.Dispose(); $stream.Dispose() }
[System.BitConverter]::ToString($bytes).Replace("-", "")
'@
$readerScript = Join-Path $wrapperBuildDir "read-payload-hash.ps1"
Set-Content -LiteralPath $readerScript -Value $readPayloadHash -Encoding utf8
$payloadSha256 = (& pwsh -NoProfile -NonInteractive -File $readerScript -WrapperPath $wrapperExePath) | Select-Object -Last 1
if ($LASTEXITCODE -eq 2) {
    throw "Install wizard wrapper carries no embedded payload resource: $wrapperExePath"
}
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($payloadSha256)) {
    throw "Could not read the embedded payload from $wrapperExePath (exit $LASTEXITCODE)."
}
$payloadSha256 = $payloadSha256.Trim()
if ($payloadSha256 -ne $velopackSetupSha256) {
    throw ("Embedded payload SHA-256 mismatch: wrapper carries {0}, this invocation's Velopack Setup.exe is {1}. " -f `
        $payloadSha256, $velopackSetupSha256) + `
        "Refusing to ship a stale or cross-channel payload."
}
Write-Output ("Embedded payload verified: SHA-256 {0} matches this invocation's Velopack Setup.exe" -f $payloadSha256)

Copy-Item -LiteralPath $wrapperExePath -Destination $setupPath -Force
Write-Output ("Replaced {0} with the install wizard wrapper ({1} bytes)." -f $expectedSetupName, (Get-Item -LiteralPath $setupPath).Length)

$setupBytes = [int64](Get-Item -LiteralPath $setupPath).Length
$setupKey = "{0}|{1}|setup" -f $DeploymentMode, $Rid
if (-not $nupkgSetupBudgetByModeRid.ContainsKey($setupKey)) {
    throw "No Setup.exe size budget for $setupKey"
}
Assert-SizeBudget -Label "Setup.exe" -Measured $setupBytes -Budget ([int64]$nupkgSetupBudgetByModeRid[$setupKey])
Write-Output ("Size budgets ok: nupkg={0} setup={1} ({2})" -f $nupkgBytes, $setupBytes, $expectedSetupName)
