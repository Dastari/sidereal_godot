param([switch]$Build)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$dirty = & git status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the working copy.' }
if ($dirty) { throw 'Save and commit your local editor changes before updating.' }
& git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw 'Pull failed; the working copy was not reset.' }
if ($Build) {
    & dotnet restore Sidereal.Godot.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & dotnet build Sidereal.Godot.csproj --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
Write-Host 'Updated. Refocus Godot and reload changed scenes when prompted.'
