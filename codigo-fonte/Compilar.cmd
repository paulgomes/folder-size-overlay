@echo off
setlocal DisableDelayedExpansion
set "PastaGBFramework=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%PastaGBFramework%\csc.exe" set "PastaGBFramework=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%PastaGBFramework%\csc.exe" goto missing
"%PastaGBFramework%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
 /out:"%~dp0..\PastaGBIcones.exe" /win32manifest:"%~dp0app.manifest" ^
 /reference:"%PastaGBFramework%\System.dll" ^
 /reference:"%PastaGBFramework%\System.Core.dll" ^
 /reference:"%PastaGBFramework%\System.Windows.Forms.dll" ^
 /reference:"%PastaGBFramework%\System.Drawing.dll" ^
 /reference:"%PastaGBFramework%\WPF\WindowsBase.dll" ^
 /reference:"%PastaGBFramework%\WPF\UIAutomationClient.dll" ^
 /reference:"%PastaGBFramework%\WPF\UIAutomationTypes.dll" ^
 "%~dp0AdaptiveRefreshPolicy.cs" "%~dp0App.Persistent.cs" "%~dp0App.Scheduling.cs" ^
 "%~dp0App.cs" "%~dp0AppPreferences.cs" "%~dp0BoundedMtaWorker.cs" ^
 "%~dp0CoordinatorPolicy.cs" "%~dp0DesktopItemIndex.cs" "%~dp0DesktopProbe.cs" ^
 "%~dp0DirectoryIdentity.cs" "%~dp0ExplorerProbe.cs" "%~dp0FolderChangeTracker.cs" ^
 "%~dp0FolderEventTracker.cs" "%~dp0FolderResolutionState.cs" "%~dp0IconCoordinator.cs" ^
 "%~dp0LabelAppearance.cs" "%~dp0LabelLayout.cs" "%~dp0LayeredLabel.cs" ^
 "%~dp0LayoutPlanCache.cs" "%~dp0ManagedWorker.cs" "%~dp0MetadataMemoryBudget.cs" ^
 "%~dp0PathRelations.cs" "%~dp0PerformanceDialog.cs" "%~dp0PersistentSizeCache.cs" ^
 "%~dp0PersistentSizeIdentity.cs" "%~dp0PreferencesDialog.cs" "%~dp0ProbeNameIndex.cs" ^
 "%~dp0RuntimeMetrics.cs" "%~dp0ScanProcess.cs" "%~dp0ScanProtocol.cs" ^
 "%~dp0ScanQueuePolicy.cs" "%~dp0Scanner.cs" "%~dp0SizePresentation.cs" ^
 "%~dp0SizeRefreshPolicy.cs" "%~dp0SizeResultPolicy.cs" "%~dp0SurfaceSnapshotPolicy.cs" ^
 "%~dp0UiDispatchWindow.cs" "%~dp0ViewChangePolicy.cs" "%~dp0ViewSnapshot.cs" ^
 "%~dp0VisualSnapshotPolicy.cs" "%~dp0VolumeChangeMonitor.cs" "%~dp0VolumeRevisions.cs"
if errorlevel 1 goto failed
echo PastaGBIcones.exe compilado com sucesso na pasta superior.
pause
exit /b 0
:missing
echo O compilador do .NET Framework nao foi encontrado neste Windows.
pause
exit /b 1
:failed
echo Falha na compilacao. Confira as mensagens acima.
pause
exit /b 1
