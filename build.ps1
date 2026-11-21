# VideoCompare build script.
# Compiles the WPF app with the C# compiler that ships with Windows, so no
# Visual Studio / MSBuild / NuGet is required.
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads
# BOM-less files as ANSI, and non-ASCII text without a BOM breaks parsing.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Zip
#
[CmdletBinding()]
param(
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir  = Join-Path $root 'src\VideoCompare'
$distDir = Join-Path $root 'dist'
$outExe  = Join-Path $distDir 'VideoCompare.exe'

$fw    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc   = Join-Path $fw 'csc.exe'
$wpf   = Join-Path $fw 'WPF'
$xaml  = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'
$icon  = Join-Path $root 'build\icon.ico'
$mf    = Join-Path $srcDir 'app.manifest'

foreach ($p in @($csc, $wpf, $xaml, $icon, $mf)) {
    if (-not (Test-Path $p)) { throw "missing build dependency: $p" }
}

$ver = '0.1.0'
$aiPath = Join-Path $srcDir 'Properties\AssemblyInfo.cs'
if (Test-Path $aiPath) {
    $m = Select-String -Path $aiPath -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)' | Select-Object -First 1
    if ($m) { $ver = $m.Matches[0].Groups[1].Value }
}

$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "no sources found in $srcDir" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $outExe) {
    try { Remove-Item $outExe -Force }
    catch { throw "cannot overwrite $outExe - close the running app first" }
}

# NOTE: do not name this $args - that is a PowerShell automatic variable
$cscArgs = @(
    '-nologo'
    '-target:winexe'
    '-platform:x64'
    '-optimize+'
    '-langversion:5'
    '-warn:4'
    ('-out:' + $outExe)
    ('-win32icon:' + $icon)
    ('-win32manifest:' + $mf)
    ('-lib:' + $wpf)
    '-r:System.dll'
    '-r:System.Core.dll'
    '-r:System.Xaml.dll'
    '-r:System.Windows.Forms.dll'
    '-r:Microsoft.VisualBasic.dll'
    '-r:WindowsBase.dll'
    '-r:PresentationCore.dll'
    '-r:PresentationFramework.dll'
) + $sources

Write-Host "==> compiling $($sources.Count) sources with $csc"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$item = Get-Item $outExe
Write-Host ("==> built {0} ({1:N0} bytes, version {2})" -f $item.FullName, $item.Length, $ver)

if ($Zip) {
    # NOTE: the local variable must not be called $zip - the [switch]$Zip parameter owns that name
    $zipPath = Join-Path $distDir ("video-compare-{0}-win-x64.zip" -f $ver)
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $stage = Join-Path $distDir 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $outExe $stage
    foreach ($doc in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
        $p = Join-Path $root $doc
        if (Test-Path $p) { Copy-Item $p $stage }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
    Remove-Item $stage -Recurse -Force
    Write-Host ("==> packaged {0} ({1:N0} bytes)" -f $zipPath, (Get-Item $zipPath).Length)
}
