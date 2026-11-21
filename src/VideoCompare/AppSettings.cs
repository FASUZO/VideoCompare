using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace VideoCompare
{
    /// <summary>
    /// 极简配置（key=value 文本）：默认 %LOCALAPPDATA%\VideoCompare\settings.ini；
    /// 该目录不可写时依次退回 %APPDATA%、程序目录，任何失败都不影响启动。
    /// </summary>
    public static class AppSettings
    {
        public static readonly string DataDir = ResolveDataDir();
        public static readonly string FilePath = Path.Combine(DataDir, "settings.ini");
        public static readonly string LogDir = Path.Combine(DataDir, "logs");

        // ---- 可持久化的值 ----
        public static int WindowWidth = 1680;
        public static int WindowHeight = 1020;
        public static int WindowLeft = int.MinValue;
        public static int WindowTop = int.MinValue;
        public static bool WindowMaximized;
        public static double Volume = 100.0;     // 0..100
        public static bool Muted;
        public static bool Linked = true;
        public static string LastDir = "";
        public static int CacheLimitMB = 2048;
        public static bool WelcomeShown;

        static string ResolveDataDir()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] bases = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("APPDATA"),
                exeDir,
            };
            foreach (var b in bases)
            {
                if (string.IsNullOrEmpty(b)) continue;
                try
                {
                    var dir = Path.Combine(b, "VideoCompare");
                    Directory.CreateDirectory(dir);
                    var probe = Path.Combine(dir, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }
            return exeDir;
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "window.width": WindowWidth = Int(v, WindowWidth); break;
                        case "window.height": WindowHeight = Int(v, WindowHeight); break;
                        case "window.left": WindowLeft = Int(v, WindowLeft); break;
                        case "window.top": WindowTop = Int(v, WindowTop); break;
                        case "window.maximized": WindowMaximized = Bool(v, WindowMaximized); break;
                        case "player.volume": Volume = Dbl(v, Volume); break;
                        case "player.muted": Muted = Bool(v, Muted); break;
                        case "ui.linked": Linked = Bool(v, Linked); break;
                        case "ui.lastdir": LastDir = v; break;
                        case "cache.limitmb": CacheLimitMB = Int(v, CacheLimitMB); break;
                        case "ui.welcomeshown": WelcomeShown = Bool(v, WelcomeShown); break;
                    }
                }
            }
            catch { }
            if (Volume < 0) Volume = 0;
            if (Volume > 100) Volume = 100;
            if (CacheLimitMB < 128) CacheLimitMB = 128;
        }

        public static void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 视频对比工具 配置文件（可手工编辑；删掉本文件即恢复默认）");
            sb.AppendLine("window.width=" + WindowWidth.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("window.height=" + WindowHeight.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("window.left=" + WindowLeft.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("window.top=" + WindowTop.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("window.maximized=" + (WindowMaximized ? "1" : "0"));
            sb.AppendLine("player.volume=" + Volume.ToString("0.#", CultureInfo.InvariantCulture));
            sb.AppendLine("player.muted=" + (Muted ? "1" : "0"));
            sb.AppendLine("ui.linked=" + (Linked ? "1" : "0"));
            sb.AppendLine("ui.lastdir=" + (LastDir ?? ""));
            sb.AppendLine("cache.limitmb=" + CacheLimitMB.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ui.welcomeshown=" + (WelcomeShown ? "1" : "0"));
            try { File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8); }
            catch { }
        }

        /// <summary>窗口尺寸/位置：只在屏幕可见范围内恢复，避免换显示器后跑到看不见的地方。</summary>
        public static void ApplyWindow(Window w)
        {
            if (w == null) return;
            try
            {
                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vRight = vLeft + SystemParameters.VirtualScreenWidth;
                double vBottom = vTop + SystemParameters.VirtualScreenHeight;

                bool havePos = WindowLeft != int.MinValue && WindowTop != int.MinValue;
                if (havePos)
                {
                    int margin = 80;
                    bool visible = WindowLeft + margin < vRight && WindowLeft + WindowWidth - margin > vLeft
                                && WindowTop + margin < vBottom && WindowTop + WindowHeight - margin > vTop;
                    if (visible)
                    {
                        w.WindowStartupLocation = WindowStartupLocation.Manual;
                        w.Left = WindowLeft;
                        w.Top = WindowTop;
                    }
                }
                if (WindowWidth >= 640) w.Width = WindowWidth;
                if (WindowHeight >= 480) w.Height = WindowHeight;
                if (WindowMaximized) w.WindowState = WindowState.Maximized;
            }
            catch { }
        }

        public static void CaptureWindow(Window w)
        {
            if (w == null) return;
            try
            {
                WindowMaximized = w.WindowState == WindowState.Maximized;
                if (w.WindowState == WindowState.Normal)
                {
                    WindowWidth = (int)Math.Round(w.Width);
                    WindowHeight = (int)Math.Round(w.Height);
                    WindowLeft = (int)Math.Round(w.Left);
                    WindowTop = (int)Math.Round(w.Top);
                }
            }
            catch { }
        }

        static int Int(string s, int fallback)
        {
            int n;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }

        static double Dbl(string s, double fallback)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        static bool Bool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return fallback;
        }
    }
}
