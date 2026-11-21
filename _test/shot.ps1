# Captures the main window of a running process into a PNG (dev helper).
#   powershell -ExecutionPolicy Bypass -File _test\shot.ps1 -ProcName VideoCompare -Out docs\screenshot.png -MaxWidth 1400
# ASCII only on purpose (Windows PowerShell 5.1 + BOM-less files).
param(
    [string]$ProcName = 'VideoCompare',
    [string]$Out = 'shot.png',
    [int]$MaxWidth = 0,
    [int]$DelayMs = 800
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('WinCap' -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinCap {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
}

[WinCap]::SetProcessDPIAware() | Out-Null

$procs = @(Get-Process -Name $ProcName -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { throw "process '$ProcName' is not running" }
# note: $pid is a PowerShell automatic variable - do not reuse that name
$targetIds = @($procs | ForEach-Object { $_.Id })

$best = [IntPtr]::Zero
$bestLen = -1
$cb = [WinCap+EnumProc]{
    param($h, $l)
    $wpid = [uint32]0
    [WinCap]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if (($script:targetIds -contains [int]$wpid) -and [WinCap]::IsWindowVisible($h)) {
        $len = [WinCap]::GetWindowTextLength($h)
        if ($len -gt $script:bestLen) { $script:bestLen = $len; $script:best = $h }
    }
    return $true
}
[WinCap]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($best -eq [IntPtr]::Zero) { throw "no visible window for '$ProcName'" }

[WinCap]::ShowWindow($best, 9) | Out-Null
[WinCap]::SetForegroundWindow($best) | Out-Null
Start-Sleep -Milliseconds $DelayMs

$r = New-Object WinCap+RECT
[WinCap]::GetWindowRect($best, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()

if ($MaxWidth -gt 0 -and $w -gt $MaxWidth) {
    $nh = [int]([math]::Round($h * $MaxWidth / $w))
    $small = New-Object System.Drawing.Bitmap $MaxWidth, $nh
    $gs = [System.Drawing.Graphics]::FromImage($small)
    $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gs.DrawImage($bmp, 0, 0, $MaxWidth, $nh)
    $gs.Dispose()
    $bmp.Dispose()
    $bmp = $small
}

$full = [System.IO.Path]::GetFullPath($Out)
$dir = [System.IO.Path]::GetDirectoryName($full)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ("saved {0} ({1:N0} bytes)" -f $full, (Get-Item $full).Length)
