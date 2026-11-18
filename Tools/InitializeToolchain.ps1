param(
    [Parameter(Mandatory)][string]$ToolDirectory,
    [switch]$RuntimeOnly
)
$ErrorActionPreference = 'Stop'
$toolDir = $ToolDirectory
$sdk = Join-Path $toolDir 'sdk/dotnet.exe'
$godotDir = Join-Path $toolDir 'godot/Godot_v4.7.2-stable_mono_win64'
$godot = Join-Path $godotDir 'Godot_v4.7.2-stable_mono_win64_console.exe'
function Get-Portable([string]$url, [string]$algorithm, [string]$digest, [string]$destination) {
    $archive = Join-Path $toolDir ([IO.Path]::GetRandomFileName()+'.zip')
    try {
        Invoke-WebRequest -UseBasicParsing $url -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm $algorithm).Hash -ne $digest) { throw "Checksum mismatch for $url" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive,$destination)
    } finally { if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive } }
}
New-Item -Force -ItemType Directory -Path $toolDir | Out-Null
if (!(Test-Path $sdk)) {
    Get-Portable 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip' SHA512 '330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee' (Join-Path $toolDir 'sdk')
}
if (!$RuntimeOnly -and !(Test-Path $godot)) {
    Get-Portable 'https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_win64.zip' SHA256 'a2a48473a7414c5f19fab690518caebb738c09ef9601f6bd2388676a7f53b3c0' (Join-Path $toolDir 'godot')
}
$env:DOTNET_ROOT = Split-Path $sdk
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = $env:DOTNET_ROOT+';'+$env:PATH
$env:DOTNET_CLI_HOME = $toolDir
$env:NUGET_PACKAGES = Join-Path $toolDir 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ROLL_FORWARD = 'Major'
[pscustomobject]@{
    DotNet = $sdk
    GodotConsole = $godot
    GodotEditor = Join-Path $godotDir 'Godot_v4.7.2-stable_mono_win64.exe'
}
