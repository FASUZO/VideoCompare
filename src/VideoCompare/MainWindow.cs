using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoCompare
{
    /// <summary>主窗口：左右两栏 + 顶部同步控制 + 差异高亮。</summary>
    public class MainWindow : Window
    {
        // 注意：Left 与 Window.Left 同名，这里是有意遮蔽（用 new 消警）
        public new PaneView Left;
        public PaneView Right;
        public bool Linked = true;
        public bool Mirroring;

        readonly TextBlock topStatus;
        readonly CheckBox linkBox;

        readonly bool diagMode;

        public MainWindow(string[] args) : this(args, false)
        {
        }

        public MainWindow(string[] args, bool diagMode)
        {
            this.diagMode = diagMode;
            Title = "视频对比工具";
            Width = 1680; Height = 1020;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Bg;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            Styles.Apply(this);

            var root = new DockPanel();

            // ---------- 顶栏 ----------
            var top = new Border
            {
                Background = Theme.Panel,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 9, 14, 9),
            };
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock
            {
                Text = "▶◀", FontSize = 13, Foreground = Theme.Accent, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            brand.Children.Add(Theme.Label("视频对比", 15, Theme.Text, FontWeights.SemiBold));
            Grid.SetColumn(brand, 0);
            topGrid.Children.Add(brand);

            topStatus = Theme.Label("", 12, Theme.Muted2);
            topStatus.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(topStatus, 1);
            topGrid.Children.Add(topStatus);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var syncPlay = Theme.Btn("▶▶ 同步播放", true);
            syncPlay.Click += (s, e) => { SyncPlay(); SetTopStatus("两侧同时播放"); };
            var syncPause = Theme.Btn("⏸ 同步暂停");
            syncPause.Click += (s, e) => { SyncPause(); SetTopStatus("两侧同时暂停"); };
            var alignBtn = Theme.Btn("⇥ 对齐到左");
            alignBtn.Click += (s, e) =>
            {
                if (!Left.HasMedia || !Right.HasMedia) { SetTopStatus("两侧都载入后才能对齐"); return; }
                double t = Left.GlobalPosition();
                Right.SeekTo(t);
                SetTopStatus("右侧已对齐到 " + PaneView.FormatClock(t));
            };
            var zeroBtn = Theme.Btn("⏮ 归零");
            zeroBtn.Click += (s, e) => { Left.SeekTo(0); Right.SeekTo(0); };
            var swapBtn = Theme.Btn("⇄ 交换");
            swapBtn.Click += async (s, e) => await Swap();

            linkBox = new CheckBox
            {
                Content = "联动",
                IsChecked = AppSettings.Linked,
                Foreground = Theme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                ToolTip = "开启后，一侧播放/暂停/拖动进度会同步到另一侧",
            };
            Linked = AppSettings.Linked;
            linkBox.Checked += (s, e) => Linked = true;
            linkBox.Unchecked += (s, e) => Linked = false;

            actions.Children.Add(syncPlay);
            actions.Children.Add(syncPause);
            actions.Children.Add(alignBtn);
            actions.Children.Add(zeroBtn);
            actions.Children.Add(swapBtn);
            actions.Children.Add(linkBox);

            var aboutBtn = Theme.Btn("关于");
            aboutBtn.Margin = new Thickness(0, 0, 0, 0);
            aboutBtn.ToolTip = "版本、依赖路径、缓存占用与清理、日志目录";
            aboutBtn.Click += delegate { (new AboutDialog { Owner = this }).ShowDialog(); };
            actions.Children.Add(aboutBtn);

            Grid.SetColumn(actions, 2);
            topGrid.Children.Add(actions);

            top.Child = topGrid;
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // ---------- 主体 ----------
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Left = new PaneView(this, "left", "左");
            Right = new PaneView(this, "right", "右");

            Grid.SetColumn(Left.Root, 0);
            grid.Children.Add(Left.Root);

            var divider = new Border { Background = Theme.Line };
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            Grid.SetColumn(Right.Root, 2);
            grid.Children.Add(Right.Root);

            root.Children.Add(grid);
            Content = root;

            // 恢复上次的窗口位置、音量、静音
            AppSettings.ApplyWindow(this);
            Left.VolumePercent = AppSettings.Volume;
            Right.VolumePercent = AppSettings.Volume;
            Left.SetMuted(AppSettings.Muted);
            Right.SetMuted(AppSettings.Muted);

            // 快捷键
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            Closing += delegate
            {
                try
                {
                    AppSettings.CaptureWindow(this);
                    if (Left != null) AppSettings.Volume = Left.VolumePercent;
                    AppSettings.Muted = Left != null && Left.IsMuted;
                    AppSettings.Linked = Linked;
                    AppSettings.Save();
                }
                catch { }
            };

            Loaded += (s, e) =>
            {
                if (!string.IsNullOrEmpty(MediaProbe.FfprobePath))
                    SetTopStatus("就绪 · ffprobe: " + Path.GetFileName(Path.GetDirectoryName(MediaProbe.FfprobePath)));
                else
                    SetTopStatus("未找到 ffprobe，元数据不可用（把 ffmpeg 放在程序目录即可）");
                // 启动时把窗口提到最前（否则容易被别的窗口挡住看不到）
                try
                {
                    Topmost = true;
                    Activate();
                    Focus();
                    Topmost = false;
                }
                catch { }
                if (!diagMode && !AppSettings.WelcomeShown)
                {
                    AppSettings.WelcomeShown = true;
                    AppSettings.Save();
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this,
                            "首次使用提示（只出现这一次）：\n\n" +
                            "· 画面解码走 Windows Media Foundation：H.264 通常开箱可用；HEVC(H.265)/VP9/AV1 需要系统装有对应解码器" +
                            "（如微软商店的「HEVC 视频扩展」），MKV 等容器也取决于系统分离器。\n" +
                            "· 把 ffmpeg 放到程序目录的 ffmpeg\\bin\\ 或加入 PATH，程序就能对不支持的编码做转封装 / 转码片段。" +
                            (FfmpegHelper.FfmpegPath == null ? "\n  （当前没有找到 ffmpeg）" : "") + "\n" +
                            "· 配置、日志、转码缓存都在：" + AppSettings.DataDir + "\n" +
                            "· 顶栏「关于」可查看版本、清空缓存、打开日志目录。",
                            AppInfo.Name + " · 首次使用",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                Async.Fire(() => StartupAsync(args ?? new string[0]));
            };
        }

        async Task StartupAsync(string[] args)
        {
            // --pickshow [path] [--folder]：打开自写文件选择器并留在屏幕上（截图/人工核对用）
            if (args.Length > 0 && string.Equals(args[0], "--pickshow", StringComparison.OrdinalIgnoreCase))
            {
                bool folder = Array.Exists(args, x => string.Equals(x, "--folder", StringComparison.OrdinalIgnoreCase));
                string start = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                var pw = new FilePickerWindow(
                    folder ? FilePickerWindow.Mode.Folder : FilePickerWindow.Mode.File,
                    start, folder ? "选择要移动到的文件夹" : "选择视频");
                pw.Owner = this;
                pw.Show();
                FilePickerWindow.LastShown = pw;   // 防止非模态窗口被 GC 回收
                return;
            }
            // --pickcheck <path1> [path2] …：不弹窗、只跑逻辑，验证自写文件选择器的路径解析
            if (args.Length > 1 && string.Equals(args[0], "--pickcheck", StringComparison.OrdinalIgnoreCase))
            {
                string[] paths = new string[args.Length - 1];
                Array.Copy(args, 1, paths, 0, paths.Length);
                RunPickCheck(paths);
                return;
            }
            // --selftest <file1> [file2]：跑一遍并写结果，便于自动化验证
            if (args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                await RunSelfTest(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "--uicheck", StringComparison.OrdinalIgnoreCase))
            {
                double ufrac = 0.4;
                if (args.Length > 3) double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ufrac);
                await RunUiCheck(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, ufrac);
                return;
            }
            // --seeklog：把进度条相关事件写进 seeklog.txt（配合真实点击测试）
            int li = Array.FindIndex(args, x => string.Equals(x, "--seeklog", StringComparison.OrdinalIgnoreCase));
            if (li >= 0)
            {
                PaneView.SeekLogOn = true;
                try { File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "seeklog.txt")); } catch { }
            }
            // --barrect <file1> [file2]：把两个进度条的屏幕矩形写进 barrect.txt 后退出（给点击注入脚本用）
            if (args.Length > 0 && string.Equals(args[0], "--barrect", StringComparison.OrdinalIgnoreCase))
            {
                bool keep = Array.Exists(args, x => string.Equals(x, "--barkeep", StringComparison.OrdinalIgnoreCase));
                await RunBarRect(args.Length > 1 ? args[1] : null, args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null, keep);
                return;
            }
            // --clicktest <file> <fracs,csv> [waitMs]：模拟真实点击（按下→写值→松开）连点，检查刻度回弹
            if (args.Length > 1 && string.Equals(args[0], "--clicktest", StringComparison.OrdinalIgnoreCase))
            {
                int w = 700;
                if (args.Length > 3) int.TryParse(args[3], out w);
                await RunClickTest(args[1], args.Length > 2 ? args[2] : "0.8,0.2,0.6,0.1,0.9,0.45", w);
                return;
            }
            // --seektest <file> <fracs,csv> [waitMs]：连做多次定位，检查进度条有没有「弹回原刻度」
            if (args.Length > 1 && string.Equals(args[0], "--seektest", StringComparison.OrdinalIgnoreCase))
            {
                int wait = 1800;
                if (args.Length > 3) int.TryParse(args[3], out wait);
                await RunSeekTest(args[1], args.Length > 2 ? args[2] : "0.2,0.85,0.05,0.6", wait);
                return;
            }
            // --seek <frac>：调好后停在该比例处，便于人工/截图核对缓存条与定位钉住行为
            int si = Array.FindIndex(args, x => string.Equals(x, "--seek", StringComparison.OrdinalIgnoreCase));
            string[] files = si >= 0 ? Array.FindAll(args, x => !x.StartsWith("--")) : args;
            bool seekGiven = false;
            double seekFrac = 0;
            if (si >= 0 && si + 1 < args.Length)
                seekGiven = double.TryParse(args[si + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seekFrac);
            if (si >= 0) files = Array.FindAll(files, x => x != args[si + 1]);

            if (files.Length >= 2) await LoadBoth(files[0], files[1]);
            else if (files.Length == 1) await Left.Load(files[0]);
            if (seekGiven) Left.DebugSeekFraction(seekFrac);
        }

        /// <summary>
        /// 无界面自检：验证「选择视频」自写选择器把各种路径写法都当成路径解析。
        /// 重点是：直接输入一个文件路径 → 立即返回该路径（而不是交给系统播放器打开）。
        /// </summary>
        void RunPickCheck(string[] paths)
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            Action<string> ok = (msg) => { pass++; sb.AppendLine("PASS " + msg); };
            Action<string> bad = (msg) => { fail++; sb.AppendLine("FAIL " + msg); };

            try
            {
                sb.AppendLine("data_dir=" + AppSettings.DataDir);
                sb.AppendLine("last_dir=" + (AppSettings.LastDir ?? "<null>"));
                sb.AppendLine("picker=FilePickerWindow (no system dialog)");

                for (int i = 0; i < paths.Length; i++)
                {
                    string raw = paths[i];
                    string shown = raw.Length > 130 ? raw.Substring(0, 62) + " … " + raw.Substring(raw.Length - 56) : raw;
                    sb.AppendLine("---- [" + (i + 1) + "] " + shown);

                    string norm = PathUtil.Normalize(raw);
                    bool isFile = PathUtil.IsFile(norm);
                    bool isDir = !isFile && PathUtil.IsDir(norm);
                    sb.AppendLine("  normalized=" + norm);
                    sb.AppendLine("  is_file=" + isFile + " is_dir=" + isDir +
                                  " server_root=" + PathUtil.IsServerRoot(norm) +
                                  " ext_ok=" + PathUtil.IsVideoName(norm));

                    if (!isFile && !isDir)
                    {
                        bad("路径不存在或无法访问：" + norm);
                        continue;
                    }

                    string dir = isFile ? PathUtil.ParentOf(norm) : norm;
                    string name = Path.GetFileName(norm);

                    // 1) 列目录：能打开，且文件模式下文件出现在列表里
                    var w1 = new FilePickerWindow(FilePickerWindow.Mode.File, null, "pickcheck");
                    bool nav1 = w1.NavigateTo(dir);
                    sb.AppendLine("  browse dir=" + dir);
                    sb.AppendLine("  browse items=" + w1.ItemCount + " status=" + w1.StatusText);
                    if (nav1) ok("能列出目录");
                    else bad("列不出目录：" + dir);
                    if (isFile)
                    {
                        if (w1.HasName(name)) ok("父目录里能看到该文件");
                        else bad("父目录里看不到该文件：" + name);
                        if (PathUtil.IsVideoName(norm)) ok("扩展名被识别为视频");
                        else sb.AppendLine("NOTE 扩展名不在视频列表里（勾选「显示所有文件类型」仍可选）：" + name);
                    }

                    // 2) 直接输入文件路径 → 立即接受（关键修复点）
                    var w2 = new FilePickerWindow(FilePickerWindow.Mode.File, null, "pickcheck");
                    bool nav2 = w2.NavigateTo(raw);
                    sb.AppendLine("  direct accepted=" + w2.Accepted + " result=" + (w2.Result ?? "<null>"));
                    if (isFile)
                    {
                        if (nav2 && w2.Accepted && string.Equals(w2.Result, norm, StringComparison.OrdinalIgnoreCase))
                            ok("直接输入文件路径 → 直接返回该文件");
                        else
                            bad("直接输入文件路径没有直接返回");
                    }
                    else
                    {
                        if (nav2 && !w2.Accepted && string.Equals(w2.CurrentDir, norm, StringComparison.OrdinalIgnoreCase))
                            ok("直接输入目录 → 进入目录且不确认");
                        else
                            bad("直接输入目录行为异常");
                    }

                    // 3) 文件夹模式（「移动」按钮用）：输入文件不能被当成文件夹确认
                    var w3 = new FilePickerWindow(FilePickerWindow.Mode.Folder, null, "pickcheck");
                    bool nav3 = w3.NavigateTo(raw);
                    if (isFile)
                    {
                        if (!w3.Accepted) ok("文件夹模式拒绝把文件当文件夹");
                        else bad("文件夹模式错误地把文件当文件夹确认了");
                    }
                    else
                    {
                        if (nav3 && !w3.Accepted) ok("文件夹模式进入目录（需再点确认）");
                        else bad("文件夹模式进入目录行为异常");
                    }
                }

                // 4) 不存在的路径不应抛异常
                var w4 = new FilePickerWindow(FilePickerWindow.Mode.File, null, "pickcheck");
                bool nav4 = w4.NavigateTo("C:\\__video_compare_no_such_dir__\\x.mp4");
                if (!nav4 && !w4.Accepted) ok("不存在的路径被拒绝且没有抛异常");
                else bad("不存在的路径处理异常");
                sb.AppendLine("  missing status=" + w4.StatusText);
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT_EXCEPTION " + ex);
            }
            finally
            {
                sb.AppendLine("SUMMARY pass=" + pass + " fail=" + fail);
                sb.AppendLine(fail == 0 ? "RESULT_OK" : "RESULT_FAIL " + fail);
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pickcheck.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        async Task RunSelfTest(string a, string b)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                // 自检要分别观察两栏，先解除联动，否则 seek/play 会互相镜像，测出来的是耦合行为
                Linked = false;
                sb.AppendLine("ffprobe=" + (MediaProbe.FfprobePath ?? "(未找到)"));
                sb.AppendLine("ffmpeg=" + (FfmpegHelper.FfmpegPath ?? "(未找到)"));
                if (a != null) await Left.Load(a);
                if (b != null) await Right.Load(b);

                // 等两侧拿到时长或失败
                for (int i = 0; i < 120; i++)
                {
                    bool lOk = !Left.HasMedia || Left.Player.NaturalDuration.HasTimeSpan;
                    bool rOk = !Right.HasMedia || Right.Player.NaturalDuration.HasTimeSpan;
                    if (lOk && rOk) break;
                    await Task.Delay(250);
                }
                Describe(sb, "left", Left);
                Describe(sb, "right", Right);

                // 给"音轨能开但没画面"的编码留出兜底时间（转封装/转码片段）
                await Task.Delay(6000);
                sb.AppendLine("after_fallback left: clip=" + Left.IsFallbackClip
                    + " natural=" + Left.Player.NaturalVideoWidth + "x" + Left.Player.NaturalVideoHeight
                    + " pos=" + Left.GlobalPosition().ToString("F1"));
                sb.AppendLine("after_fallback right: clip=" + Right.IsFallbackClip
                    + " natural=" + Right.Player.NaturalVideoWidth + "x" + Right.Player.NaturalVideoHeight
                    + " pos=" + Right.GlobalPosition().ToString("F1"));

                // 播 2.5 秒看进度是否推进。先从 10% 处起播：文件若已停在末尾，位置不会推进，
                // 那样这个检查就永远抓不到「播放卡住」的回归。
                if (Left.HasMedia) Left.SeekTo(Left.Duration() * 0.1);
                if (Right.HasMedia) Right.SeekTo(Right.Duration() * 0.1);
                // 等两侧定位真正落定（进度条钉住期间 playback 还没稳定，直接测会偶发假失败）
                for (int i = 0; i < 40 && (Left.IsSeekPending || Right.IsSeekPending); i++) await Task.Delay(100);
                await Task.Delay(400);
                double l0 = Left.GlobalPosition(), r0 = Right.GlobalPosition();
                if (Left.HasMedia) Left.Play();
                if (Right.HasMedia) Right.Play();
                await Task.Delay(2500);
                double l1 = Left.GlobalPosition(), r1 = Right.GlobalPosition();
                sb.AppendLine("state_left " + Left.SeekState() + " playing=" + Left.IsPlaying()
                    + " canPause=" + Left.Player.CanPause);
                sb.AppendLine("state_right " + Right.SeekState() + " playing=" + Right.IsPlaying()
                    + " canPause=" + Right.Player.CanPause);
                sb.AppendLine(string.Format("play_left {0:F2} -> {1:F2}", l0, l1));
                sb.AppendLine(string.Format("play_right {0:F2} -> {1:F2}", r0, r1));

                // 明确的断言，CI 只看 RESULT_* 就能判定
                int fail = 0;
                if (a != null)
                {
                    if (Left.Duration() <= 0.5) { fail++; sb.AppendLine("FAIL left duration"); }
                    if (Left.Player.NaturalVideoWidth <= 0) { fail++; sb.AppendLine("FAIL left no picture"); }
                    if (l1 - l0 < 0.4) { fail++; sb.AppendLine("FAIL left playback stalled"); }
                }
                if (b != null)
                {
                    if (Right.Duration() <= 0.5) { fail++; sb.AppendLine("FAIL right duration"); }
                    if (Right.Player.NaturalVideoWidth <= 0) { fail++; sb.AppendLine("FAIL right no picture"); }
                    if (r1 - r0 < 0.4) { fail++; sb.AppendLine("FAIL right playback stalled"); }
                }

                // 拖动测试
                if (Left.HasMedia)
                {
                    Left.SeekTo(Left.Duration() * 0.5);
                    await Task.Delay(1500);
                    sb.AppendLine(string.Format("seek_left to {0:F1} now {1:F1}", Left.Duration() * 0.5, Left.GlobalPosition()));
                }
                if (Left.HasMedia)
                {
                    Left.SetMuted(true);
                    await Task.Delay(150);
                    sb.AppendLine("mute_on   " + Left.MuteState());
                    Left.SetMuted(false);
                    await Task.Delay(150);
                    sb.AppendLine("mute_off  " + Left.MuteState());
                    sb.AppendLine(string.Format(
                        "info_layout left_scrollableH={0:F1} right_scrollableH={1:F1}",
                        Left.InfoScrollableHeight, Right.InfoScrollableHeight));
                    sb.AppendLine("cache_left  " + Left.CacheDesc());
                    sb.AppendLine("cache_right " + Right.CacheDesc());
                }
                sb.AppendLine(fail == 0 ? "RESULT_OK" : ("RESULT_FAIL " + fail));
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT_EXCEPTION " + ex);
            }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        /// <summary>界面自检：验证进度条/音量条/滚动条的自绘模板部件齐全，并走一遍拖动提交路径。</summary>
        async Task RunUiCheck(string a, string b, double frac)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (a != null) await Left.Load(a);
                if (b != null) await Right.Load(b);
                for (int i = 0; i < 120; i++)
                {
                    bool lOk = !Left.HasMedia || Left.Player.NaturalDuration.HasTimeSpan;
                    bool rOk = !Right.HasMedia || Right.Player.NaturalDuration.HasTimeSpan;
                    if (lOk && rOk) break;
                    await Task.Delay(250);
                }
                await Task.Delay(1200);   // 等模板应用 + 布局完成

                sb.AppendLine("left_controls  " + Left.DescribeControls());
                sb.AppendLine("right_controls " + Right.DescribeControls());

                if (Left.HasMedia)
                {
                    double dur = Left.Duration();
                    double durLeft = Left.Duration();
                    Left.DebugSeekFraction(frac);          // 与鼠标拖动收尾同一路径
                    for (int k = 0; k < 5; k++)
                    {
                        await Task.Delay(600);
                        sb.AppendLine(string.Format("sample{0} pos={1:F1} gpos={2:F1} bar={3:F0} text={4} target={5:F1}",
                            k, Left.CurrentPosition(), Left.GlobalPosition(), Left.SeekFraction() * 1000,
                            Left.TimeText(), frac * durLeft));

                    }
                }
                if (Left.HasMedia)
                {
                    Left.SetMuted(true);
                    await Task.Delay(150);
                    sb.AppendLine("mute_on   " + Left.MuteState());
                    Left.SetMuted(false);
                    await Task.Delay(150);
                    sb.AppendLine("mute_off  " + Left.MuteState());
                    sb.AppendLine(string.Format(
                        "info_layout left_scrollableH={0:F1} right_scrollableH={1:F1}",
                        Left.InfoScrollableHeight, Right.InfoScrollableHeight));
                    sb.AppendLine("cache_left  " + Left.CacheDesc());
                    sb.AppendLine("cache_right " + Right.CacheDesc());
                }
                sb.AppendLine("RESULT_OK");
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT_EXCEPTION " + ex);
            }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uicheck.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        /// <summary>把进度条屏幕矩形写出来，供点击注入脚本使用。</summary>
        async Task RunBarRect(string a, string b, bool keepRunning)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (a != null && b != null) await LoadBoth(a, b);
                else if (a != null) await Left.Load(a);
                for (int i = 0; i < 120; i++)
                {
                    if (Left.HasMedia && Left.Player.NaturalDuration.HasTimeSpan) break;
                    await Task.Delay(250);
                }
                await Task.Delay(1500);
                var origin = PointToScreen(new Point(0, 0));
                sb.AppendLine("client_origin x=" + (int)origin.X + " y=" + (int)origin.Y);
                sb.AppendLine("window w=" + (int)ActualWidth + " h=" + (int)ActualHeight);
                sb.AppendLine(Left.BarRect());
                sb.AppendLine(Right.BarRect());
                sb.Append(Left.HitTestReport());
                sb.AppendLine("RESULT_OK");
            }
            catch (Exception ex) { sb.AppendLine("RESULT_EXCEPTION " + ex); }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "barrect.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                if (!keepRunning) Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// 定位回归测试：连续跳到多个位置，每次跳完连续采样进度条刻度，
        /// 只要拇指中途离开过目标（例如被播放位置拽回原刻度）就判 FAIL。
        /// </summary>
        /// <summary>
        /// 模拟真实点击的回归测试：每次点击都走「PreviewMouseDown → 轨道写值 → PreviewMouseUp → CommitSeek」，
        /// 和鼠标点击完全同一条路径。判据是刻度只许朝点击方向走，绝不许往回跳。
        /// </summary>
        async Task RunClickTest(string file, string csv, int waitMs)
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            try
            {
                await Left.Load(file);
                for (int i = 0; i < 120; i++)
                {
                    if (Left.HasMedia && Left.Player.NaturalDuration.HasTimeSpan) break;
                    await Task.Delay(250);
                }
                await Task.Delay(800);
                sb.AppendLine("file=" + file + " waitMs=" + waitMs);
                sb.AppendLine(Left.HitTestReport().Replace("\r\n", " | "));

                double prevF = -1;
                foreach (string raw in csv.Split(','))
                {
                    double f;
                    if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    Left.DebugClickFraction(f);
                    double worst = 0, first = -1, last = -1, minB = 9, maxB = -9;
                    int samples = 0;
                    for (int t = 0; t < waitMs; t += 100)
                    {
                        await Task.Delay(100);
                        double b = Left.SeekFraction();
                        if (first < 0) first = b;
                        last = b;
                        if (b < minB) minB = b;
                        if (b > maxB) maxB = b;
                        double bad = 0;
                        if (prevF < 0) bad = t < 400 ? Math.Abs(b - f) : Math.Max(0, (f - 0.02) - b);
                        else if (f > prevF + 0.02) bad = Math.Max(0, (f - 0.02) - b);
                        else if (f < prevF - 0.02) bad = Math.Max(0, b - (f + 0.02));
                        else bad = Math.Max(0, Math.Abs(b - f) - 0.02);
                        if (bad > worst) worst = bad;
                        samples++;
                    }
                    bool ok = worst <= 0.005 && Math.Abs(first - f) <= 0.03;
                    if (ok) pass++; else fail++;
                    sb.AppendLine(string.Format(
                        "click f={0:F3} prev={1:F3} {2} snapback={3:P1} first={4:F3} min={5:F3} max={6:F3} last={7:F3} n={8} {9}",
                        f, prevF, ok ? "PASS" : "FAIL", worst, first, minB, maxB, last, samples, Left.SeekState()));
                    prevF = f;
                }

                // 点空（值没变）不该触发定位：以前这里会把播放器拽回旧刻度
                double barBefore = Left.SeekFraction();
                string stBefore = Left.SeekState();
                Left.DebugClickBlank();
                await Task.Delay(600);
                double barAfter = Left.SeekFraction();
                bool blankOk = Math.Abs(barAfter - barBefore) < 0.02;
                if (blankOk) pass++; else fail++;
                sb.AppendLine(string.Format("blank_click {0} before={1:F3} after={2:F3} state_before={3}",
                    blankOk ? "PASS" : "FAIL", barBefore, barAfter, stBefore));

                sb.AppendLine("SUMMARY pass=" + pass + " fail=" + fail);
                sb.AppendLine(fail == 0 ? "RESULT_OK" : "RESULT_FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT_EXCEPTION " + ex);
            }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clicktest.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        async Task RunSeekTest(string file, string csv, int waitMs)
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            try
            {
                await Left.Load(file);
                for (int i = 0; i < 120; i++)
                {
                    if (Left.HasMedia && Left.Player.NaturalDuration.HasTimeSpan) break;
                    await Task.Delay(250);
                }
                await Task.Delay(800);
                sb.AppendLine("file=" + file + " waitMs=" + waitMs);

                string[] parts = csv.Split(',');
                double prevF = -1;
                foreach (string raw in parts)
                {
                    double f;
                    if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    Left.DebugSeekFraction(f);
                    double worst = 0, first = -1, last = -1, minB = 9, maxB = -9;
                    int samples = 0;
                    for (int t = 0; t < waitMs; t += 100)
                    {
                        await Task.Delay(100);
                        double b = Left.SeekFraction();
                        if (first < 0) first = b;
                        last = b;
                        if (b < minB) minB = b;
                        if (b > maxB) maxB = b;
                        // 「弹回原刻度」的判据：拇指朝跳转的反方向跑了。
                        // 向后跳就绝不允许跑回跳转前的位置，向前跳就绝不允许掉回跳转前的位置。
                        double bad = 0;
                        // 第一次跳：只在前 400ms 要求「准」，之后播放自然前进不算问题
                        if (prevF < 0) bad = t < 400 ? Math.Abs(b - f) : Math.Max(0, (f - 0.02) - b);
                        else if (f > prevF + 0.02) bad = Math.Max(0, (f - 0.02) - b);
                        else if (f < prevF - 0.02) bad = Math.Max(0, b - (f + 0.02));
                        else bad = Math.Max(0, Math.Abs(b - f) - 0.02);
                        if (bad > worst) worst = bad;
                        samples++;
                    }
                    bool ok = worst <= 0.005 && Math.Abs(first - f) <= 0.03;
                    if (ok) pass++; else fail++;
                    sb.AppendLine(string.Format(
                        "seek f={0:F3} prev={1:F3} {2} snapback={3:P1} first={4:F3} min={5:F3} max={6:F3} last={7:F3} n={8} {9}",
                        f, prevF, ok ? "PASS" : "FAIL", worst, first, minB, maxB, last, samples, Left.SeekState()));
                    prevF = f;
                }
                sb.AppendLine("SUMMARY pass=" + pass + " fail=" + fail);
                sb.AppendLine(fail == 0 ? "RESULT_OK" : "RESULT_FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT_EXCEPTION " + ex);
            }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "seektest.txt"),
                        sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        void Describe(System.Text.StringBuilder sb, string tag, PaneView p)
        {
            if (!p.HasMedia) { sb.AppendLine(tag + " (无媒体)"); return; }
            var m = p.Info;
            sb.AppendLine(string.Format(
                "{0}: name={1} container={2} video={3} {4} {5} fps={6} depth={7} audio={8} {9} {10}Hz dur={11:F1} size={12}",
                tag, m.Name, m.Container, m.VideoCodec, m.ResolutionText, m.FpsText, m.Fps,
                m.BitDepth, m.AudioText, m.ChannelText, m.SampleRate, m.Duration, m.SizeHuman));
            if (p.Player.NaturalDuration.HasTimeSpan)
                sb.AppendLine(string.Format("{0}: mediaOpened dur={1:F2} natural={2}x{3}",
                    tag, p.Player.NaturalDuration.TimeSpan.TotalSeconds,
                    p.Player.NaturalVideoWidth, p.Player.NaturalVideoHeight));
            else
                sb.AppendLine(tag + ": mediaOpened=false");
        }

        public void SetTopStatus(string text)
        {
            topStatus.Text = text ?? "";
        }

        void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                var target = Left.HasMedia ? Left : (Right.HasMedia ? Right : null);
                if (target != null) { target.TogglePlay(); e.Handled = true; }
            }
            else if (e.Key == Key.Left || e.Key == Key.Right)
            {
                double delta = (e.Key == Key.Left ? -5 : 5);
                if (Keyboard.Modifiers == ModifierKeys.Shift) delta = (e.Key == Key.Left ? -0.04 : 0.04);
                if (Keyboard.Modifiers == ModifierKeys.Control) delta *= 12;
                if (Left.HasMedia) Left.SeekRelative(delta);
                if (Right.HasMedia) Right.SeekRelative(delta);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var target = Left.HasMedia ? Right : Left;
                string start = (target.Info != null && !string.IsNullOrEmpty(target.Info.Dir))
                    ? target.Info.Dir
                    : AppSettings.LastDir;
                string picked = FilePickerWindow.PickFile(this, start, "选择视频");
                if (!string.IsNullOrEmpty(picked))
                {
                    var pane = target;
                    Async.Fire(() => pane.Load(picked));
                }
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------- 同步
        public void SyncPlay()
        {
            Mirroring = true;
            try
            {
                if (Left.HasMedia) Left.Play();
                if (Right.HasMedia) Right.Play();
            }
            finally { Mirroring = false; }
        }

        public void SyncPause()
        {
            Mirroring = true;
            try
            {
                if (Left.HasMedia) Left.Pause();
                if (Right.HasMedia) Right.Pause();
            }
            finally { Mirroring = false; }
        }

        public void MirrorPlay(PaneView from, bool playing)
        {
            if (Mirroring || !Linked) return;
            var other = from == Left ? Right : Left;
            if (other == null || !other.HasMedia) return;
            Mirroring = true;
            try { if (playing) other.Play(); else other.Pause(); }
            finally { Mirroring = false; }
        }

        public void MirrorSeek(PaneView from, double seconds)
        {
            if (Mirroring || !Linked) return;
            var other = from == Left ? Right : Left;
            if (other == null || !other.HasMedia) return;
            if (Math.Abs(other.GlobalPosition() - seconds) < 0.3) return;
            Mirroring = true;
            try { other.SeekTo(seconds); }
            finally { Mirroring = false; }
        }

        public void MirrorVolume(PaneView from)
        {
            if (Mirroring || !Linked) return;
            var other = from == Left ? Right : Left;
            if (other == null) return;
            // 由 PaneView 内部控制音量条，这里仅同步播放器音量
            Mirroring = true;
            try { other.Player.Volume = from.Player.Volume; }
            finally { Mirroring = false; }
        }

        public void MirrorMute(PaneView from, bool mute)
        {
            if (Mirroring || !Linked) return;
            var other = from == Left ? Right : Left;
            if (other == null) return;
            Mirroring = true;
            try { other.SetMuted(mute); }
            finally { Mirroring = false; }
        }

        // ---------------------------------------------------------------- 深色标题栏
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>把系统画的白色标题栏切成深色（Win10 1809+ 的属性 20，旧版为 19）。</summary>
        public static void DarkTitleBar(Window w)
        {
            try
            {
                IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar(this);
        }

        // ---------------------------------------------------------------- 载入/交换/差异
        public async Task LoadBoth(string a, string b)
        {
            await Left.Load(a);
            await Right.Load(b);
            UpdateDiff();
        }

        public void AfterPaneLoad(PaneView pane)
        {
            UpdateDiff();
        }

        public async Task Swap()
        {
            if (!Left.HasMedia && !Right.HasMedia) { SetTopStatus("两侧都还没有载入视频"); return; }
            string lp = Left.CurrentPath, rp = Right.CurrentPath;
            await Left.Load(rp);
            await Right.Load(lp);
            SetTopStatus("已交换左右");
        }

        public void UpdateDiff()
        {
            if (Left == null || Right == null) return;
            bool both = Left.HasMedia && Right.HasMedia;
            int diffCount = 0;
            foreach (var key in Left.ValueTexts.Keys)
            {
                if (PaneView.IsNoDiff(key)) continue;
                var lb = Left.RowBorders[key];
                var rb = Right.RowBorders[key];
                string lv = Left.RowValue(key);
                string rv = Right.RowValue(key);
                bool diff = both && !string.IsNullOrEmpty(lv) && !string.IsNullOrEmpty(rv)
                            && lv != "-" && rv != "-" && lv != rv;
                lb.Background = diff ? Theme.DiffBg : Brushes.Transparent;
                lb.BorderBrush = diff ? Theme.Amber : Brushes.Transparent;
                lb.BorderThickness = new Thickness(diff ? 2 : 0, 0, 0, 0);
                rb.Background = diff ? Theme.DiffBg : Brushes.Transparent;
                rb.BorderBrush = diff ? Theme.Amber : Brushes.Transparent;
                rb.BorderThickness = new Thickness(diff ? 2 : 0, 0, 0, 0);
                if (diff) diffCount++;
            }
            if (both)
                SetTopStatus(diffCount > 0 ? ("两侧共有 " + diffCount + " 项元数据不同") : "两侧元数据一致");
        }
    }

    public static class Async
    {
        /// <summary>fire-and-forget，异常不吞掉（写到调试输出）。</summary>
        public static void Fire(System.Func<Task> action)
        {
            if (action == null) return;
            var t = action();
            if (t == null) return;
            t.ContinueWith(x =>
            {
                if (x.Exception != null) System.Diagnostics.Debug.WriteLine(x.Exception);
            }, TaskScheduler.Default);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args == null) args = new string[0];

            // 1) 配置先读（后续缓存目录、窗口位置都依赖它）
            AppSettings.Load();

            // 2) 诊断模式（--selftest 等）不参与单实例，方便脚本连续跑
            bool diag = IsDiagnostic(args);

            if (!diag)
            {
                using (var single = SingleInstance.TryAcquire())
                {
                    if (!single.IsFirst)
                    {
                        // 已经在运行：把那个窗口提到前台，本进程直接退出
                        SingleInstance.ActivateExisting();
                        return;
                    }
                    RunApp(args, diag);
                }
            }
            else
            {
                RunApp(args, diag);
            }
        }

        static void RunApp(string[] args, bool diag)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

            // 3) 未捕获异常兜底：写日志 + 弹窗告知路径
            CrashGuard.Install(app);

            // 4) 后台把转码缓存压回上限，别让它在磁盘上无限长大
            try
            {
                var t = new Thread(delegate()
                {
                    try { FfmpegHelper.TrimCache(AppSettings.CacheLimitMB); } catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }

            var win = new MainWindow(args, diag);
            app.Run(win);
        }

        static bool IsDiagnostic(string[] args)
        {
            string[] switches = { "--selftest", "--uicheck", "--seektest", "--clicktest", "--barrect", "--pickcheck", "--pickshow" };
            for (int i = 0; i < args.Length; i++)
            {
                for (int j = 0; j < switches.Length; j++)
                {
                    if (string.Equals(args[i], switches[j], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }
}
