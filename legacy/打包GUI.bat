@echo off
rem Build the native WPF GUI (ASCII only + CRLF, keep it that way).
setlocal
cd /d "%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "XAML=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"

echo Building gui\VideoCompare.exe ...
"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ ^
  -out:gui\VideoCompare.exe -win32icon:build\icon.ico -win32manifest:gui\app.manifest ^
  -r:"%WPF%\PresentationFramework.dll" -r:"%WPF%\PresentationCore.dll" ^
  -r:"%WPF%\WindowsBase.dll" -r:"%XAML%" ^
  -r:System.Windows.Forms.dll -r:Microsoft.VisualBasic.dll ^
  gui\MainWindow.cs gui\PaneView.cs gui\MediaProbe.cs gui\FfmpegHelper.cs ^
  gui\Prompt.cs gui\Styles.cs gui\PrefetchCache.cs
if errorlevel 1 ( echo [FAILED] build error & pause & exit /b 1 )

rem Copy over the exe already sitting in dist (wildcard keeps its localized name).
for %%F in ("dist\*.exe") do copy /y gui\VideoCompare.exe "%%~fF" >nul
echo Done: dist\*.exe
pause
