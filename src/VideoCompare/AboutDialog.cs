using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoCompare
{
    /// <summary>
    /// 「关于」对话框：版本、依赖路径、缓存占用与清理、日志目录、许可与解码器说明。
    /// </summary>
    public class AboutDialog : Window
    {
        readonly TextBlock cacheValue;
        readonly TextBlock hintText;

        public AboutDialog()
        {
            Title = "关于 · " + AppInfo.Name;
            Width = 620;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Theme.Bg;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            Styles.Apply(this);
            SourceInitialized += delegate { MainWindow.DarkTitleBar(this); };

            var root = new Border
            {
                Background = Theme.Bg,
                Padding = new Thickness(22, 18, 22, 16),
            };
            var stack = new StackPanel();
            root.Child = stack;

            // ---- 标题 ----
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = "▶◀",
                FontSize = 15,
                Foreground = Theme.Accent,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(Theme.Label(AppInfo.Name, 18, Theme.Text, FontWeights.SemiBold));
            head.Children.Add(new TextBlock
            {
                Text = "v" + AppInfo.Version,
                FontSize = 12,
                Foreground = Theme.Accent,
                Background = Theme.Panel2,
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = "左右双栏视频对比播放器 · 原生 Windows GUI · MIT 许可",
                FontSize = 12,
                Foreground = Theme.Muted,
                Margin = new Thickness(0, 6, 0, 2),
            });

            stack.Children.Add(Divider());

            // ---- 路径与依赖 ----
            AddRow(stack, "程序目录", AppDomain.CurrentDomain.BaseDirectory);
            AddRow(stack, "数据目录", AppSettings.DataDir + "（配置 / 日志 / 缓存）");
            cacheValue = AddRow(stack, "转码缓存", "");
            AddRow(stack, "ffmpeg", FfmpegHelper.FfmpegPath ?? "未找到 —— 转封装/转码片段、部分编码的兜底播放不可用");
            AddRow(stack, "ffprobe", MediaProbe.FfprobePath ?? "未找到 —— 元数据（编码/声道/分辨率/帧率）不可用");

            stack.Children.Add(Divider());

            // ---- 说明 ----
            var notice = new Border
            {
                Background = Theme.Panel,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
            };
            var noticeStack = new StackPanel();
            notice.Child = noticeStack;
            noticeStack.Children.Add(Theme.Label("关于播放能力", 12, Theme.Text, FontWeights.SemiBold));
            hintText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = Theme.Muted,
                Margin = new Thickness(0, 6, 0, 0),
                LineHeight = 18,
                Text = "画面解码走 Windows Media Foundation：H.264 一般开箱可用；" +
                       "HEVC（H.265）、VP9、AV1 需要系统里装有对应的解码器" +
                       "（例如微软商店的「HEVC 视频扩展」），否则可能只有声音或打不开。" +
                       "MKV 等容器同样取决于系统是否装了对应的分离器。\n" +
                       "把 ffmpeg 放进程序目录的 ffmpeg\\bin\\ 或加入 PATH，程序就能对不支持的编码" +
                       "做「转封装」或「转码片段」，把打不开的片子也放起来。",
            };
            noticeStack.Children.Add(hintText);
            stack.Children.Add(notice);

            // ---- 按钮 ----
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var clearBtn = Theme.Btn("清空缓存");
            clearBtn.Click += delegate
            {
                int n = FfmpegHelper.ClearCache();
                RefreshCache();
                MessageBox.Show(n > 0 ? ("已删除 " + n + " 个缓存文件。") : "缓存已经是空的（正在播放的片段会跳过）。",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            };
            var openData = Theme.Btn("打开数据目录");
            openData.Click += delegate { OpenDir(AppSettings.DataDir); };
            var openLogs = Theme.Btn("打开日志目录");
            openLogs.Click += delegate
            {
                try { Directory.CreateDirectory(AppSettings.LogDir); } catch { }
                OpenDir(AppSettings.LogDir);
            };
            var close = Theme.Btn("关闭", true);
            close.Click += delegate { Close(); };
            buttons.Children.Add(clearBtn);
            buttons.Children.Add(openData);
            buttons.Children.Add(openLogs);
            buttons.Children.Add(close);
            stack.Children.Add(buttons);

            Content = root;
            RefreshCache();
        }

        void RefreshCache()
        {
            try
            {
                long bytes = FfmpegHelper.CacheBytes();
                cacheValue.Text = FormatSize(bytes) + "（上限 " + AppSettings.CacheLimitMB + " MB，超过自动清理最旧的）";
            }
            catch { cacheValue.Text = "(读取失败)"; }
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        static Border Divider()
        {
            return new Border
            {
                Height = 1,
                Background = Theme.Line,
                Margin = new Thickness(0, 12, 0, 12),
            };
        }

        static TextBlock AddRow(Panel host, string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lb = Theme.Label(label, 11.5, Theme.Muted2);
            lb.VerticalAlignment = VerticalAlignment.Top;
            lb.Margin = new Thickness(0, 2, 8, 0);
            Grid.SetColumn(lb, 0);
            g.Children.Add(lb);

            var val = new TextBlock
            {
                Text = value,
                FontSize = 11.5,
                Foreground = Theme.Text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
            };
            Grid.SetColumn(val, 1);
            g.Children.Add(val);

            host.Children.Add(g);
            return val;
        }

        static void OpenDir(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show("目录不存在：" + dir, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var psi = new ProcessStartInfo(dir);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message + "\n\n" + dir, AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
