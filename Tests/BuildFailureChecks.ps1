$ErrorActionPreference='Stop'
$workspaceRoot=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $workspaceRoot
$before=@(Get-ChildItem -LiteralPath dist/win-x64 -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
# This process-local mock injects restore failure; no package, source or system setting changes.
function global:dotnet {
    if ($args[0] -eq '--list-sdks') { $global:LASTEXITCODE=0; '8.0.425 [fault-injection fixture]'; return }
    if ($args[0] -eq '--version') { $global:LASTEXITCODE=0; '8.0.425'; return }
    if ($args[0] -eq 'run') { $global:LASTEXITCODE=0; return }
    if ($args[0] -eq 'restore') { $global:LASTEXITCODE=1; 'INJECTED restore failure'; return }
    throw 'Unexpected build action in fault injection.'
}
$failedAsExpected=$false
try { & (Join-Path $workspaceRoot 'BUILD_ECOCOOL.ps1') }
catch { if ($_.Exception.Message -notmatch 'dotnet restore failed') { throw }; $failedAsExpected=$true }
if (-not $failedAsExpected) { throw 'Failure injection did not reach the intended gate.' }
$after=@(Get-ChildItem -LiteralPath dist/win-x64 -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
if (($before -join "`n") -cne ($after -join "`n")) { throw 'Prior distribution changed after restore failure.' }
if (@(Get-ChildItem -LiteralPath dist -Directory -Filter 'build-*').Count -gt 0) { throw 'Failed build left active staging.' }
'PASS injected restore failure preserved every distribution byte and cleaned its staging.'
exit 0
