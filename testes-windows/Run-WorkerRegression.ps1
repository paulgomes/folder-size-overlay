[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Este roteiro exercita o leitor em Windows com .NET Framework 4.8.'
}
$netFramework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
if ([int]$netFramework.Release -lt 528040) { throw '.NET Framework 4.8 ou mais recente é necessário.' }

$sourceExe = (Resolve-Path -LiteralPath $Executable).Path
$sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$assembly = [Reflection.AssemblyName]::GetAssemblyName($sourceExe)
if ($assembly.Version.Major -lt 5) { throw 'Selecione o executável da versão 5 ou mais recente, que contém o leitor isolado.' }
$runRoot = Join-Path $env:TEMP 'PastaGB-worker-verificacoes'
$runDirectory = Join-Path $runRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $runDirectory)
$copiedExe = Join-Path $runDirectory ($assembly.Name + '.exe')
Copy-Item -LiteralPath $sourceExe -Destination $copiedExe
if (Test-Path -LiteralPath ($sourceExe + '.config')) {
    Copy-Item -LiteralPath ($sourceExe + '.config') -Destination ($copiedExe + '.config')
}
if ((Get-FileHash -LiteralPath $copiedExe -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'A cópia de teste não corresponde ao executável selecionado.'
}
$sourceHash.ToLowerInvariant() | Set-Content -LiteralPath (Join-Path $runDirectory 'PASTAGB-WORKER-TEST-OWNED.txt') -Encoding UTF8

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Compilador do .NET Framework não encontrado.' }
$harness = Join-Path $runDirectory 'ScanWorkerWindowsTests.exe'
$compilerArguments = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/langversion:5', '/codepage:65001',
    ('/out:' + $harness), ('/reference:' + $copiedExe),
    ('/reference:' + (Join-Path $frameworkRoot 'System.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Core.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Web.Extensions.dll')),
    (Join-Path $PSScriptRoot 'ScanWorkerWindowsTests.cs')
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'A fixture não compilou contra a API do executável selecionado.' }

$harnessConfiguration = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
'@
$harnessConfiguration | Set-Content -LiteralPath ($harness + '.config') -Encoding UTF8
$windowsVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$manifest = [ordered]@{
    source_executable = $sourceExe
    source_sha256 = $sourceHash.ToLowerInvariant()
    tested_copy = $copiedExe
    assembly_version = $assembly.Version.ToString()
    started_at = (Get-Date).ToString('o')
    windows_build = [string]$windowsVersion.CurrentBuildNumber
    windows_revision = $windowsVersion.UBR
    powershell = $PSVersionTable.PSVersion.ToString()
    net_framework_release = $netFramework.Release
    windows_ui_executed = $false
    test_state = 'PREPARED'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'execucao-worker.json') -Encoding UTF8

Write-Host 'O roteiro cria arquivos pequenos em uma pasta temporária exclusiva e inicia leitores do EXE copiado.'
Write-Host 'Serão verificados: totais, dependências, subtotais, configuração de CPU ou fallback, cancelamento e saída dos processos.'
Write-Host ('Executável SHA256: ' + $sourceHash)
& $harness $copiedExe $runDirectory
$testExit = $LASTEXITCODE
$manifest['finished_at'] = (Get-Date).ToString('o')
$manifest['harness_exit_code'] = $testExit
$manifest['test_state'] = if ($testExit -eq 0) { 'EXECUTED_PASS' } else { 'EXECUTED_FAIL' }
$manifest['tested_copy_sha256_after'] = (Get-FileHash -LiteralPath $copiedExe -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'execucao-worker.json') -Encoding UTF8
if ($manifest['tested_copy_sha256_after'] -ne $sourceHash.ToLowerInvariant()) {
    throw 'O executável mudou durante a execução; descarte essa aprovação.'
}
Write-Host ('Relatório nativo: ' + (Join-Path $runDirectory 'resultado-worker-windows.json'))
if ($testExit -ne 0) { throw 'Uma verificação do leitor falhou. Consulte o relatório.' }
Write-Host 'Fixture concluída. CONFIRMED informa configuração de CPU consultada; FALLBACK informa limitação explícita do ambiente.'
Write-Host 'Este roteiro não aprova interface visual, DPI, retirada de mídia ou percentual real de CPU sob carga.'
