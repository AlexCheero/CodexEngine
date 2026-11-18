param([Parameter(Mandatory=$true)][string]$EditorDirectory)
$ErrorActionPreference = 'Stop'

function AbsolutePath([string]$path) {
    if ($path.StartsWith('\??\')) { $path = $path.Substring(4) }
    if ($path.StartsWith('\\?\UNC\')) { $path = '\\' + $path.Substring(8) }
    if ($path.StartsWith('\\?\')) { $path = $path.Substring(4) }
    return [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
}

$source = AbsolutePath (Join-Path $PSScriptRoot 'addons/codex_engine')
if (![IO.File]::Exists((Join-Path $source 'EntityAuthoring.cs'))) {
    throw "The engine addon is missing at '$source'. Initialize the engine submodule."
}
$editor = (Get-Item -LiteralPath $EditorDirectory -Force).FullName
$addons = Join-Path $editor 'addons'
[IO.Directory]::CreateDirectory($addons) | Out-Null
$mount = Join-Path $addons 'codex_engine'

# Enumerating the parent also finds a junction whose previous target no longer exists.
if ([IO.Directory]::GetFileSystemEntries($addons, 'codex_engine').Count -gt 0) {
    $entry = Get-Item -LiteralPath $mount -Force
    if (-not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $entry.LinkType -ne 'Junction') {
        throw "A path already exists at '$mount'. Move it aside before mounting the engine addon."
    }
    $target = [string](@($entry.Target)[0])
    if ($target -and [String]::Equals((AbsolutePath $target), $source, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    # Delete only the directory entry. Never descend into the old engine checkout.
    [IO.Directory]::Delete($mount, $false)
}

New-Item -ItemType Junction -Path $mount -Target $source | Out-Null
