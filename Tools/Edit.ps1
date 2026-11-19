param([switch]$Headless)
$ErrorActionPreference = 'Stop'
$engine = Split-Path -Parent $PSScriptRoot
$project = Join-Path $engine 'Godot'
$toolchain = & (Join-Path $PSScriptRoot 'InitializeToolchain.ps1') -ToolDirectory (Join-Path $engine '.tools')

& $toolchain.DotNet build (Join-Path $project 'CodexEditor.csproj') -v:q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Headless) {
    & $toolchain.GodotConsole --headless --editor --import --path $project
    exit $LASTEXITCODE
}

& $toolchain.GodotEditor --editor --path $project
