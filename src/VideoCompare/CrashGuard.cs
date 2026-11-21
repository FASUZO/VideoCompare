using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VideoCompare
{
    /// <summary>
    /// 未捕获异常兜底：写日志到 %LOCALAPPDATA%\VideoCompare\logs\crash-*.log 并弹窗告知路径，
    /// 避免窗口化程序崩溃时「窗口无声消失、用户无从反馈」。
    /// </summary>
    public static class CrashGuard
    {
        static bool _installed;

        public static void Install(Application app)
        {
            if (_installed) return;
            _installed = true;

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Write("AppDomain", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e)
            {
                Write("Task", e.Exception);
                e.SetObserved();
            };

            if (app != null)
            {
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    string path = Write("Dispatcher", e.Exception);
                    e.Handled = true;
                    ShowMessage(e.Exception, path);
                };
            }
        }

        /// <summary>把异常写进日志文件，返回日志路径（失败返回空串）。</summary>
        public static string Write(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.LogDir);
                string name = "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                string path = Path.Combine(AppSettings.LogDir, name);
                var sb = new StringBuilder();
                sb.AppendLine("时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("来源   : " + source);
                sb.AppendLine("版本   : " + AppInfo.Version + " (" + AppInfo.VersionFull + ")");
                sb.AppendLine("系统   : " + Environment.OSVersion.VersionString + " / CLR " + Environment.Version);
                sb.AppendLine("64 位  : " + (Environment.Is64BitProcess ? "是" : "否"));
                sb.AppendLine("ffmpeg : " + (FfmpegHelper.FfmpegPath ?? "(未找到)"));
                sb.AppendLine("ffprobe: " + (MediaProbe.FfprobePath ?? "(未找到)"));
                sb.AppendLine("目录   : " + AppDomain.CurrentDomain.BaseDirectory);
                sb.AppendLine();
                sb.AppendLine(ex == null ? "(无异常对象)" : ex.ToString());
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch { return ""; }
        }

        public static void ShowMessage(Exception ex, string logPath)
        {
            try
            {
                string tail = string.IsNullOrEmpty(logPath)
                    ? "（日志写入失败）"
                    : ("日志已保存到：\n" + logPath);
                MessageBox.Show(
                    "程序遇到一个内部错误，已尽量保持运行。\n\n" +
                    (ex == null ? "" : ex.GetType().Name + "：" + ex.Message + "\n\n") +
                    tail + "\n\n如果在 GitHub 反馈问题，请附上这个日志文件。",
                    AppInfo.Name + " · 出错了",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }
    }
}
