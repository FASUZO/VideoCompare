# Generates the sample media used by the self tests. Requires ffmpeg/ffprobe on PATH.
# ASCII only on purpose (Windows PowerShell 5.1 + BOM-less files).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root 'media'
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw "ffmpeg not found on PATH - install it (choco install ffmpeg) or add it to PATH"
}

# NOTE: the parameter cannot be called $args - that is an automatic variable.
function Shot($name, $ffArgs) {
    $path = Join-Path $out $name
    Write-Host "==> $name"
    & ffmpeg -y -hide_banner -loglevel error @ffArgs $path
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $name" }
}

# 1) H.264 + AAC, 720p25, 6s
Shot '01_h264_25fps.mp4' @(
    '-f','lavfi','-i','testsrc2=size=1280x720:rate=25:duration=6',
    '-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=6',
    '-c:v','libx264','-preset','veryfast','-pix_fmt','yuv420p','-g','50',
    '-c:a','aac','-b:a','128k'
)

# 2) HEVC 10bit + E-AC-3 5.1 in MKV, 5s
Shot '02_hevc_aac.mkv' @(
    '-f','lavfi','-i','testsrc2=size=1280x720:rate=25:duration=5',
    '-f','lavfi','-i','sine=frequency=330:sample_rate=48000:duration=5',
    '-c:v','libx265','-preset','veryfast','-pix_fmt','yuv420p10le','-x265-params','log-level=error',
    '-c:a','eac3','-ac','6','-b:a','384k'
)

# 3) ProRes 422 + PCM in MOV, 4s
Shot '03_prores422.mov' @(
    '-f','lavfi','-i','testsrc2=size=960x540:rate=25:duration=4',
    '-f','lavfi','-i','sine=frequency=550:sample_rate=48000:duration=4',
    '-c:v','prores_ks','-profile:v','2','-pix_fmt','yuv422p10le',
    '-c:a','pcm_s16le'
)

# 4) VP9 + Opus in WebM, 5s
Shot '04_vp9_opus.webm' @(
    '-f','lavfi','-i','testsrc2=size=1280x720:rate=25:duration=5',
    '-f','lavfi','-i','sine=frequency=660:sample_rate=48000:duration=5',
    '-c:v','libvpx-vp9','-deadline','realtime','-cpu-used','8','-b:v','1M',
    '-c:a','libopus','-b:a','96k'
)

# 5) MPEG-4 + MP3 in AVI, 4s (for the fallback/remux path)
Shot '05_mpeg4_mp3.avi' @(
    '-f','lavfi','-i','testsrc2=size=640x480:rate=25:duration=4',
    '-f','lavfi','-i','sine=frequency=770:sample_rate=44100:duration=4',
    '-c:v','mpeg4','-q:v','5','-c:a','libmp3lame','-b:a','128k'
)

# 6) Vertical 720x1280, 3s
Shot '06_vertical_720x1280.mp4' @(
    '-f','lavfi','-i','testsrc2=size=720x1280:rate=30:duration=3',
    '-c:v','libx264','-preset','veryfast','-pix_fmt','yuv420p'
)

# 7) 90s clip used by --seektest / --clicktest
Shot '07_long_90s.mp4' @(
    '-f','lavfi','-i','testsrc2=size=960x540:rate=25:duration=90',
    '-f','lavfi','-i','sine=frequency=880:sample_rate=48000:duration=90',
    '-c:v','libx264','-preset','ultrafast','-pix_fmt','yuv420p','-g','25',
    '-c:a','aac','-b:a','128k'
)

Write-Host ""
Write-Host "done -> $out"
Get-ChildItem $out | ForEach-Object { "{0,-28} {1,10:N0} bytes" -f $_.Name, $_.Length }
