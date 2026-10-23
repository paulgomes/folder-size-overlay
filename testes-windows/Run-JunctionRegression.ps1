[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Este roteiro exige Windows. Nenhuma chamada nativa foi executada.'
}
$sourceExe = (Resolve-Path -LiteralPath $Executable).Path
$sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($sourceExe).Name
$runDirectory = Join-Path $env:TEMP ('PastaGB-juncoes-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $runDirectory)
$copiedExe = Join-Path $runDirectory ($assemblyName + '.exe')
Copy-Item -LiteralPath $sourceExe -Destination $copiedExe
if (Test-Path -LiteralPath ($sourceExe + '.config')) {
    Copy-Item -LiteralPath ($sourceExe + '.config') -Destination ($copiedExe + '.config')
}
if ((Get-FileHash -LiteralPath $copiedExe -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'A copia de teste difere do EXE selecionado.'
}
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Compilador do .NET Framework nao encontrado.' }
$fixtureSource = Join-Path $PSScriptRoot 'ScannerWindowsFixtureTests.cs'
$harness = Join-Path $runDirectory 'ScannerWindowsFixtureTests.exe'
$compilerArguments = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/langversion:5', '/codepage:65001',
    ('/out:' + $harness), ('/reference:' + $copiedExe),
    ('/reference:' + (Join-Path $frameworkRoot 'System.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Core.dll')), $fixtureSource
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Fixture nao compilou. Nenhuma aprovacao nativa registrada.' }
$stdout = Join-Path $runDirectory 'stdout.txt'
$stderr = Join-Path $runDirectory 'stderr.txt'
Write-Host 'Criando e removendo apenas juncoes locais do cenario temporario proprio.'
$process = Start-Process -FilePath $harness -WorkingDirectory $runDirectory -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$report = [ordered]@{
    timestamp = (Get-Date).ToString('o')
    executable = $sourceExe
    sha256 = $sourceHash.ToLowerInvariant()
    assembly_version = [Reflection.AssemblyName]::GetAssemblyName($copiedExe).Version.ToString()
    windows_version = [Environment]::OSVersion.ToString()
    windows_native_execution = $true
    windows_ui_execution = $false
    exit_code = $process.ExitCode
    passed = ($process.ExitCode -eq 0)
    stdout = (Get-Content -LiteralPath $stdout -Raw)
    stderr = (Get-Content -LiteralPath $stderr -Raw)
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'resultado-juncoes.json') -Encoding UTF8
Write-Host ('Evidencias: ' + $runDirectory)
if ($process.ExitCode -ne 0) { throw 'A verificacao de juncoes falhou; consulte resultado-juncoes.json.' }
Write-Host 'Juncoes do cenario aprovadas. DPI, USB, permissoes e Explorer permanecem verificacoes separadas.'
