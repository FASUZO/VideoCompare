using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VideoCompare
{
    /// <summary>媒体元数据（ffprobe flat 输出解析）。</summary>
    public class MediaInfo
    {
        public string Path = "";
        public string Name = "";
        public string Dir = "";

        public string Container = "-";
        public string FormatLong = "";
        public double Duration = 0;
        public long Size = 0;
        public long BitRateTotal = 0;

        public string VideoCodec = "-"; public string VideoCodecId = "";
        public string VideoProfile = ""; public string VideoLevel = "";
        public int Width = 0, Height = 0;
        public double Fps = 0;
        public long VideoBitrate = 0;
        public long Frames = 0;
        public string PixFmt = "";
        public int BitDepth = 0;
        public string ColorDesc = "";
        public int Rotation = 0;

        public bool HasAudio = false;
        public string AudioCodec = ""; public string AudioProfile = "";
        public int Channels = 0; public string ChannelLayout = "";
        public int SampleRate = 0; public int AudioBitDepth = 0;
        public long AudioBitrate = 0; public string AudioLanguage = "";
        public int AudioStreams = 0;

        public int SubtitleStreams = 0;
        public bool IsNetwork = false;
        public List<string> Warnings = new List<string>();

        public string SizeHuman
        {
            get
            {
                double v = Size; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
                while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
                return i == 0 ? ((long)v) + " B" : v.ToString(i == 0 ? "0" : (v < 10 ? "0.00" : "0.0"), CultureInfo.InvariantCulture) + " " + u[i];
            }
        }

        public string DurationText
        {
            get
            {
                if (Duration <= 0) return "-";
                var t = TimeSpan.FromSeconds(Duration);
                return t.TotalHours >= 1
                    ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                    : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
            }
        }

        public string ResolutionText
        {
            get { return Width > 0 && Height > 0 ? Width + " × " + Height : "-"; }
        }

        public string FpsText
        {
            get { return Fps > 0 ? (Math.Abs(Fps - Math.Round(Fps)) < 0.01 ? Math.Round(Fps).ToString() : Fps.ToString("0.###", CultureInfo.InvariantCulture)) + " fps" : "-"; }
        }

        public string BitrateText(long bps)
        {
            if (bps <= 0) return "-";
            return bps >= 1000000
                ? (bps / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps"
                : (bps / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kbps";
        }

        public string AudioText
        {
            get
            {
                if (!HasAudio) return "无音轨";
                var sb = new StringBuilder(AudioCodec);
                if (!string.IsNullOrEmpty(AudioProfile)) sb.Append(" (").Append(AudioProfile).Append(")");
                return sb.ToString();
            }
        }

        public string ChannelText
        {
            get
            {
                if (!HasAudio) return "无音轨";
                if (!string.IsNullOrEmpty(ChannelLayout)) return ChannelLayout;
                return Channels > 0 ? Channels + " 声道" : "-";
            }
        }
    }

    /// <summary>ffprobe flat 输出取值辅助（C# 5 无局部函数）。</summary>
    internal static class D
    {
        public static string Get(Dictionary<string, string> d, string k)
        {
            string v; return d.TryGetValue(k, out v) ? v : null;
        }
        public static double Num(Dictionary<string, string> d, string k)
        {
            double v; var s = Get(d, k);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
        public static long Long(Dictionary<string, string> d, string k)
        {
            long v; var s = Get(d, k);
            return s != null && long.TryParse(s, out v) ? v : 0;
        }
    }

    public static class MediaProbe
    {
        public static string FfprobePath = FindFfprobe();

        static string FindFfprobe()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(exeDir, "ffmpeg", "bin", "ffprobe.exe"),
                Path.Combine(exeDir, "ffmpeg", "ffprobe.exe"),
                Path.Combine(exeDir, "bin", "ffprobe.exe"),
                Path.Combine(exeDir, "ffprobe.exe"),
                // 便携用法：把 ffmpeg 解到 %LOCALAPPDATA%\VideoCompare\ffmpeg\bin 也能被找到
                Path.Combine(AppSettings.DataDir, "ffmpeg", "bin", "ffprobe.exe"),
                Path.Combine(AppSettings.DataDir, "ffmpeg", "ffprobe.exe"),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim(), "ffprobe.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        static Dictionary<string, string> RunFlat(string file, int timeoutMs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(FfprobePath)) return dict;
            var psi = new ProcessStartInfo(FfprobePath,
                "-v error -print_format flat -show_format -show_streams \"" + file + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(timeoutMs);
                foreach (var line in output.Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim().Trim('"');
                    dict[key] = val;
                }
            }
            return dict;
        }

        /// <summary>读取元数据（同步，调用方放到后台线程）。</summary>
        public static MediaInfo Probe(string path)
        {
            var info = new MediaInfo
            {
                Path = path,
                Name = Path.GetFileName(path),
                Dir = Path.GetDirectoryName(path) ?? "",
                IsNetwork = path.StartsWith(@"\\"),
            };
            try
            {
                var fi = new FileInfo(path);
                info.Size = fi.Exists ? fi.Length : 0;
            }
            catch { }

            bool timeout = info.IsNetwork;
            var d = RunFlat(path, timeout ? 120000 : 30000);
            if (d.Count == 0)
            {
                info.Warnings.Add(!File.Exists(path) ? "文件不存在或无法访问" : "ffprobe 未返回信息");
                return info;
            }


            // 容器
            string fmt = D.Get(d, "format.format_name") ?? "";
            if (fmt.Contains("matroska") || fmt.Contains("webm"))
                info.Container = path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "WebM" : "Matroska (MKV)";
            else if (fmt.Contains("mov") || fmt.Contains("mp4"))
            {
                string brand = D.Get(d, "format.tags.major_brand");
                info.Container = brand != null && brand.Trim() == "qt" ? "QuickTime MOV" : "MP4";
            }
            else if (fmt.Contains("avi")) info.Container = "AVI";
            else if (fmt.Contains("mpegts")) info.Container = "MPEG-TS";
            else if (fmt.Contains("flv")) info.Container = "FLV";
            else info.Container = string.IsNullOrEmpty(fmt) ? "-" : fmt.Split(',')[0].ToUpperInvariant();

            info.FormatLong = D.Get(d, "format.format_long_name");
            info.Duration = D.Num(d, "format.duration");
            long sizeFromProbe = D.Long(d, "format.size");
            if (sizeFromProbe > 0) info.Size = sizeFromProbe;
            info.BitRateTotal = D.Long(d, "format.bit_rate");

            // 遍历 stream
            int i = 0;
            while (true)
            {
                string idx = D.Get(d, "streams.stream." + i + ".index");
                if (idx == null) break;
                string type = D.Get(d, "streams.stream." + i + ".codec_type");
                string prefix = "streams.stream." + i + ".";

                if (type == "video" && info.Width == 0)
                {
                    info.VideoCodecId = D.Get(d, prefix + "codec_name") ?? "";
                    info.VideoCodec = FriendlyName(info.VideoCodecId);
                    string vp = D.Get(d, prefix + "profile"); info.VideoProfile = (vp == null || vp == "unknown") ? "" : vp;
                    string lvl = D.Get(d, prefix + "level");
                    if (lvl != null && lvl != "-99")
                    {
                        double lv = D.Num(d, prefix + "level");
                        if (info.VideoCodecId == "h264") info.VideoLevel = (lv / 10).ToString("0.#", CultureInfo.InvariantCulture);
                        else if (info.VideoCodecId == "hevc") info.VideoLevel = (lv / 30).ToString("0.#", CultureInfo.InvariantCulture);
                        else if (lv > 0) info.VideoLevel = lv.ToString("0", CultureInfo.InvariantCulture);
                    }
                    info.Width = (int)D.Num(d, prefix + "width");
                    info.Height = (int)D.Num(d, prefix + "height");
                    int rot = (int)D.Num(d, prefix + "tags.rotate");
                    if (rot == 0) rot = (int)D.Num(d, prefix + "side_data_list.side_data.0.rotation");
                    info.Rotation = Math.Abs(rot) % 360;
                    if (info.Rotation == 90 || info.Rotation == 270)
                    {
                        int t = info.Width; info.Width = info.Height; info.Height = t;
                    }
                    info.Fps = ParseFraction(D.Get(d, prefix + "r_frame_rate"));
                    if (info.Fps <= 0) info.Fps = ParseFraction(D.Get(d, prefix + "avg_frame_rate"));
                    info.VideoBitrate = D.Long(d, prefix + "bit_rate");
                    info.Frames = D.Long(d, prefix + "nb_frames");
                    info.PixFmt = D.Get(d, prefix + "pix_fmt") ?? "";
                    info.BitDepth = ParseBitDepth(D.Get(d, prefix + "bits_per_raw_sample"), info.PixFmt);
                    info.ColorDesc = ColorText(D.Get(d, prefix + "color_transfer"), D.Get(d, prefix + "color_primaries"));
                }
                else if (type == "audio")
                {
                    info.AudioStreams++;
                    if (!info.HasAudio)
                    {
                        info.HasAudio = true;
                        info.AudioCodec = FriendlyName(D.Get(d, prefix + "codec_name") ?? "", true);
                        string ap = D.Get(d, prefix + "profile"); info.AudioProfile = (ap == null || ap == "unknown") ? "" : ap;
                        info.Channels = (int)D.Num(d, prefix + "channels");
                        string layout = D.Get(d, prefix + "channel_layout");
                        info.ChannelLayout = LayoutCn(layout, info.Channels);
                        info.SampleRate = (int)D.Num(d, prefix + "sample_rate");
                        info.AudioBitrate = D.Long(d, prefix + "bit_rate");
                        string bd = D.Get(d, prefix + "bits_per_raw_sample") ?? D.Get(d, prefix + "bits_per_sample");
                        int bdv;
                        if (bd != null && int.TryParse(bd, out bdv) && bdv > 8) info.AudioBitDepth = bdv;
                        info.AudioLanguage = D.Get(d, prefix + "tags.language") ?? "";
                    }
                }
                else if (type == "subtitle") info.SubtitleStreams++;

                i++;
                if (i > 32) break;
            }

            if (info.Duration <= 0 && info.Fps > 0 && info.Frames > 0) info.Duration = info.Frames / info.Fps;
            if (info.VideoBitrate <= 0 && info.BitRateTotal > 0)
                info.VideoBitrate = Math.Max(0, info.BitRateTotal - info.AudioBitrate);
            if (info.Frames <= 0 && info.Fps > 0 && info.Duration > 0)
                info.Frames = (long)Math.Round(info.Fps * info.Duration);
            if (info.Width == 0) info.Warnings.Add("未识别到视频流");
            return info;
        }

        static double ParseFraction(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var parts = s.Split('/');
            double a, b;
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0)
                return a / b;
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static int ParseBitDepth(string raw, string pixFmt)
        {
            int v;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out v) && v > 0) return v;
            if (!string.IsNullOrEmpty(pixFmt))
            {
                if (pixFmt.Contains("p10") || pixFmt.Contains("10le") || pixFmt.Contains("10be")) return 10;
                if (pixFmt.Contains("p12")) return 12;
                if (pixFmt.Contains("p16")) return 16;
                if (pixFmt.Contains("p9")) return 9;
                return 8;
            }
            return 0;
        }

        static string ColorText(string transfer, string primaries)
        {
            var parts = new List<string>();
            if (transfer == "smpte2084") parts.Add("HDR10 (PQ)");
            else if (transfer == "arib-std-b67") parts.Add("HLG");
            if (primaries != null && primaries.StartsWith("bt2020")) parts.Add("BT.2020 广色域");
            else if (primaries != null && primaries.StartsWith("bt709")) parts.Add("BT.709");
            return parts.Count == 0 ? "" : string.Join(" / ", parts.ToArray());
        }

        static string LayoutCn(string layout, int channels)
        {
            switch (layout)
            {
                case "mono": return "单声道 (1.0)";
                case "stereo": return "立体声 (2.0)";
                case "5.1":
                case "5.1(side)": return "5.1 声道";
                case "7.1": return "7.1 声道";
                case "2.1": return "2.1 声道";
                case "4.0": return "4.0 声道";
                case "5.0": return "5.0 声道";
            }
            if (!string.IsNullOrEmpty(layout)) return layout;
            return channels > 0 ? channels + " 声道" : "";
        }

        static readonly Dictionary<string, string> VideoNames = new Dictionary<string, string>
        {
            {"h264","H.264 / AVC"}, {"hevc","H.265 / HEVC"}, {"h265","H.265 / HEVC"},
            {"vp8","VP8"}, {"vp9","VP9"}, {"av1","AV1"}, {"mpeg4","MPEG-4 Part 2"},
            {"mpeg2video","MPEG-2"}, {"mpeg1video","MPEG-1"}, {"prores","Apple ProRes"},
            {"dnxhd","Avid DNxHD/HR"}, {"mjpeg","Motion JPEG"}, {"rawvideo","未压缩视频"},
            {"theora","Theora"}, {"vc1","VC-1"}, {"wmv3","WMV9 / VC-1"}, {"msmpeg4v3","MS MPEG-4 v3"},
            {"h263","H.263"}, {"flv1","Sorenson H.263"}, {"ffv1","FFV1 无损"}, {"dvvideo","DV"},
        };

        static readonly Dictionary<string, string> AudioNames = new Dictionary<string, string>
        {
            {"aac","AAC"}, {"mp3","MP3"}, {"mp2","MP2"}, {"ac3","AC-3 (Dolby Digital)"},
            {"eac3","E-AC-3 (Dolby Digital Plus)"}, {"dts","DTS"}, {"truehd","Dolby TrueHD"},
            {"flac","FLAC 无损"}, {"opus","Opus"}, {"vorbis","Vorbis"}, {"alac","ALAC 无损"},
            {"pcm_s16le","PCM 16bit"}, {"pcm_s24le","PCM 24bit"}, {"pcm_s32le","PCM 32bit"},
            {"pcm_f32le","PCM 浮点 32bit"}, {"amr_nb","AMR-NB"}, {"wmav2","WMA v2"},
        };

        static string FriendlyName(string id, bool audio = false)
        {
            if (string.IsNullOrEmpty(id)) return "-";
            var map = audio ? AudioNames : VideoNames;
            string v;
            if (map.TryGetValue(id, out v)) return v;
            return id.ToUpperInvariant();
        }
    }
}
