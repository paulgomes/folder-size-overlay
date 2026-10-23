[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [string]$OutputDirectory = (Join-Path $env:TEMP 'PastaGB-verificacoes')
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Este roteiro precisa de Windows com uma sessão desktop interativa.'
}
if (-not [Environment]::UserInteractive) {
    throw 'Use uma sessão desktop interativa. Um serviço/agente sem desktop não valida o Explorador.'
}

$sourceExe = (Resolve-Path -LiteralPath $Executable).Path
$sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($sourceExe).Name
$runDirectory = Join-Path $OutputDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[void](New-Item -ItemType Directory -Path $runDirectory)
$copiedExe = Join-Path $runDirectory ($assemblyName + '.exe')
Copy-Item -LiteralPath $sourceExe -Destination $copiedExe
if (Test-Path -LiteralPath ($sourceExe + '.config')) {
    Copy-Item -LiteralPath ($sourceExe + '.config') -Destination ($copiedExe + '.config')
}
if ((Get-FileHash -LiteralPath $copiedExe -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'A cópia de teste não corresponde ao executável selecionado.'
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Compilador do .NET Framework não encontrado.' }
$harness = Join-Path $runDirectory 'ExplorerRegressionHarness.exe'
$compilerArguments = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/langversion:5', '/codepage:65001',
    ('/out:' + $harness), ('/reference:' + $copiedExe),
    ('/reference:' + (Join-Path $frameworkRoot 'System.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Core.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Drawing.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Windows.Forms.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'System.Web.Extensions.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'Microsoft.CSharp.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'WPF\WindowsBase.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'WPF\UIAutomationClient.dll')),
    ('/reference:' + (Join-Path $frameworkRoot 'WPF\UIAutomationTypes.dll')),
    (Join-Path $PSScriptRoot 'ExplorerRegressionHarness.cs')
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'O harness não compilou; nenhuma aprovação de interface foi registrada.' }

$windowsVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$manifest = [ordered]@{
    source_executable = $sourceExe
    source_sha256 = $sourceHash.ToLowerInvariant()
    tested_copy = $copiedExe
    started_at = (Get-Date).ToString('o')
    powershell = $PSVersionTable.PSVersion.ToString()
    windows_build = [string]$windowsVersion.CurrentBuildNumber
    windows_revision = $windowsVersion.UBR
    windows_display_version = $windowsVersion.DisplayVersion
    windows_edition = $windowsVersion.EditionID
    manual_visual_review = 'PENDING'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'execucao.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MATRIZ-VISUAL.md') -Destination (Join-Path $runDirectory 'MATRIZ-VISUAL.md')

Write-Host 'O teste abre uma janela própria do Explorador e cria cerca de 22 MB de arquivos de cenário.'
Write-Host 'O aplicativo selecionado permanece aberto para a inspeção visual. Nenhum arquivo preexistente é alterado.'
Write-Host ('Executável SHA256: ' + $sourceHash)
& $harness $copiedExe $runDirectory
$testExit = $LASTEXITCODE
Write-Host ('Evidências e matriz manual: ' + $runDirectory)
if ($testExit -ne 0) { throw 'Uma verificação automática falhou. Consulte resultado-windows.json.' }
Write-Host 'Automação concluída. Os screenshots, cliques reais, arrastes, DPI e USB ainda exigem revisão manual.'
