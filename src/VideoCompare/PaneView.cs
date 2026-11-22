using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoCompare
{
    public static class Theme
    {
        public static readonly Brush Bg = Brush("#0e1014");
        public static readonly Brush Panel = Brush("#161a21");
        public static readonly Brush Panel2 = Brush("#1c212b");
        public static readonly Brush Line = Brush("#2a3140");
        public static readonly Brush Text = Brush("#e7ebf3");
        public static readonly Brush Muted = Brush("#8d97ab");
        public static readonly Brush Muted2 = Brush("#6b7488");
        public static readonly Brush Accent = Brush("#4d8dff");
        public static readonly Brush Green = Brush("#34d399");
        public static readonly Brush Amber = Brush("#fbbf24");
        public static readonly Brush Red = Brush("#f16c6c");
        public static readonly Brush DiffBg = Brush("#3a2f14");

        public static Brush Brush(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public static TextBlock Label(string text, double size = 12, Brush brush = null, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = brush ?? Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = weight ?? FontWeights.Normal,
            };
        }

        public static Button Btn(string text, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Background = primary ? Accent : Panel2,
                Foreground = primary ? Brush("#08111f") : Text,
                BorderBrush = primary ? Accent : Line,
                BorderThickness = new Thickness(1),
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            };
            b.Template = BuildButtonTemplate();
            return b;
        }

        static ControlTemplate BuildButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(System.Windows.Controls.Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(System.Windows.Controls.Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(System.Windows.Controls.Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(System.Windows.Controls.Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }
    }

    /// <summary>一栏：播放器 + 控制条 + 操作按钮 + 信息面板。</summary>
    public class PaneView
    {
        public readonly string Side;              // "left" / "right"
        public readonly Border Root;
        public MediaElement Player;
        public MediaInfo Info;
        public string CurrentPath;
        public bool HasMedia { get { return Info != null && !string.IsNullOrEmpty(CurrentPath); } }
        public bool IsFallbackClip;               // 当前播的是转码片段
        public double ClipOffset;

        readonly TextBlock nameText;
        readonly Border stage;
        readonly Grid stageGrid;
        readonly TextBlock hintText;
        readonly TextBlock statusText;
        readonly ProgressBar progressBar;
        readonly Button playButton;
        readonly Slider seekBar;
        readonly Slider volumeBar;
        readonly ToggleButton muteBtn;
        readonly System.Windows.Shapes.Path muteSlash;
        readonly ComboBox speedBox;
        readonly TextBlock timeText;
        readonly StackPanel infoPanel;
        readonly ScrollViewer infoScroll;
        public readonly Dictionary<string, TextBlock> ValueTexts = new Dictionary<string, TextBlock>();
        public readonly Dictionary<string, Border> RowBorders = new Dictionary<string, Border>();

        readonly DispatcherTimer timer;
        readonly MainWindow host;
        readonly PrefetchCache cache = new PrefetchCache();
        bool muted;
        double lastFrac, lastDur, lastShown;
        long lastLen;
        bool draggingSeek;
        double pendingSeekSeconds = -1;
        double pendingSeekTarget = -1;    // 已请求、但播放器还没真正到位的位置（秒，全局）
        DateTime pendingSeekAt;
        DateTime lastReassertAt;          // 上一次重新下发定位的时间
        int pendingStable;                // 连续「已到位」的拍数，够了才交还控制权
        int pendingRetries;               // 重新下发定位的次数（播放器会悄悄退回旧位置）
        double seekTarget = -1;           // 最近一次用户要求的位置
        DateTime seekReqAt;               // 最近一次用户要求定位的时间
        double posBeforeSeek;             // 下发定位前播放头在哪（用来判断「有没有真的动」）
        int seekDir;                      // +1 向后跳、-1 向前回退、0 原地微调
        bool seekValueGuard;              // 程序写 seekBar.Value 时置位，避免被当成用户操作
        public static bool SeekLogOn;     // --seeklog：把进度条相关事件写进 seeklog.txt
        static readonly object seekLogGate = new object();
        double logTickUntil = -1;         // 定位后 3 秒内的 tick 也记下来
        double shownPos = -1;             // 进度条上当前显示的位置（播放器回退时靠它守住刻度）
        int displayDrift;                 // 播放头连续「比显示位置更靠前」的拍数
        double valueAtDown;               // 用户动进度条之前的值
        DateTime lastUserTouch;           // 用户最后一次动进度条的时间（这段时间内程序不许改写刻度）
        CancellationTokenSource transcodeCts;
        int fallbackStage; // 0未尝试 1已转封装 2已转码片段 3放弃

        public PaneView(MainWindow host, string side, string label)
        {
            this.host = host;
            Side = side;

            var grid = new Grid { Margin = new Thickness(0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                   // 标题
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 播放区
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                   // 控制条
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                   // 操作
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 信息（按内容自适应，尽量不滚动）

            // ---- 标题行 ----
            var head = new Grid { Margin = new Thickness(12, 9, 12, 9) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Background = side == "left" ? Theme.Accent : Theme.Green,
                CornerRadius = new CornerRadius(6),
                Width = 22, Height = 22,
                Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock
                {
                    Text = label, FontSize = 11.5, FontWeight = FontWeights.Bold,
                    Foreground = Theme.Brush("#0b1220"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
            Grid.SetColumn(badge, 0);
            head.Children.Add(badge);

            nameText = Theme.Label("未选择视频", 13, Theme.Muted, FontWeights.SemiBold);
            nameText.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(nameText, 1);
            head.Children.Add(nameText);

            var pickBtn = Theme.Btn("选择视频…", true);
            pickBtn.Click += async (s, e) => await PickFile();
            Grid.SetColumn(pickBtn, 2);
            head.Children.Add(pickBtn);

            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---- 播放区（拖放目标）----
            Player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Stretch = Stretch.Uniform,
                Volume = 1.0,
            };
            Player.MediaOpened += Player_MediaOpened;
            Player.MediaFailed += Player_MediaFailed;
            Player.MediaEnded += Player_MediaEnded;

            var overlay = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            hintText = Theme.Label("把视频拖到这里，或点右上角「选择视频…」", 13, Theme.Muted2);
            hintText.HorizontalAlignment = HorizontalAlignment.Center;
            overlay.Children.Add(hintText);

            progressBar = new ProgressBar
            {
                Height = 4, Width = 260, Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed,
                Foreground = Theme.Accent, Background = Theme.Line, BorderThickness = new Thickness(0),
            };
            statusText = Theme.Label("", 11.5, Theme.Muted2);
            statusText.HorizontalAlignment = HorizontalAlignment.Center;
            statusText.Margin = new Thickness(0, 6, 0, 0);
            overlay.Children.Add(progressBar);
            overlay.Children.Add(statusText);

            stageGrid = new Grid();
            stageGrid.Children.Add(Player);
            stageGrid.Children.Add(overlay);

            stage = new Border
            {
                Background = Theme.Brush("#06070a"),
                AllowDrop = true,
                Child = stageGrid,
            };
            stage.DragOver += Stage_DragOver;
            stage.Drop += Stage_Drop;
            Grid.SetRow(stage, 1);
            grid.Children.Add(stage);

            // ---- 控制条 ----
            var bar = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            playButton = Theme.Btn("▶ 播放");
            playButton.Width = 78;
            playButton.Click += (s, e) => TogglePlay();
            Grid.SetColumn(playButton, 0);
            bar.Children.Add(playButton);

            var backBtn = Theme.Btn("« 5s");
            backBtn.Click += (s, e) => { SeekRelative(-5); };
            Grid.SetColumn(backBtn, 1);
            bar.Children.Add(backBtn);

            var fwdBtn = Theme.Btn("5s »");
            fwdBtn.Click += (s, e) => { SeekRelative(5); };
            Grid.SetColumn(fwdBtn, 2);
            bar.Children.Add(fwdBtn);

            seekBar = new Slider
            {
                Minimum = 0, Maximum = 1000, Value = 0,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = Styles.Get("SeekSlider"),
                IsEnabled = false,
            };
            // 注意：Slider 自己的类处理器处理「点到某处」时会把 PreviewMouseDown 标成 Handled，
            // 普通 += 根本收不到（真实点击的日志里就只有 mouse_up、没有 mouse_down）。
            // 所以必须 handledEventsToo:true 注册。
            seekBar.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                draggingSeek = true;
                valueAtDown = seekBar.Value;
                L("mouse_down value=" + seekBar.Value.ToString("0.0", CultureInfo.InvariantCulture) + " pinned=" + pendingSeekTarget.ToString("0.0", CultureInfo.InvariantCulture));
            }), true);
            seekBar.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler((s, e) =>
            {
                draggingSeek = false;
                double v = seekBar.Value;
                L("mouse_up value=" + v.ToString("0.0", CultureInfo.InvariantCulture) + " down=" + valueAtDown.ToString("0.0", CultureInfo.InvariantCulture));
                CommitSeek();
            }), true);
            // 程序自己刷新拇指位置时（每 200ms）不能被当成用户操作，否则会来回打架。
            // 反过来：只要不是程序写的（guard=0），就是用户在点/拖 —— 立刻占住进度条，
            // 否则下一拍（200ms 内）就会被程序写回旧刻度，看起来就是「点了又弹回原刻度」。
            seekBar.ValueChanged += (s, e) =>
            {
                L("value_changed " + e.OldValue.ToString("0.0", CultureInfo.InvariantCulture) + "->" + e.NewValue.ToString("0.0", CultureInfo.InvariantCulture) + " guard=" + (seekValueGuard ? 1 : 0) + " dragging=" + (draggingSeek ? 1 : 0));
                if (seekValueGuard) return;
                if (!draggingSeek) valueAtDown = e.OldValue;
                draggingSeek = true;
                lastUserTouch = DateTime.UtcNow;
            };
            seekBar.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler((s, e) =>
            {
                L("lost_capture dragging=" + (draggingSeek ? 1 : 0) + " value=" + seekBar.Value.ToString("0.0", CultureInfo.InvariantCulture));
                if (!draggingSeek) return;
                draggingSeek = false;
                CommitSeek();
            }), true);
            Grid.SetColumn(seekBar, 3);
            bar.Children.Add(seekBar);

            timeText = Theme.Label("00:00 / 00:00", 11.5, Theme.Muted);
            timeText.MinWidth = 110;
            timeText.TextAlignment = TextAlignment.Center;
            Grid.SetColumn(timeText, 4);
            bar.Children.Add(timeText);

            // ---- 静音开关（点一下关掉/恢复声音，图标出现红斜杠）----
            muteBtn = new ToggleButton
            {
                Style = Styles.Get("MuteToggle"),
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "静音（点一下关掉声音）",
                IsEnabled = false,
            };
            var muteIcon = new Grid { Width = 16, Height = 16 };
            muteIcon.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1.6,5.6 H4.3 L8.2,2.2 V13.8 L4.3,10.4 H1.6 Z"),
                Fill = Theme.Text,
            });
            muteIcon.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M10.3,5.3 A3.6,3.6 0 0 1 10.3,10.7"),
                Stroke = Theme.Text, StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            muteIcon.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12.4,3.4 A6.2,6.2 0 0 1 12.4,12.6"),
                Stroke = Theme.Text, StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            muteSlash = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1.2,1.2 L14.8,14.8"),
                Stroke = Theme.Red, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed,
            };
            muteIcon.Children.Add(muteSlash);
            muteBtn.Content = muteIcon;
            muteBtn.Checked += (s, e) => ApplyMute(true);
            muteBtn.Unchecked += (s, e) => ApplyMute(false);
            Grid.SetColumn(muteBtn, 5);
            bar.Children.Add(muteBtn);

            volumeBar = new Slider
            {
                Minimum = 0, Maximum = 100, Value = 100, Width = 78,
                Margin = new Thickness(8, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = Styles.Get("VolumeSlider"),
                IsEnabled = false,
            };
            volumeBar.ValueChanged += (s, e) =>
            {
                Player.Volume = volumeBar.Value / 100.0;
                if (host.Linked && !host.Mirroring) host.MirrorVolume(this);
            };
            Grid.SetColumn(volumeBar, 6);
            bar.Children.Add(volumeBar);

            speedBox = new ComboBox
            {
                Width = 78, FontSize = 12,
                Style = Styles.Get("FlatCombo"),
            };
            foreach (var s2 in new[] { "0.25", "0.5", "0.75", "1", "1.25", "1.5", "2", "4" })
                speedBox.Items.Add(s2 + "×");
            speedBox.SelectedIndex = 3;
            speedBox.SelectionChanged += (s, e) => ApplySpeed();
            Grid.SetColumn(speedBox, 7);
            bar.Children.Add(speedBox);

            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);

            // ---- 操作按钮 ----
            var ops = new WrapPanel { Margin = new Thickness(12, 0, 12, 9) };
            var stopBtn = Theme.Btn("■ 停止");
            stopBtn.Click += (s, e) => StopPlayback(true);
            var renameBtn = Theme.Btn("重命名");
            renameBtn.Click += async (s, e) => await Rename();
            var moveBtn = Theme.Btn("移动");
            moveBtn.Click += async (s, e) => await Move();
            var delBtn = Theme.Btn("删除");
            delBtn.Foreground = Theme.Red;
            delBtn.Click += (s, e) => Delete();
            var revealBtn = Theme.Btn("打开位置");
            revealBtn.Click += (s, e) => RevealFile();
            var reloadBtn = Theme.Btn("重载");
            reloadBtn.Click += async (s, e) => { if (HasMedia) await Load(CurrentPath, true); };
            ops.Children.Add(stopBtn);
            ops.Children.Add(renameBtn);
            ops.Children.Add(moveBtn);
            ops.Children.Add(delBtn);
            ops.Children.Add(revealBtn);
            ops.Children.Add(reloadBtn);
            Grid.SetRow(ops, 3);
            grid.Children.Add(ops);

            // ---- 信息面板 ----
            infoScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(12, 0, 12, 12),
                MaxHeight = 470,        // 信息区最多占这么高，再长才滚动
            };
            infoPanel = new StackPanel();
            infoScroll.Content = infoPanel;
            Grid.SetRow(infoScroll, 4);
            grid.Children.Add(infoScroll);

            Root = new Border
            {
                Background = Theme.Panel,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(0),
                Child = grid,
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += Timer_Tick;
            timer.Start();

            BuildInfoRows();
        }

        // ---------------------------------------------------------------- 元数据行
        class RowDef
        {
            public string Key, Label, Group;
            public bool Wide;                 // 占满整行（文件名、路径这类会很长）
            public Func<MediaInfo, string> Get;
        }

        static readonly RowDef[] Rows =
        {
            new RowDef { Key="name", Label="文件名", Group="文件信息", Wide = true, Get = m => m.Name },
            new RowDef { Key="container", Label="容器格式", Group="文件信息", Get = m => m.Container },
            new RowDef { Key="duration", Label="时长", Group="文件信息", Get = m => m.DurationText },
            new RowDef { Key="size", Label="文件大小", Group="文件信息", Get = m => m.SizeHuman },
            new RowDef { Key="bitrate_total", Label="总码率", Group="文件信息", Get = m => m.BitrateText(m.BitRateTotal) },
            new RowDef { Key="storage", Label="存储位置", Group="文件信息", Get = m => m.IsNetwork ? "网络共享（SMB/UNC）" : "本地磁盘" },

            new RowDef { Key="v_codec", Label="编码方式", Group="视频", Get = m => m.VideoCodec },
            new RowDef { Key="v_profile", Label="Profile / Level", Group="视频", Get = m =>
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(m.VideoProfile)) parts.Add(m.VideoProfile);
                    if (!string.IsNullOrEmpty(m.VideoLevel)) parts.Add("Level " + m.VideoLevel);
                    return parts.Count == 0 ? "-" : string.Join(" · ", parts.ToArray());
                } },
            new RowDef { Key="v_res", Label="分辨率", Group="视频", Get = m => m.ResolutionText },
            new RowDef { Key="v_fps", Label="帧率", Group="视频", Get = m => m.FpsText },
            new RowDef { Key="v_bitrate", Label="视频码率", Group="视频", Get = m => m.BitrateText(m.VideoBitrate) },
            new RowDef { Key="v_frames", Label="总帧数", Group="视频", Get = m => m.Frames > 0 ? m.Frames.ToString("N0") + " 帧" : "-" },
            new RowDef { Key="v_pixfmt", Label="像素格式", Group="视频", Get = m => string.IsNullOrEmpty(m.PixFmt) ? "-" : m.PixFmt },
            new RowDef { Key="v_depth", Label="位深", Group="视频", Get = m => m.BitDepth > 0 ? m.BitDepth + " bit" : "-" },
            new RowDef { Key="v_color", Label="色彩 / HDR", Group="视频", Get = m => string.IsNullOrEmpty(m.ColorDesc) ? "-" : m.ColorDesc },
            new RowDef { Key="v_rotate", Label="旋转", Group="视频", Get = m => m.Rotation > 0 ? m.Rotation + "°" : "-" },

            new RowDef { Key="a_codec", Label="编码方式", Group="音频", Get = m => m.AudioText },
            new RowDef { Key="a_channels", Label="声道", Group="音频", Get = m => m.ChannelText },
            new RowDef { Key="a_rate", Label="采样率", Group="音频", Get = m => m.SampleRate > 0 ? (m.SampleRate % 1000 == 0 ? (m.SampleRate / 1000) + " kHz" : (m.SampleRate / 1000.0).ToString("0.0") + " kHz") : "-" },
            new RowDef { Key="a_depth", Label="位深", Group="音频", Get = m => m.AudioBitDepth > 0 ? m.AudioBitDepth + " bit" : "-" },
            new RowDef { Key="a_bitrate", Label="码率", Group="音频", Get = m => m.HasAudio ? m.BitrateText(m.AudioBitrate) : "无音轨" },
            new RowDef { Key="a_lang", Label="语言", Group="音频", Get = m => string.IsNullOrEmpty(m.AudioLanguage) ? "-" : m.AudioLanguage },

            new RowDef { Key="subs", Label="字幕流", Group="其他", Get = m => m.SubtitleStreams + " 条" },
            new RowDef { Key="audios", Label="音轨数量", Group="其他", Get = m => m.AudioStreams + " 条" },
            new RowDef { Key="path", Label="完整路径", Group="其他", Wide = true, Get = m => m.Path },
        };

        // 不参与左右差异比较的字段
        static readonly HashSet<string> NoDiff = new HashSet<string> { "name", "size", "storage", "path", "v_frames" };

        void BuildInfoRows()
        {
            string group = null;
            Grid grid = null;
            int row = 0, col = 0;

            foreach (var def in Rows)
            {
                if (def.Group != group)
                {
                    group = def.Group;
                    infoPanel.Children.Add(new TextBlock
                    {
                        Text = group,
                        FontSize = 10.5,
                        Foreground = Theme.Muted2,
                        Margin = new Thickness(2, 7, 0, 3),
                    });
                    grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    infoPanel.Children.Add(grid);
                    row = 0;
                    col = 0;
                }

                var border = BuildRowBorder(def, !def.Wide);

                if (def.Wide)
                {
                    if (col == 1) { row++; col = 0; }
                    EnsureRow(grid, row);
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, 0);
                    Grid.SetColumnSpan(border, 2);
                    border.Margin = new Thickness(0);
                    row++;
                    col = 0;
                }
                else
                {
                    EnsureRow(grid, row);
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, col);
                    border.Margin = col == 0 ? new Thickness(0, 0, 10, 0) : new Thickness(0);
                    col++;
                    if (col == 2) { col = 0; row++; }
                }
                grid.Children.Add(border);
            }
        }

        static void EnsureRow(Grid g, int row)
        {
            while (g.RowDefinitions.Count <= row)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Border BuildRowBorder(RowDef def, bool halfWidth)
        {
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(halfWidth ? 72 : 84) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = Theme.Label(def.Label, 12, Theme.Muted2);
            Grid.SetColumn(lbl, 0);
            rowGrid.Children.Add(lbl);

            var val = Theme.Label("-", 12.5, Theme.Text);
            if (halfWidth)
            {
                val.TextWrapping = TextWrapping.NoWrap;
                val.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else val.TextWrapping = TextWrapping.Wrap;
            val.Cursor = Cursors.IBeam;
            Grid.SetColumn(val, 1);
            rowGrid.Children.Add(val);

            var border = new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(5),
                Child = rowGrid,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
            };

            ValueTexts[def.Key] = val;
            RowBorders[def.Key] = border;
            return border;
        }

        public string RowValue(string key)
        {
            TextBlock tb;
            return ValueTexts.TryGetValue(key, out tb) ? tb.Text : "";
        }

        public void UpdateInfoRows()
        {
            foreach (var def in Rows)
            {
                TextBlock tb;
                if (!ValueTexts.TryGetValue(def.Key, out tb)) continue;
                string text = "-";
                try { if (Info != null) text = def.Get(Info) ?? "-"; } catch { }
                if (string.IsNullOrEmpty(text)) text = "-";
                tb.Text = text;
                tb.ToolTip = text.Length > 16 ? text : null;
            }
        }

        public static bool IsNoDiff(string key) { return NoDiff.Contains(key); }

        /// <summary>把用户/命令行给的路径归一：去引号、file:// 转本地、正斜杠转反斜杠、UNC 补足双反斜杠、相对路径转绝对路径。</summary>
        public static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Trim();
            if (p.Length > 1 && p[0] == '\u0022' && p[p.Length - 1] == '\u0022') p = p.Substring(1, p.Length - 2);
            if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { p = new Uri(p).LocalPath; } catch { }
            }
            p = p.Replace('/', '\\');
            if (p.StartsWith("\\", StringComparison.Ordinal))
            {
                p = "\\\\" + p.TrimStart('\\');   // UNC 必须是两个反斜杠
            }
            try
            {
                if (!Path.IsPathRooted(p)) p = Path.GetFullPath(p);
            }
            catch { }
            return p;
        }

        // ---------------------------------------------------------------- 载入
        public async Task<bool> Load(string path, bool keepPosition = false)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = NormalizePath(path);
            double resume = keepPosition ? CurrentPosition() : 0;
            StopPlayback(false);

            SetStatus("正在读取信息…", true);
            nameText.Text = Path.GetFileName(path);
            nameText.Foreground = Theme.Text;
            nameText.ToolTip = path;

            MediaInfo info = null;
            await Task.Run(() => { info = MediaProbe.Probe(path); });

            Info = info;
            CurrentPath = path;
            UpdateInfoRows();
            host.AfterPaneLoad(this);

            SetStatus("", false);
            hintText.Visibility = Visibility.Collapsed;
            seekBar.IsEnabled = true;
            volumeBar.IsEnabled = true;
            fallbackStage = 0;
            IsFallbackClip = false;
            ClipOffset = 0;

            PlaySource(path);

            if (Info != null && Info.IsNetwork) cache.Start(path);
            else cache.Stop();
            muteBtn.IsEnabled = true;
            ApplyMute(muted);

            if (resume > 0)
            {
                pendingSeekSeconds = resume;
            }
            return true;
        }

        void PlaySource(string path)
        {
            // 换源等于换了时间轴，定位状态与「显示位置」总闸都要清零
            shownPos = -1;
            pendingSeekTarget = -1;
            pendingStable = 0;
            pendingRetries = 0;
            seekTarget = -1;
            try
            {
                Player.Source = new Uri(path, UriKind.Absolute);
                Player.Position = TimeSpan.Zero;
                Player.Play();
                SetPlayIcon(true);
            }
            catch (Exception ex)
            {
                SetStatus("无法播放：" + ex.Message, false);
            }
        }

        // ---------------------------------------------------------------- 播放事件
        void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (pendingSeekSeconds > 0)
            {
                var t = pendingSeekSeconds;
                pendingSeekSeconds = -1;
                try { Player.Position = TimeSpan.FromSeconds(t); } catch { }
            }
            if (IsFallbackClip && Info != null)
                SetStatus(string.Format("正在播放转码片段（原片 {0} 起）", FormatClock(ClipOffset)), false);
            else
                SetStatus("", false);
            ApplySpeed();
            try { Player.IsMuted = muted; } catch { }

            // 有些编码 MediaElement 会“打开但出不来画面”（natural=0x0），也要走兜底
            if (Info != null && Info.Width > 0)
            {
                Async.Fire(async () =>
                {
                    await Task.Delay(600);
                    if (Info != null && Info.Width > 0 && Player.NaturalVideoWidth == 0 && fallbackStage < 3)
                    {
                        await FallbackAsync();
                    }
                });
            }
        }

        void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string msg = e.ErrorException != null ? e.ErrorException.Message : "未知错误";
            if (Info != null && fallbackStage < 3)
            {
                Async.Fire(() => FallbackAsync());
                return;
            }
            SetStatus("播放失败：" + msg, false);
            hintText.Text = "这个编码无法播放，可以点「重载」重试，或用 ffmpeg 转码后再看";
            hintText.Visibility = Visibility.Visible;
        }

        void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            SetPlayIcon(false);
            if (IsFallbackClip)
                SetStatus("片段播放结束（点「5s»」或拖动进度条会从新位置续转）", false);
        }

        async Task FallbackAsync()
        {
            string src = CurrentPath;
            if (string.IsNullOrEmpty(src)) return;
            bool network = Info != null && Info.IsNetwork;
            double start = IsFallbackClip ? ClipOffset + CurrentPosition() : CurrentPosition();

            transcodeCts = new CancellationTokenSource();
            var token = transcodeCts.Token;

            if (fallbackStage == 0)
            {
                fallbackStage = 1;
                SetStatus("编码不受支持，正在尝试转封装（很快）…", true, true);
                string remuxed = await Task.Run(() => FfmpegHelper.Remux(src, Info, null, token));
                if (remuxed != null)
                {
                    SetStatus("已转封装，正在播放…", false);
                    IsFallbackClip = false;
                    ClipOffset = 0;
                    PlaySource(remuxed);
                    return;
                }
            }
            fallbackStage = 2;

            double window = 60;
            SetStatus(string.Format("正在转码片段（{0} 起 {1:0} 秒）…", FormatClock(start), window), true, true);
            var progress = new Progress<double>(sec =>
            {
                progressBar.Value = Math.Min(100, sec / window * 100);
            });
            string clip = await Task.Run(() =>
            {
                double last = 0;
                return FfmpegHelper.TranscodeClip(src, start, window, sec =>
                {
                    if (sec - last > 0.5) { last = sec; ((IProgress<double>)progress).Report(sec); }
                }, token);
            });

            if (clip == null)
            {
                fallbackStage = 3;
                SetStatus("转码失败。可以先在系统里用播放器确认文件是否完好。", false, false, true);
                hintText.Text = "转码失败（可能没有 ffmpeg，或文件损坏）";
                hintText.Visibility = Visibility.Visible;
                return;
            }
            IsFallbackClip = true;
            ClipOffset = start;
            fallbackStage = 2;
            SetStatus(string.Format("正在播放转码片段（{0} – {1}）", FormatClock(start), FormatClock(start + window)), false);
            PlaySource(clip);
        }

        // ---------------------------------------------------------------- 播放控制
        public void TogglePlay()
        {
            if (!HasMedia) return;
            if (Player.IsBuffering) { }
            bool playing = IsPlaying();
            if (playing) Pause(); else Play();
        }

        public void Play()
        {
            if (!HasMedia) return;
            Player.Play();
            SetPlayIcon(true);
            if (host.Linked && !host.Mirroring) host.MirrorPlay(this, true);
        }

        public void Pause()
        {
            Player.Pause();
            SetPlayIcon(false);
            if (host.Linked && !host.Mirroring) host.MirrorPlay(this, false);
        }

        public bool IsPlaying()
        {
            // MediaElement 没有公开的播放状态，用 Position 是否推进来判断
            return Player.CanPause && Player.Position > _lastPosition.Add(TimeSpan.FromMilliseconds(1))
                   || (Player.CanPause && _isPlayingFlag);
        }

        bool _isPlayingFlag;
        TimeSpan _lastPosition;

        public void SetPlayIcon(bool playing)
        {
            _isPlayingFlag = playing;
            playButton.Content = playing ? "⏸ 暂停" : "▶ 播放";
        }

        void ApplySpeed()
        {
            string s = speedBox.SelectedItem as string ?? "1×";
            s = s.TrimEnd('×');
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                Player.SpeedRatio = v;
        }

        public double Duration()
        {
            if (Player.NaturalDuration.HasTimeSpan) return Player.NaturalDuration.TimeSpan.TotalSeconds;
            return Info != null ? Info.Duration : 0;
        }

        public double CurrentPosition()
        {
            if (Player.NaturalDuration.HasTimeSpan) return Player.Position.TotalSeconds;
            return 0;
        }

        public double GlobalPosition()
        {
            return CurrentPosition() + (IsFallbackClip ? ClipOffset : 0);
        }

        // ---------------------------------------------------------------- 自检辅助（--uicheck）
        public string DescribeControls()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("seek{style=" + (seekBar.Style == null ? "null" : "ok") + " " + DescribeParts(seekBar) + "}");

            sb.Append(" vol{style=" + (volumeBar.Style == null ? "null" : "ok") + " " + DescribeParts(volumeBar) + "}");
            sb.Append(" combo{style=" + (speedBox.Style == null ? "null" : "ok") + "}");
            sb.Append(" scroll{");
            if (infoScroll == null) sb.Append("no-scrollviewer");
            else if (infoScroll.Template == null) sb.Append("no-template");
            else
            {
                var sbv = infoScroll.Template.FindName("PART_VerticalScrollBar", infoScroll) as ScrollBar;
                if (sbv == null) sb.Append("no-scrollbar");
                else
                {
                    sb.Append("w=" + sbv.ActualWidth.ToString("0.#", CultureInfo.InvariantCulture));
                    sb.Append(" visible=" + (sbv.IsVisible ? "1" : "0"));
                    sb.Append(" thumbH=" + ThumbOf(sbv));
                    sb.Append(" " + DescribeParts(sbv));
                }
            }
            sb.Append("}");
            sb.Append(" mute{on=" + (muted ? "1" : "0")
                      + " enabled=" + (muteBtn.IsEnabled ? "1" : "0")
                      + " slash=" + (muteSlash.Visibility == Visibility.Visible ? "1" : "0") + "}");
            sb.Append(" info{rows=" + ValueTexts.Count
                      + " wide=2"
                      + " h=" + infoScroll.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture)
                      + " scrollableH=" + infoScroll.ScrollableHeight.ToString("0.#", CultureInfo.InvariantCulture)
                      + " " + cache.Describe() + "}");
            return sb.ToString();
        }

        /// <summary>是否还有一次定位没落定（进度条钉住期间为 true）。自检用来等定位完成。</summary>
        public bool IsSeekPending { get { return pendingSeekTarget >= 0; } }

        public bool PlayerMuted { get { return Player.IsMuted; } }

        public string MuteState()
        {
            return "muted=" + (muted ? "1" : "0")
                 + " slash=" + (muteSlash.Visibility == Visibility.Visible ? "1" : "0")
                 + " player=" + (Player.IsMuted ? "1" : "0")
                 + " tooltip=" + (muteBtn.ToolTip as string);
        }

        public double InfoScrollableHeight { get { return infoScroll.ScrollableHeight; } }

        /// <summary>诊断：缓存带在图上的真实位置。</summary>
        public string CacheBandState() { return "band{removed}"; }

        public string CacheDesc() { return cache.Describe() + " diag{frac=" + lastFrac.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + " dur=" + lastDur.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " shown=" + lastShown.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " len=" + lastLen + "}"; }

        static string ThumbOf(ScrollBar sb)
        {
            if (sb.Template == null) return "-";
            var tr = sb.Template.FindName("PART_Track", sb) as Track;
            if (tr == null || tr.Thumb == null) return "-";
            return tr.Thumb.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture)
                 + "x" + tr.Thumb.ActualWidth.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static string DescribeParts(Control c)
        {
            if (c.Template == null) return "no-template";
            var tr = c.Template.FindName("PART_Track", c) as Track;
            if (tr == null) return "no-PART_Track";
            return "track{thumb=" + (tr.Thumb != null ? "ok" : "MISSING")
                 + " dec=" + (tr.DecreaseRepeatButton != null ? "ok" : "MISSING")
                 + " inc=" + (tr.IncreaseRepeatButton != null ? "ok" : "MISSING")
                 + " orient=" + tr.Orientation + "}";
        }

        /// <summary>模拟一次鼠标拖动收尾：写 Value 再 CommitSeek，走与真实拖拽相同的代码路径。</summary>
        public void DebugSeekFraction(double f)
        {
            seekBar.Value = seekBar.Minimum + (seekBar.Maximum - seekBar.Minimum) * f;
            CommitSeek();
        }

        /// <summary>诊断：进度条在屏幕上的物理像素矩形（点击注入脚本用）。</summary>
        /// <summary>测试用：模拟一次真实点击（按下 → 轨道写值 → 松开），走和鼠标一样的代码路径。</summary>
        public void DebugClickFraction(double f)
        {
            // 完全照抄真实点击的顺序（用户日志里的样子）：
            // Slider 的类处理器直接把 Value 改掉并把 PreviewMouseDown 标成 Handled，
            // 我们收不到 mouse_down；过一会儿才收到 PreviewMouseUp。
            L("sim_click_down_missing value=" + seekBar.Value.ToString("0.0", CultureInfo.InvariantCulture));
            double lo = seekBar.Minimum, span = seekBar.Maximum - seekBar.Minimum;
            seekBar.SetCurrentValue(RangeBase.ValueProperty, lo + span * f);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            up.RoutedEvent = UIElement.PreviewMouseUpEvent;
            seekBar.RaiseEvent(up);
        }

        /// <summary>测试用：点在进度条上但值没变（以前会误触发一次定位，把播放器拽回旧刻度）。</summary>
        public void DebugClickBlank()
        {
            double keep = seekBar.Value;
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            down.RoutedEvent = UIElement.PreviewMouseDownEvent;
            seekBar.RaiseEvent(down);
            // Slider 自己的 IsMoveToPointEnabled 会拿真实光标位置改写值，这里还原成「没点动」
            seekBar.SetCurrentValue(RangeBase.ValueProperty, keep);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
            up.RoutedEvent = UIElement.PreviewMouseUpEvent;
            seekBar.RaiseEvent(up);
        }

        /// <summary>逐点命中测试：进度条 20px 高，看哪些 y 真的能摸到轨道。</summary>
        public string HitTestReport()
        {
            var sb = new System.Text.StringBuilder();
            double w = seekBar.ActualWidth;
            double h = seekBar.ActualHeight;
            double[] ys = { 1, 4, 8, 10, 13, 16, 18 };
            double[] fs = { 0.1, 0.5, 0.9 };
            foreach (double y in ys)
            {
                sb.Append("hit y=" + y.ToString("0", CultureInfo.InvariantCulture) + " h=" + h.ToString("0", CultureInfo.InvariantCulture));
                foreach (double f in fs)
                {
                    var pt = new Point(6.5 + f * (w - 13), y);
                    var el = seekBar.InputHitTest(pt);
                    sb.Append(" f" + f.ToString("0.0", CultureInfo.InvariantCulture) + "=" + DescribeHit(el));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string DescribeHit(object el)
        {
            var d = el as DependencyObject;
            if (d == null) return "null";
            var fe = d as FrameworkElement;
            string nm = fe != null && !string.IsNullOrEmpty(fe.Name) ? "#" + fe.Name : "";
            return d.GetType().Name + nm;
        }

        public string BarRect()
        {
            try
            {
                var p = seekBar.PointToScreen(new Point(0, 0));
                return Side + "_bar x=" + (int)p.X + " y=" + (int)p.Y +
                       " w=" + (int)seekBar.ActualWidth + " h=" + (int)seekBar.ActualHeight;
            }
            catch (Exception ex) { return Side + "_bar error=" + ex.Message; }
        }

        public double SeekFraction()
        {
            double span = seekBar.Maximum - seekBar.Minimum;
            return span > 0 ? (seekBar.Value - seekBar.Minimum) / span : 0;
        }

        public string TimeText() { return timeText.Text; }

        public void SeekRelative(double seconds)
        {
            if (!HasMedia) return;
            SeekTo(GlobalPosition() + seconds);
        }

        public void SeekTo(double seconds)
        {
            if (!HasMedia) return;
            double dur = Duration();
            double target = Math.Max(0, Math.Min(dur > 0 ? dur : seconds, seconds));
            if (IsFallbackClip)
            {
                double local = target - ClipOffset;
                double clipDur = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 60;
                if (local < 0 || local > clipDur - 0.3)
                {
                    PendingSeek(target, true);
                    Async.Fire(() => FallbackFrom(target));
                    return;
                }
            }
            L("seekto target=" + target.ToString("0.0", CultureInfo.InvariantCulture) + " pos=" + GlobalPosition().ToString("0.0", CultureInfo.InvariantCulture));
            PendingSeek(target, true);   // 先记住「下发前播放头在哪」，再下发
            AttemptSeek(target);
            if (host.Linked && !host.Mirroring) host.MirrorSeek(this, target);
        }

        async Task FallbackFrom(double target)
        {
            IsFallbackClip = false;
            ClipOffset = 0;
            double keep = target;
            transcodeCts = new CancellationTokenSource();
            var token = transcodeCts.Token;
            double window = 60;
            SetStatus(string.Format("正在转码片段（{0} 起 {1:0} 秒）…", FormatClock(keep), window), true, true);
            string src = CurrentPath;
            var progress = new Progress<double>(sec => { progressBar.Value = Math.Min(100, sec / window * 100); });
            string clip = await Task.Run(() =>
            {
                double last = 0;
                return FfmpegHelper.TranscodeClip(src, keep, window, sec =>
                {
                    if (sec - last > 0.5) { last = sec; ((IProgress<double>)progress).Report(sec); }
                }, token);
            });
            if (clip == null) { SetStatus("转码失败", false); return; }
            IsFallbackClip = true;
            ClipOffset = keep;
            SetStatus(string.Format("片段 {0} – {1}", FormatClock(keep), FormatClock(keep + window)), false);
            PlaySource(clip);
        }

        void L(string msg)
        {
            if (!SeekLogOn) return;
            try
            {
                lock (seekLogGate)
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "seeklog.txt"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + Side + " " + msg + "\r\n");
                }
            }
            catch { }
        }

        void CommitSeek()
        {
            if (!HasMedia) return;
            double dur = Duration();
            if (dur <= 0) return;
            double target = seekBar.Value / 1000.0 * dur;
            // 基准是「进度条现在显示的刻度」，不是播放器报的位置：
            // 点在拇指上、或点在没接通轨道的空白处时值根本没变，这时再下发一次定位
            // 会把播放器拽回进度条上的旧刻度（就是用户说的「点了反而回到原刻度」）。
            double shown = pendingSeekTarget >= 0 ? pendingSeekTarget : (shownPos >= 0 ? shownPos : GlobalPosition());
            L("commit value=" + seekBar.Value.ToString("0.0", CultureInfo.InvariantCulture) + " target=" + target.ToString("0.0", CultureInfo.InvariantCulture) + " shown=" + shown.ToString("0.0", CultureInfo.InvariantCulture) + " dur=" + dur.ToString("0.0", CultureInfo.InvariantCulture));
            if (Math.Abs(target - shown) < 0.35) { L("commit_skip same_as_display"); return; }
            SeekTo(target);
        }

        void Timer_Tick(object sender, EventArgs e)
        {
            if (!HasMedia) return;
            double dur = Duration();
            double pos = GlobalPosition();

            // 在进度条外面松手时 Slider 收不到 MouseUp，这里兜一下，免得拇指被卡住
            if (draggingSeek && Mouse.LeftButton == MouseButtonState.Released)
            {
                draggingSeek = false;
                CommitSeek();
            }

            // ---- 定位中：进度条永远以用户点的目标为准，画面随后再跟上 ----
            bool pinned = pendingSeekTarget >= 0;
            if (pinned)
            {
                double target = pendingSeekTarget;
                double elapsed = (DateTime.UtcNow - pendingSeekAt).TotalSeconds;
                bool atTarget = ReachedTarget(pos, target);
                if (atTarget) pendingStable++; else pendingStable = 0;

                if (pendingStable >= 3)
                {
                    // 连续 5 拍（≈1 秒）都停在目标上，才算真的到位
                    pendingSeekTarget = -1;
                    pinned = false;
                    SetStatusIfChanged("", false);
                }
                else if (elapsed > 45)
                {
                    pendingSeekTarget = -1;
                    pinned = false;
                    SetStatusIfChanged("定位 " + FormatClock(target) + " 太慢，已放弃（可换个位置或先暂停一会儿）", false, false, true);
                }
                else
                {
                    // 播放器经常「先返回目标、再悄悄退回旧位置」，退了就再下发一次
                    if (!atTarget && elapsed > 0.9 && pendingRetries < 8 &&
                        (DateTime.UtcNow - lastReassertAt).TotalSeconds > 0.7)
                    {
                        ReassertSeek(target);
                    }
                    SetStatusIfChanged(PendingSeekStatus(target), false);
                }
            }
            else
            {
                // 显示位置单向前进：只有用户点进度条才能让它往回跳。
                // 播放器说自己在旧位置（把定位撤销了），一概不认——先记下来，必要时再下发一次。
                if (shownPos < 0) shownPos = pos;
                if (pos > shownPos)
                {
                    shownPos = pos;
                    displayDrift = 0;
                }
                else if (pos < shownPos - 0.4)
                {
                    displayDrift++;
                    if (seekTarget >= 0 && pendingRetries < 8 &&
                        (DateTime.UtcNow - seekReqAt).TotalSeconds < 12 &&
                        (DateTime.UtcNow - lastReassertAt).TotalSeconds > 1.0 &&
                        shownPos - pos > 1.0)
                    {
                        pendingSeekTarget = seekTarget;
                        pendingSeekAt = DateTime.UtcNow;
                        pendingStable = 0;
                        ReassertSeek(seekTarget);
                        pinned = true;
                        SetStatusIfChanged(PendingSeekStatus(seekTarget), false);
                    }
                    else if (displayDrift > 150)
                    {
                        // 连续 30 秒都追不上（片子真的播不动了）才认输，免得永远卡住
                        shownPos = pos;
                        displayDrift = 0;
                    }
                }
                else
                {
                    displayDrift = 0;
                }
            }

            double shown = pinned ? pendingSeekTarget : shownPos;
            if (shown < 0) shown = pos;
            if (pinned) shownPos = shown;
            timeText.Text = FormatClock(shown) + " / " + FormatClock(dur);
            UpdateCacheFeed(dur, shown);
            bool userTouching = draggingSeek || (DateTime.UtcNow - lastUserTouch).TotalMilliseconds < 600;
            if (!userTouching && dur > 0)
            {
                double v = Math.Max(0, Math.Min(1000, shown / dur * 1000));
                if (Math.Abs(seekBar.Value - v) > 0.01)
                {
                    seekValueGuard = true;
                    seekBar.Value = v;
                    seekValueGuard = false;
                }
            }


            if (SeekLogOn && (pinned || DateTime.UtcNow.Ticks / 10000000.0 < logTickUntil))
                L("tick shown=" + shown.ToString("0.0", CultureInfo.InvariantCulture) + " pos=" + pos.ToString("0.0", CultureInfo.InvariantCulture) +
                  " pinned=" + (pinned ? 1 : 0) + " bar=" + SeekFraction().ToString("0.000", CultureInfo.InvariantCulture) +
                  " dragging=" + (draggingSeek ? 1 : 0) + " retries=" + pendingRetries);

            _isPlayingFlag = Player.CanPause && pos > _lastPosition.TotalSeconds + 0.02;
            _lastPosition = TimeSpan.FromSeconds(pos);
        }

        /// <summary>真正把位置下发给播放器（转码片段时间轴要换算）。</summary>
        void AttemptSeek(double target)
        {
            L("attempt target=" + target.ToString("0.0", CultureInfo.InvariantCulture) + " clip=" + (IsFallbackClip ? 1 : 0));
            double local = target;
            if (IsFallbackClip) local = target - ClipOffset;
            if (local < 0) local = 0;
            try { Player.Position = TimeSpan.FromSeconds(local); } catch { }
        }

        /// <summary>播放器把位置退回去了，重新下发一次。</summary>
        void ReassertSeek(double target)
        {
            L("reassert target=" + target.ToString("0.0", CultureInfo.InvariantCulture) + " pos=" + GlobalPosition().ToString("0.0", CultureInfo.InvariantCulture));
            pendingRetries++;
            lastReassertAt = DateTime.UtcNow;
            AttemptSeek(target);
            double dur = Duration();
            if (dur > 0) cache.WarmFraction(target / dur);
        }

        /// <summary>播放头是不是已经真的到了目标（按跳转方向判断，避免「没动也算到」）。</summary>
        bool ReachedTarget(double pos, double target)
        {
            // 播放器落到目标后往往立刻继续播放，位置会往前跑；若只用固定 ±0.5 的窗口，
            // 定位完成的那一瞬赶不上采样（每 200ms 一拍）就一直判「没到位」，于是不停重发，
            // 反而把播放按住（表现为播不动）。所以容差随「发出定位后过了多久」放宽，
            // 但封顶，避免把「还没动」的旧位置也当成到位。
            double since = (DateTime.UtcNow - pendingSeekAt).TotalSeconds;
            double tol = 0.5 + (since > 0 ? since * 1.3 : 0);
            if (tol > 3.0) tol = 3.0;

            if (seekDir > 0) return pos >= target - tol;                          // 向后跳：到或越过目标
            if (seekDir < 0) return pos <= target + tol && pos < posBeforeSeek - 0.4;  // 回退：确实退下来了
            return Math.Abs(pos - target) <= tol;
        }

        /// <summary>记住这次定位请求：进度条钉在目标上，直到播放器真的、稳定地到位。</summary>
        void PendingSeek(double target, bool fresh)
        {
            double now = GlobalPosition();
            if (fresh) posBeforeSeek = now;
            if (target - posBeforeSeek > 1) seekDir = 1;
            else if (posBeforeSeek - target > 1) seekDir = -1;
            else seekDir = 0;
            pendingSeekTarget = target;
            pendingSeekAt = DateTime.UtcNow;
            pendingStable = 0;
            seekTarget = target;
            seekReqAt = DateTime.UtcNow;
            if (fresh) { pendingRetries = 0; lastReassertAt = DateTime.UtcNow; }
            shownPos = target;
            displayDrift = 0;
            logTickUntil = DateTime.UtcNow.Ticks / 10000000.0 + 3.0;
            double dur = Duration();
            if (dur > 0) cache.WarmFraction(target / dur);
        }

        /// <summary>诊断：定位状态。</summary>
        public string SeekState()
        {
            return "seek_state{pending=" + (pendingSeekTarget >= 0 ? pendingSeekTarget.ToString("0.0", CultureInfo.InvariantCulture) : "-") +
                   " target=" + (seekTarget >= 0 ? seekTarget.ToString("0.0", CultureInfo.InvariantCulture) : "-") +
                   " pos=" + GlobalPosition().ToString("0.0", CultureInfo.InvariantCulture) +
                   " bar=" + (SeekFraction() * 1000).ToString("0.0", CultureInfo.InvariantCulture) +
                   " stable=" + pendingStable + " retries=" + pendingRetries +
                   " ms=" + (int)(DateTime.UtcNow - pendingSeekAt).TotalMilliseconds + "}";
        }

        /// <summary>把播放头告诉预读线程（灰色缓存区间已按用户要求去掉，预读仍然保留以加快网络定位）。</summary>
        /// <summary>定位等待时给用户看「还要等多久」：把已经预读到的比例算出来。</summary>
        string PendingSeekStatus(double target)
        {
            string head = "正在定位到 " + FormatClock(target) + "…";
            long from, to, len;
            if (!cache.TryGet(out from, out to, out len) || len <= 0) return head;
            double dur = Duration();
            if (dur <= 0) return head;
            long want = (long)(Math.Min(target, dur) / dur * len);
            if (want <= from || want <= to) return head + "（缓冲中）";
            int pct = (int)((to - from) * 100.0 / (want - from));
            if (pct < 0) pct = 0;
            if (pct > 99) pct = 99;
            return head + "（已缓冲 " + pct + "%）";
        }

        // 灰色缓存区间已按用户要求去掉；预读线程仍然要跟着播放头走，
        // 这样网络文件跳到哪都有现成的数据。
        void UpdateCacheFeed(double dur, double shown)
        {
            if (dur <= 0 || double.IsNaN(shown) || shown <= 0) return;
            long len = cache.Length;
            if (len <= 0) return;
            double frac = shown / dur;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            lastFrac = frac; lastDur = dur; lastShown = shown; lastLen = len;
            cache.Playhead((long)(frac * len));
        }

        /// <summary>静音/恢复。</summary>
        void ApplyMute(bool mute)
        {
            muted = mute;
            try { Player.IsMuted = mute; } catch { }
            if (muteSlash != null) muteSlash.Visibility = mute ? Visibility.Visible : Visibility.Collapsed;
            if (muteBtn != null) muteBtn.ToolTip = mute ? "已静音（点一下恢复声音）" : "静音（点一下关掉声音）";
            if (host != null && host.Linked && !host.Mirroring) host.MirrorMute(this, mute);
        }

        /// <summary>音量百分比（0..100），用于配置持久化。</summary>
        public double VolumePercent
        {
            get { return volumeBar == null ? 100.0 : volumeBar.Value; }
            set
            {
                if (volumeBar == null) return;
                double v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                volumeBar.Value = v;
                try { Player.Volume = v / 100.0; } catch { }
            }
        }

        public bool IsMuted { get { return muted; } }

        public void SetMuted(bool mute)
        {
            if (muteBtn == null) return;
            if (muteBtn.IsChecked == mute) ApplyMute(mute);
            else muteBtn.IsChecked = mute;      // 触发 Checked/Unchecked → ApplyMute
        }

        public void ToggleMute() { SetMuted(!muted); }

        public static string FormatClock(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        // ---------------------------------------------------------------- 状态显示
        void SetStatusIfChanged(string text, bool busy, bool withProgress = false, bool error = false)
        {
            string t = text ?? "";
            if (statusText.Text == t && progressBar.Visibility == (withProgress ? Visibility.Visible : Visibility.Collapsed))
                return;
            SetStatus(t, busy, withProgress, error);
        }

        void SetStatus(string text, bool busy, bool withProgress = false, bool error = false)
        {
            statusText.Text = text ?? "";
            statusText.Foreground = error ? Theme.Red : Theme.Muted2;
            statusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            progressBar.Visibility = withProgress ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = 0;
        }

        // ---------------------------------------------------------------- 拖放
        void Stage_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                stage.BorderBrush = Theme.Accent;
                stage.BorderThickness = new Thickness(2);
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        async void Stage_Drop(object sender, DragEventArgs e)
        {
            stage.BorderBrush = Theme.Line;
            stage.BorderThickness = new Thickness(0);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (files.Length >= 2)
            {
                await host.LoadBoth(files[0], files[1]);
                return;
            }
            string path = files[0];
            if (Directory.Exists(path))
            {
                string first = null, second = null;
                try
                {
                    foreach (var f in Directory.GetFiles(path))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".avi" ||
                            ext == ".webm" || ext == ".ts" || ext == ".m2ts" || ext == ".wmv" || ext == ".mpg" || ext == ".mpeg")
                        {
                            if (first == null) first = f;
                            else if (second == null) { second = f; break; }
                        }
                    }
                }
                catch { }
                if (first == null) return;
                if (second != null) await host.LoadBoth(first, second);
                else await Load(first);
                return;
            }
            await Load(path);
        }

        // ---------------------------------------------------------------- 文件操作
        async Task PickFile()
        {
            string start = (Info != null && !string.IsNullOrEmpty(Info.Dir)) ? Info.Dir : AppSettings.LastDir;
            string picked = FilePickerWindow.PickFile(Window.GetWindow(Root), start, "选择视频");
            if (!string.IsNullOrEmpty(picked))
            {
                await Load(picked);
            }
        }

        public void StopPlayback(bool notify)
        {
            try
            {
                if (transcodeCts != null) transcodeCts.Cancel();
                Player.Stop();
                Player.Close();
                Player.Source = null;
            }
            catch { }
            SetPlayIcon(false);
            cache.Stop();
            pendingSeekTarget = -1;
            shownPos = -1;
            seekTarget = -1;
            pendingRetries = 0;
            seekBar.Value = 0;
            seekBar.IsEnabled = false;
            volumeBar.IsEnabled = false;
            muteBtn.IsEnabled = false;
            timeText.Text = "00:00 / 00:00";
            _isPlayingFlag = false;
            if (HasMedia)
            {
                hintText.Text = "已停止（文件占用已释放）。点「重载」或重新拖入即可继续。";
                hintText.Visibility = Visibility.Visible;
            }
            if (notify) SetStatus("已停止并释放文件占用", false);
        }

        async Task Rename()
        {
            if (!HasMedia) return;
            string dir = Path.GetDirectoryName(CurrentPath);
            string oldName = Path.GetFileName(CurrentPath);
            string input = Prompt.Show(Window.GetWindow(Root), "重命名", "新文件名：", oldName);
            if (string.IsNullOrWhiteSpace(input) || input == oldName) return;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (input.IndexOf(c) >= 0)
                {
                    MessageBox.Show(Window.GetWindow(Root), "文件名不能包含 \\ / : * ? \" < > | 等字符", "重命名");
                    return;
                }
            }
            string target = Path.Combine(dir, input);
            if (File.Exists(target)) { MessageBox.Show(Window.GetWindow(Root), "目标文件名已存在", "重命名"); return; }
            try
            {
                bool wasPlaying = IsPlaying();
                double pos = GlobalPosition();
                Player.Stop(); Player.Close(); Player.Source = null;
                File.Move(CurrentPath, target);
                await Load(target, false);
                if (pos > 0) SeekTo(pos);
                if (wasPlaying) Play();
                host.SetTopStatus("已重命名为 " + input);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(Root), "重命名失败：" + ex.Message, "重命名");
            }
        }

        async Task Move()
        {
            if (!HasMedia) return;
            string start = (Info != null && !string.IsNullOrEmpty(Info.Dir) && Directory.Exists(Info.Dir))
                ? Info.Dir
                : AppSettings.LastDir;
            string folder = FilePickerWindow.PickFolder(Window.GetWindow(Root), start, "选择要移动到的文件夹");
            if (string.IsNullOrEmpty(folder)) return;
            folder = PathUtil.Normalize(folder);
            string dest = Path.Combine(folder, Path.GetFileName(CurrentPath));
            if (string.Equals(Path.GetDirectoryName(CurrentPath), folder, StringComparison.OrdinalIgnoreCase))
            {
                host.SetTopStatus("文件已在该文件夹中");
                return;
            }
            if (File.Exists(PathUtil.Safe(dest)))
            {
                string stem = Path.GetFileNameWithoutExtension(CurrentPath);
                string ext = Path.GetExtension(CurrentPath);
                for (int i = 1; i < 100; i++)
                {
                    string cand = Path.Combine(folder, stem + " (" + i + ")" + ext);
                    if (!File.Exists(PathUtil.Safe(cand))) { dest = cand; break; }
                }
            }
            try
            {
                bool wasPlaying = IsPlaying();
                double pos = GlobalPosition();
                Player.Stop(); Player.Close(); Player.Source = null;
                File.Move(PathUtil.Safe(CurrentPath), PathUtil.Safe(dest));
                await Load(dest, false);
                if (pos > 0) SeekTo(pos);
                if (wasPlaying) Play();
                host.SetTopStatus("已移动到 " + folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(Root), "移动失败：" + ex.Message, "移动");
            }
        }

        void Delete()
        {
            if (!HasMedia) return;
            var res = MessageBox.Show(Window.GetWindow(Root),
                "确定要删除吗？\n\n" + CurrentPath + "\n\n（本地文件会移到回收站；网络文件将永久删除）",
                "删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            string path = CurrentPath;
            Player.Stop(); Player.Close(); Player.Source = null;
            try
            {
                bool network = path.StartsWith(@"\\");
                if (network) File.Delete(path);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                host.SetTopStatus("已删除：" + Path.GetFileName(path));
                Info = null;
                CurrentPath = null;
                UpdateInfoRows();
                nameText.Text = "未选择视频";
                nameText.Foreground = Theme.Muted;
                hintText.Text = "把视频拖到这里，或点右上角「选择视频…」";
                hintText.Visibility = Visibility.Visible;
                host.AfterPaneLoad(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(Root), "删除失败：" + ex.Message, "删除");
            }
        }

        void RevealFile()
        {
            if (!HasMedia) return;
            try
            {
                // 走 shell32 的 SHOpenFolderAndSelectItems：不 spawn explorer.exe
                //（某些机器上 explorer.exe /select 会以 0xc0000142 直接崩掉）
                if (!ShellReveal.SelectInExplorer(CurrentPath))
                    host.SetTopStatus("无法在资源管理器里定位：" + Path.GetFileName(CurrentPath));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(Root), "无法打开资源管理器：" + ex.Message, "打开位置");
            }
        }
    }
}
