# Run from the extracted package under the execution policy allowed by Windows.
# Requires the already-built product EXE/config in the package root.
$ErrorActionPreference = 'Stop'
$productRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Compilador do .NET Framework ausente.' }
$names = @('PastaGBIcones.exe', 'PastaGBIcones.exe.config')
$lines = foreach ($name in $names) {
    $path = Join-Path $productRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Arquivo ausente: $path" }
    ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + $name
}
$manifest = Join-Path $PSScriptRoot 'payload-sha256.txt'
[IO.File]::WriteAllText($manifest, (($lines -join "`n") + "`n"), [Text.Encoding]::ASCII)
$output = Join-Path $productRoot 'PastaGB-Instalar.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/langversion:5', '/codepage:65001',
    ('/out:' + $output), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/reference:' + (Join-Path $framework 'System.dll')),
    ('/reference:' + (Join-Path $framework 'System.Core.dll')),
    ('/reference:' + (Join-Path $framework 'System.Drawing.dll')),
    ('/reference:' + (Join-Path $framework 'System.Windows.Forms.dll')))
foreach ($name in $names) { $arguments += '/resource:' + (Join-Path $productRoot $name) + ',' + $name }
$arguments += '/resource:' + $manifest + ',payload-sha256.txt'
$arguments += Join-Path $PSScriptRoot 'Setup.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PastaGB-Instalar.exe.config') -Destination ($output + '.config') -Force
Write-Output ('Instalador compilado: ' + $output)
Get-FileHash -LiteralPath $output -Algorithm SHA256
