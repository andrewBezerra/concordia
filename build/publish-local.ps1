# Builds concordia-hook with Native AOT for this Windows machine and places it where the plugin
# launcher looks for it. Requires the "Desktop development with C++" workload (MSVC linker).
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$rid = "win-$arch"
$publish = Join-Path $root "artifacts/publish/$rid"
$out = Join-Path $root "plugins/concordia/bin/$rid"

dotnet publish (Join-Path $root 'src/Concordia.Hook/Concordia.Hook.csproj') -c Release -r $rid -o $publish --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $publish 'concordia-hook.exe') $out -Force

Write-Host "Published $rid -> $out"
& (Join-Path $out 'concordia-hook.exe') version
