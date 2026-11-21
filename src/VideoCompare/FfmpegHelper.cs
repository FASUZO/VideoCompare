using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VideoCompare
{
    /// <summary>ffmpeg 封装：不支持的封装/编码先“转封装”，不行再转码片段。</summary>
    public static class FfmpegHelper
    {
        public static string FfmpegPath = Find("ffmpeg.exe", MediaProbe.FfprobePath);

        public static string CacheDir = EnsureCacheDir();

        /// <summary>缓存目录：数据目录下的 cache（不可写时退回程序目录，失败也不挡启动）。</summary>
        static string EnsureCacheDir()
        {
            try
            {
                var dir = Path.Combine(AppSettings.DataDir, "cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "VideoCompare-cache");
        }

        /// <summary>缓存目录当前占用（字节）。</summary>
        public static long CacheBytes()
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.GetFiles(CacheDir))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>把缓存压到 limitMB 以内（从最旧的开始删），返回删除的文件数。</summary>
        public static int TrimCache(int limitMB)
        {
            if (limitMB < 1) limitMB = 1;
            long limit = (long)limitMB * 1024L * 1024L;
            int removed = 0;
            try
            {
                var files = new List<FileInfo>();
                foreach (var f in Directory.GetFiles(CacheDir))
                {
                    try { files.Add(new FileInfo(f)); } catch { }
                }
                long total = 0;
                foreach (var fi in files) total += fi.Length;
                if (total <= limit) return 0;
                files.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                foreach (var fi in files)
                {
                    if (total <= limit) break;
                    try
                    {
                        long len = fi.Length;
                        fi.Delete();
                        total -= len;
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>清空全部缓存（正在使用的片段删不掉时会跳过，返回删除数）。</summary>
        public static int ClearCache()
        {
            int removed = 0;
            try
            {
                foreach (var f in Directory.GetFiles(CacheDir))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        static string Find(string exeName, string siblingPath)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(exeDir, "ffmpeg", "bin", exeName),
                Path.Combine(exeDir, "ffmpeg", exeName),
                Path.Combine(exeDir, "bin", exeName),
                Path.Combine(exeDir, exeName),
                // 便携用法：把 ffmpeg 解到 %LOCALAPPDATA%\VideoCompare\ffmpeg\bin 也能被找到
                Path.Combine(AppSettings.DataDir, "ffmpeg", "bin", exeName),
                Path.Combine(AppSettings.DataDir, "ffmpeg", exeName),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            if (!string.IsNullOrEmpty(siblingPath))
            {
                try
                {
                    var p = Path.Combine(Path.GetDirectoryName(siblingPath), exeName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        static string Key(string path, string tag)
        {
            try
            {
                var fi = new FileInfo(path);
                string raw = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + tag;
                using (var sha = SHA1.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    var sb = new StringBuilder();
                    for (int i = 0; i < 10; i++) sb.Append(bytes[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return Guid.NewGuid().ToString("N").Substring(0, 20); }
        }

        /// <summary>判断能否被 MediaElement 直接播放（仅做初步判断，真失败还会回退）。</summary>
        public static bool LikelyPlayable(MediaInfo m)
        {
            if (m == null) return true;
            bool containerOk = m.Container == "MP4" || m.Container == "QuickTime MOV";
            bool codecOk = m.VideoCodecId == "h264" || m.VideoCodecId == "hevc" || m.VideoCodecId == "av1"
                           || m.VideoCodecId == "mpeg4" && m.Container == "AVI";
            return containerOk && codecOk;
        }

        /// <summary>运行 ffmpeg，回调进度 0..1。</summary>
        public static bool Run(string args, Action<double> onProgress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(FfmpegPath)) return false;
            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (token.IsCancellationRequested) { try { p.Kill(); } catch { } return false; }
                    if (line.StartsWith("out_time_us="))
                    {
                        long us;
                        if (long.TryParse(line.Substring(12), out us) && us >= 0 && onProgress != null)
                            onProgress(us / 1e6);
                    }
                }
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }

        /// <summary>转封装：只换容器/音频编码，视频流直接 copy（几秒完成）。</summary>
        public static string Remux(string source, MediaInfo m, Action<double> onProgress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(FfmpegPath)) return null;
            string outPath = Path.Combine(CacheDir, Key(source, "remux") + ".mp4");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 4096) return outPath;

            string args = "-v error -y -nostdin -i \"" + source + "\" " +
                          "-map 0:v:0 -map 0:a:0? -c:v copy -c:a aac -b:a 192k -ac 2 " +
                          "-movflags +faststart \"" + outPath + "\"";
            bool ok = Run(args, null, token);
            if (ok && File.Exists(outPath) && new FileInfo(outPath).Length > 4096) return outPath;
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            return null;
        }

        /// <summary>转码一个片段（默认 60 秒，最长边 1920/1080）。</summary>
        public static string TranscodeClip(string source, double startSeconds, double windowSeconds,
                                           Action<double> onProgress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(FfmpegPath)) return null;
            string tag = string.Format(CultureInfo.InvariantCulture, "clip-{0:0}-{1:0}", startSeconds, windowSeconds);
            string outPath = Path.Combine(CacheDir, Key(source, tag) + ".mp4");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 4096) return outPath;

            string args = "-v error -y -nostdin ";
            if (startSeconds > 0)
                args += "-ss " + startSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " ";
            if (windowSeconds > 0)
                args += "-t " + windowSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " ";
            args += "-i \"" + source + "\" -map 0:v:0 -map 0:a:0? " +
                    "-vf \"scale='min(1920,iw)':'min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2\" " +
                    "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 192k -ac 2 -movflags +faststart -progress pipe:1 -nostats \"" + outPath + "\"";
            bool ok = Run(args, onProgress, token);
            if (ok && File.Exists(outPath) && new FileInfo(outPath).Length > 4096) return outPath;
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            return null;
        }
    }
}
