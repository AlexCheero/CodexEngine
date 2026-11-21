param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string[]]$Effects = @('Surface', 'Character', 'AmbientOcclusion', 'Post', 'CrossCubeSky', 'SrgbSprite', 'ShadowTiles', 'Temporal')
)
$ErrorActionPreference = 'Stop'
$kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
$compiler = if ($kits.KitsRoot10) {
    Get-ChildItem -Directory -LiteralPath (Join-Path $kits.KitsRoot10 'bin') |
        Where-Object { $_.Name -match '^\d+(\.\d+){3}$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\fxc.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if (!$compiler) { throw 'Install the Windows SDK with the x64 FXC shader compiler to build the FNA effects.' }

$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$temporary = Join-Path $output ([guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    foreach ($effect in $effects) {
        $source = Join-Path $SourceDirectory ($effect + '.fx')
        $binary = Join-Path $temporary ($effect + '.fxb')
        & $compiler /nologo /T fx_2_0 /I $SourceDirectory /Fo $binary $source
        if ($LASTEXITCODE -ne 0) { throw "FXC failed for $source (exit $LASTEXITCODE)." }
    }
    foreach ($effect in $effects) {
        $binary = Join-Path $temporary ($effect + '.fxb')
        $destination = Join-Path $output ($effect + '.fxb')
        try {
            [IO.File]::Move($binary, $destination)
        } catch [IO.IOException] {
            if (![IO.File]::Exists($destination)) { throw }
            [IO.File]::Replace($binary, $destination, [System.Management.Automation.Language.NullString]::Value)
        }
    }
} finally {
    foreach ($effect in $effects) { [IO.File]::Delete((Join-Path $temporary ($effect + '.fxb'))) }
    [IO.Directory]::Delete($temporary)
}
