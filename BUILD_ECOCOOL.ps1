param(
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install a supported .NET 8 SDK, then rerun this script.'
}

$requiredSdk = '8.0.425'
$sdkLines = @(dotnet --list-sdks)
if (-not ($sdkLines | Where-Object { $_ -match ('^' + [regex]::Escape($requiredSdk) + '\s') })) {
    throw "The verified build SDK $requiredSdk is required. Install Microsoft.DotNet.SDK.8 version $requiredSdk, then rerun this script."
}

# global.json is canonical for the build toolchain. This guard prevents a newer SDK
# (for example .NET 10) from silently compiling a net8.0 target on a developer machine.
$resolvedSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedSdk -ne $requiredSdk) {
    throw "SDK selection mismatch. Expected $requiredSdk from global.json, but dotnet resolved '$resolvedSdk'."
}
Write-Host "Verified build SDK: $resolvedSdk"

$targetDist = Join-Path $root 'dist\win-x64'
$dist = Join-Path $root ('dist\build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dist -Force | Out-Null
try {

Write-Host 'Running static EcoCool safety checks...'
if (Get-Command python -ErrorAction SilentlyContinue) {
    python tools\ecocool-static-check.py
    if ($LASTEXITCODE -ne 0) { throw 'EcoCool static safety check failed.' }
    python tools\lang-check.py
    if ($LASTEXITCODE -ne 0) { throw 'Language contract check failed.' }
} else {
    throw 'Python is required: refusing to publish without source-level safety checks.'
}

dotnet run --project .\Tests\SafetyHarness.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Behavioral safety harness failed.' }

Write-Host 'Restoring NuGet dependencies for win-x64...'
# Publish below is runtime-specific (-r win-x64) and uses --no-restore. Restore MUST
# therefore resolve the same RID; otherwise project.assets.json has only the generic
# net8.0-windows target and publish fails with NETSDK1047.
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
dotnet restore .\GhostDeck.csproj -r win-x64 --locked-mode -p:TreatWarningsAsErrors=true "-p:SelfContained=$selfContainedValue"
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

$assetsPath = Join-Path $root 'obj\project.assets.json'
if (-not (Test-Path $assetsPath)) {
    throw "Restore completed but $assetsPath was not produced."
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$ridTarget = @($assets.targets.PSObject.Properties.Name | Where-Object { $_ -match '/win-x64$' } | Select-Object -First 1)
if ($ridTarget.Count -eq 0) {
    throw 'Restore verification failed: project.assets.json has no win-x64 target. Refusing publish.'
}
Write-Host "Verified restore target: $($ridTarget[0])"

$args = @(
    'publish', '.\GhostDeck.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '--no-restore',
    '-o', $dist,
    '-p:PublishSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=true'
)
if ($SelfContained) { $args += '-p:SelfContained=true' } else { $args += '-p:SelfContained=false' }

Write-Host 'Publishing GhostDeck EcoCool Focus...'
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $dist 'GhostDeckEcoCool.exe'
if (-not (Test-Path $exe)) { throw "Publish completed but $exe was not produced." }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
@(
    "GhostDeckEcoCool.exe  SHA-256  $hash",
    "Built: $(Get-Date -Format o)",
    "Source version: 1.39.2 EcoCool Focus",
    'Hardware control: LOCKED; exact model/BIOS/EC review pending'
) | Set-Content -LiteralPath (Join-Path $dist 'SHA256.txt') -Encoding UTF8

Copy-Item .\FIRST_RUN_ECOCOOL.txt $dist
Copy-Item .\ECOCOOL_SAFETY_NOTES.md $dist
Copy-Item .\LICENSE $dist
Copy-Item .\THIRD-PARTY-NOTICES.md $dist
Copy-Item .\TRADEMARK.md $dist
Copy-Item .\licenses (Join-Path $dist 'licenses') -Recurse

# Publish the directory only after every check and copy succeeds. Retain the exact
# previous distribution as a canonical rollback ZIP before replacing it.
$previous = Join-Path $root 'dist\win-x64.previous'
if (Test-Path -LiteralPath $previous) {
    throw "Previous interrupted promotion found at $previous. Inspect and recover it before building again."
}
if (Test-Path -LiteralPath $targetDist) {
    $releaseDir = Join-Path $root 'releases'
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    Compress-Archive -LiteralPath $targetDist -DestinationPath (Join-Path $releaseDir 'previous-dist.zip') -Force
    Move-Item -LiteralPath $targetDist -Destination $previous
}
try { Move-Item -LiteralPath $dist -Destination $targetDist }
catch {
    if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $targetDist }
    throw
}
if (Test-Path -LiteralPath $previous) {
    # This exact, build-owned directory is archived above. Never delete a computed
    # recursive target without verifying its resolved absolute workspace location.
    $resolvedPrevious = (Resolve-Path -LiteralPath $previous).Path
    $expectedPrevious = [IO.Path]::GetFullPath((Join-Path $root 'dist\win-x64.previous'))
    if ($resolvedPrevious -ne $expectedPrevious) { throw 'Rollback cleanup path mismatch.' }
    Remove-Item -LiteralPath $resolvedPrevious -Recurse -Force
}
$exe = Join-Path $targetDist 'GhostDeckEcoCool.exe'
}
finally {
    if (Test-Path -LiteralPath $dist) {
        $resolvedStaging = (Resolve-Path -LiteralPath $dist).Path
        $expectedStaging = [IO.Path]::GetFullPath($dist)
        $stagingParent = [IO.Path]::GetDirectoryName($resolvedStaging)
        if ($resolvedStaging -ne $expectedStaging -or
            $stagingParent -ne [IO.Path]::GetFullPath((Join-Path $root 'dist')) -or
            [IO.Path]::GetFileName($resolvedStaging) -notmatch '^build-[a-f0-9]{32}$') {
            throw 'Staging cleanup path mismatch.'
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

Write-Host ''
Write-Host 'BUILD PASS'
Write-Host "EXE: $exe"
Write-Host "SHA-256: $hash"
