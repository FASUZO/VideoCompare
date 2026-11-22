using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoCompare
{
    /// <summary>
    /// 路径工具：把用户/资源管理器复制来的各种写法统一成 Windows 能用的路径。
    /// 同时负责长路径（&gt; 260）的 \\?\ 前缀处理。
    /// </summary>
    internal static class PathUtil
    {
        public static readonly string[] VideoExts = new string[]
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".mts", ".wmv",
            ".mpg", ".mpeg", ".flv", ".3gp", ".ogv", ".m4v", ".rmvb", ".rm", ".asf",
            ".vob", ".f4v", ".divx", ".mxf", ".m2v", ".dat",
        };

        public static bool IsVideoName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            for (int i = 0; i < VideoExts.Length; i++)
            {
                if (VideoExts[i] == ext) return true;
            }
            return false;
        }

        /// <summary>
        /// 归一化：去引号/空白、file:/// → 本地路径、正斜杠 → 反斜杠、
        /// 单反斜杠开头补成 UNC 两反斜杠、合并重复分隔符、相对路径转绝对路径。
        /// </summary>
        public static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace("\r", "").Replace("\n", "").Trim();
            if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"')
            {
                p = p.Substring(1, p.Length - 2).Trim();
            }
            if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { p = new Uri(p).LocalPath; } catch { }
            }

            p = p.Replace('/', '\\');

            bool longPrefix = p.StartsWith("\\\\?\\", StringComparison.Ordinal);
            if (!longPrefix)
            {
                if (p.StartsWith("\\", StringComparison.Ordinal))
                {
                    // 单反斜杠 / 多个反斜杠开头一律当成 UNC：\\server\share
                    int i = 0;
                    while (i < p.Length && p[i] == '\\') i++;
                    string rest = p.Substring(i);
                    p = rest.Length > 0 ? "\\\\" + rest : "\\\\";
                }
                // 合并中间重复分隔符，但保留 UNC 前缀的两个
                string head = p.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\" : "";
                string tail = p.Substring(head.Length);
                while (tail.IndexOf("\\\\", StringComparison.Ordinal) >= 0)
                {
                    tail = tail.Replace("\\\\", "\\");
                }
                p = head + tail;
            }

            try
            {
                if (!Path.IsPathRooted(p) && p.Length < 240) p = Path.GetFullPath(p);
            }
            catch { }

            if (p.Length == 2 && p[1] == ':') p = p + "\\";
            return p;
        }

        /// <summary>长路径加 \\?\ 前缀；普通路径顺带去掉多余尾分隔符。</summary>
        public static string Safe(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("\\\\?\\", StringComparison.Ordinal)) return p;
            string q = p;
            if (q.Length > 3) q = q.TrimEnd('\\');
            if (q.Length < 240) return q;
            if (q.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + q.Substring(2);
            return "\\\\?\\" + q;
        }

        public static bool IsDir(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return Directory.Exists(Safe(p)); }
            catch { return false; }
        }

        public static bool IsFile(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return File.Exists(Safe(p)); }
            catch { return false; }
        }

        /// <summary>服务器根 \\server（没有共享名）：Directory.* 不支持，需要走 NetShareEnum。</summary>
        public static bool IsServerRoot(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            string q = p.TrimEnd('\\');
            if (!q.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            string rest = q.Substring(2);
            return rest.Length > 0 && rest.IndexOf('\\') < 0;
        }

        public static string ServerOf(string p)
        {
            string q = (p ?? "").TrimEnd('\\');
            if (!q.StartsWith("\\\\", StringComparison.Ordinal)) return null;
            string rest = q.Substring(2);
            int slash = rest.IndexOf('\\');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        public static string ParentOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            string q = p.Length > 3 ? p.TrimEnd('\\') : p;
            if (IsServerRoot(q))
            {
                // \\server → 盘符级「此电脑」，交给调用方处理
                return "";
            }
            try
            {
                string parent = Path.GetDirectoryName(q);
                if (parent == null) return "";
                if (parent.Length == 2 && parent[1] == ':') return parent + "\\";
                return parent;
            }
            catch { return null; }
        }

        /// <summary>列出子目录；失败时返回空数组并给出错误文本。</summary>
        public static string[] Dirs(string path, out string error)
        {
            error = null;
            try { return Directory.GetDirectories(Safe(path)); }
            catch (Exception ex) { error = Friendly(ex); return new string[0]; }
        }

        public static string[] Files(string path, out string error)
        {
            error = null;
            try { return Directory.GetFiles(Safe(path)); }
            catch (Exception ex) { error = Friendly(ex); return new string[0]; }
        }

        public static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "没有访问权限";
            if (ex is DirectoryNotFoundException) return "路径不存在";
            if (ex is FileNotFoundException) return "文件不存在";
            if (ex is PathTooLongException) return "路径太长";
            if (ex is IOException) return "设备或网络不可用（" + ex.Message.Trim() + "）";
            return ex.GetType().Name + "：" + ex.Message.Trim();
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] units = new string[] { "KB", "MB", "GB", "TB" };
            int u = -1;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(u == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }
    }

    /// <summary>枚举 \\server 下的共享（Directory.* 不支持服务器根）。</summary>
    internal static class ShareEnum
    {
        const int STYPE_DISKTREE = 0;
        const uint STYPE_SPECIAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        struct SHARE_INFO_1
        {
            public IntPtr netname;
            public uint type;
            public IntPtr remark;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetShareEnum(string servername, int level, out IntPtr bufptr,
            int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);

        [DllImport("netapi32.dll")]
        static extern int NetApiBufferFree(IntPtr buffer);

        /// <summary>返回 \\server\share 形式的磁盘共享列表（隐藏共享如 C$ / IPC$ 会被过滤）。</summary>
        public static string[] DiskShares(string server, out string error)
        {
            error = null;
            var list = new List<string>();
            IntPtr buf = IntPtr.Zero;
            int read = 0, total = 0, resume = 0;
            try
            {
                int rc = NetShareEnum(server, 1, out buf, -1, out read, out total, ref resume);
                if (rc != 0)
                {
                    error = "无法枚举共享（错误码 " + rc + "）";
                    return new string[0];
                }
                int size = Marshal.SizeOf(typeof(SHARE_INFO_1));
                IntPtr cur = buf;
                for (int i = 0; i < read; i++)
                {
                    SHARE_INFO_1 info = (SHARE_INFO_1)Marshal.PtrToStructure(cur, typeof(SHARE_INFO_1));
                    cur = new IntPtr(cur.ToInt64() + size);
                    if (info.type != STYPE_DISKTREE) continue;
                    if ((info.type & STYPE_SPECIAL) != 0) continue;
                    string name = Marshal.PtrToStringUni(info.netname);
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add("\\\\" + server + "\\" + name);
                }
            }
            catch (Exception ex)
            {
                error = PathUtil.Friendly(ex);
            }
            finally
            {
                if (buf != IntPtr.Zero) { try { NetApiBufferFree(buf); } catch { } }
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }
    }

    /// <summary>
    /// 自绘「打开文件 / 选择文件夹」窗口。
    /// 用它替掉系统 OpenFileDialog / FolderBrowserDialog 的原因：
    /// 系统对话框的地址栏粘贴 UNC 路径后按回车会用 ShellExecute 打开文件（变成系统播放器），
    /// 也会对 //nas/... 这类写法弹「你不能使用该程序打开此位置」。自己实现就能把
    /// 输入一律当成路径解析，命中文件就直接返回给调用方。
    /// </summary>
    internal sealed class FilePickerWindow : Window
    {
        // 非模态显示时需要一个强引用，否则窗口会被 GC 回收并关掉
        internal static FilePickerWindow LastShown;

        public enum Mode { File, Folder }

        readonly Mode mode;
        readonly TextBox pathBox;
        readonly ListBox places;
        readonly ListBox list;
        readonly TextBlock status;
        readonly Button okBtn;
        readonly CheckBox showAll;
        string currentDir;
        string result;
        bool accepted;

        public string Result { get { return result; } }
        public string CurrentDir { get { return currentDir; } }
        public int ItemCount { get { return list.Items.Count; } }
        public string StatusText { get { return status.Text; } }

        public FilePickerWindow(Mode mode, string startPath, string title)
        {
            this.mode = mode;
            Title = title ?? (mode == Mode.Folder ? "选择文件夹" : "选择视频");
            Width = 860;
            Height = 580;
            MinWidth = 620;
            MinHeight = 420;
            Background = Theme.Panel;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Styles.Apply(this);

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 路径行 ----
            var pathRow = new Grid();
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathBox = new TextBox
            {
                Padding = new Thickness(9, 6, 9, 6),
                Background = Theme.Panel2,
                Foreground = Theme.Text,
                BorderBrush = Theme.Line,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "可以直接粘贴完整路径（支持 \\\\server\\share\\a.mp4、//server/share/a.mp4、C:/a/b.mp4、file:///…），回车即打开",
            };
            pathBox.KeyDown += PathBox_KeyDown;
            Grid.SetColumn(pathBox, 0);
            pathRow.Children.Add(pathBox);

            var goBtn = Theme.Btn("转到");
            goBtn.Margin = new Thickness(8, 0, 0, 0);
            goBtn.Click += delegate { NavigateTo(pathBox.Text); };
            Grid.SetColumn(goBtn, 1);
            pathRow.Children.Add(goBtn);
            Grid.SetRow(pathRow, 0);
            root.Children.Add(pathRow);

            // ---- 主体：左侧常用位置 + 右侧条目列表 ----
            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            places = new ListBox
            {
                Background = Theme.Panel2,
                Foreground = Theme.Text,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(2),
            };
            places.SelectionChanged += delegate
            {
                var item = places.SelectedItem as ListBoxItem;
                if (item == null) return;
                string target = item.Tag as string;
                if (!string.IsNullOrEmpty(target)) NavigateTo(target);
            };
            Grid.SetColumn(places, 0);
            body.Children.Add(places);

            list = new ListBox
            {
                Background = Theme.Panel2,
                Foreground = Theme.Text,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(2),
            };
            list.MouseDoubleClick += delegate { OpenSelected(); };
            list.KeyDown += List_KeyDown;
            list.SelectionChanged += delegate { UpdateOkState(); };
            Grid.SetColumn(list, 1);
            body.Children.Add(list);
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // ---- 状态行 ----
            status = Theme.Label("", 12, Theme.Muted2);
            status.Margin = new Thickness(2, 8, 0, 0);
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(status, 2);
            root.Children.Add(status);

            // ---- 按钮行 ----
            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            showAll = new CheckBox
            {
                Content = "显示所有文件类型",
                Foreground = Theme.Muted,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            showAll.Checked += delegate { if (currentDir != null) Fill(currentDir, null); };
            showAll.Unchecked += delegate { if (currentDir != null) Fill(currentDir, null); };
            Grid.SetColumn(showAll, 0);
            bottom.Children.Add(showAll);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var upBtn = Theme.Btn("上级");
            upBtn.Click += delegate { GoUp(); };
            buttons.Children.Add(upBtn);
            var refreshBtn = Theme.Btn("刷新");
            refreshBtn.Click += delegate { if (currentDir != null) Fill(currentDir, null); };
            buttons.Children.Add(refreshBtn);
            var cancelBtn = Theme.Btn("取消");
            cancelBtn.Click += delegate { Close(); };
            buttons.Children.Add(cancelBtn);
            okBtn = Theme.Btn(mode == Mode.Folder ? "选择此文件夹" : "打开", true);
            okBtn.Click += delegate { OpenSelected(); };
            buttons.Children.Add(okBtn);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);
            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            Content = root;
            SourceInitialized += delegate { MainWindow.DarkTitleBar(this); };
            Loaded += delegate
            {
                BuildPlaces();
                string start = string.IsNullOrEmpty(startPath) ? AppSettings.LastDir : startPath;
                if (string.IsNullOrEmpty(start) || !NavigateTo(start)) NavigateTo(FirstDrive());
                list.Focus();
            };
        }

        static string FirstDrive()
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.IsReady) return d.RootDirectory.FullName;
                }
            }
            catch { }
            return "C:\\";
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        void BuildPlaces()
        {
            places.Items.Clear();

            string last = AppSettings.LastDir;
            if (!string.IsNullOrEmpty(last))
            {
                places.Items.Add(PlaceItem("最近使用", Path.GetFileName(last.TrimEnd('\\')) == "" ? last : Path.GetFileName(last.TrimEnd('\\')), last));
            }
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string label = d.Name.TrimEnd('\\');
                    string text = (string.IsNullOrEmpty(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel) + " (" + label + ")";
                    places.Items.Add(PlaceItem(d.DriveType == DriveType.Network ? "网络驱动器" : "磁盘", text, d.RootDirectory.FullName));
                }
                catch { }
            }
            places.Items.Add(PlaceItem("网络", "输入 \\\\服务器\\共享 …", null));
        }

        ListBoxItem PlaceItem(string kind, string text, string target)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock
            {
                Text = kind == "网络" ? "\uE774" : (kind == "磁盘" ? "\uEDA2" : "\uE8B7"),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 18,
                Foreground = Theme.Muted2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sp.Children.Add(icon);
            sp.Children.Add(Theme.Label(text, 12, Theme.Text));
            return new ListBoxItem { Content = sp, Tag = target, ToolTip = target, Padding = new Thickness(6, 5, 6, 5) };
        }

        void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { NavigateTo(pathBox.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            else if (e.Key == Key.Back) { GoUp(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        /// <summary>
        /// 解析一段输入并跳转。命中文件就返回（不交给系统打开），命中目录就列出来。
        /// 返回值：true 表示输入被正确处理（跳转成功或已选中文件）。
        /// </summary>
        internal bool NavigateTo(string input)
        {
            string p = PathUtil.Normalize(input);
            if (string.IsNullOrEmpty(p))
            {
                SetStatus("请输入路径，或从上面的列表里选一个文件", true);
                return false;
            }
            // 去掉多余的尾部分隔符（保留 C:\ 这种根）
            string probe = p.Length > 3 ? p.TrimEnd('\\') : p;

            if (PathUtil.IsFile(probe))
            {
                string dir = PathUtil.ParentOf(probe);
                if (mode == Mode.Folder)
                {
                    Fill(dir, null);
                    SetStatus("这是一个文件，不是文件夹：" + Path.GetFileName(probe), true);
                    return false;
                }
                Accept(probe);
                return true;
            }
            if (PathUtil.IsDir(probe))
            {
                Fill(probe, null);
                return true;
            }
            if (PathUtil.IsServerRoot(probe))
            {
                // \\server：走 NetShareEnum 列共享
                string err;
                string[] shares = ShareEnum.DiskShares(PathUtil.ServerOf(probe), out err);
                currentDir = probe;
                pathBox.Text = probe;
                list.Items.Clear();
                foreach (string s in shares) list.Items.Add(MakeItem(Path.GetFileName(s), s, true, -1, null));
                if (shares.Length == 0) SetStatus(err ?? "这个服务器上没有可访问的共享", true);
                else SetStatus("共享 " + shares.Length + " 个", false);
                UpdateOkState();
                return true;
            }
            if (probe.Length == 0 || SamePath(probe, "此电脑"))
            {
                FillComputer();
                return true;
            }
            SetStatus("路径不存在或无法访问：" + p, true);
            return false;
        }

        void FillComputer()
        {
            currentDir = "";
            pathBox.Text = "";
            list.Items.Clear();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    list.Items.Add(MakeItem(d.Name, d.RootDirectory.FullName, true, -1, null));
                }
            }
            catch { }
            SetStatus("在此选择磁盘，或在上面直接粘贴 \\\\服务器\\共享\\文件.mp4", false);
            UpdateOkState();
        }

        /// <summary>列出目录内容；selectName 用于刷新后保持选中。</summary>
        void Fill(string dir, string selectName)
        {
            if (string.IsNullOrEmpty(dir)) { FillComputer(); return; }
            string err;
            string[] dirs = PathUtil.Dirs(dir, out err);
            if (err != null)
            {
                SetStatus("无法打开 " + dir + "：" + err, true);
                return;
            }
            string[] files = PathUtil.Files(dir, out err);
            if (err != null)
            {
                SetStatus("无法读取 " + dir + "：" + err, true);
                return;
            }

            currentDir = dir;
            pathBox.Text = dir;
            list.Items.Clear();

            List<string> dl = new List<string>(dirs);
            dl.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> fl = new List<string>();
            bool all = showAll != null && showAll.IsChecked == true;
            foreach (string f in files)
            {
                if (all || PathUtil.IsVideoName(f)) fl.Add(f);
            }
            fl.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string d in dl) list.Items.Add(MakeItem(Path.GetFileName(d), d, true, -1, null));

            const int cap = 4000;
            int shown = 0;
            foreach (string f in fl)
            {
                if (shown >= cap) break;
                long size = -1;
                DateTime mod = DateTime.MinValue;
                try
                {
                    var fi = new FileInfo(PathUtil.Safe(f));
                    size = fi.Length;
                    mod = fi.LastWriteTime;
                }
                catch { }
                list.Items.Add(MakeItem(Path.GetFileName(f), f, false, size, mod));
                shown++;
            }

            if (!string.IsNullOrEmpty(selectName))
            {
                foreach (object o in list.Items)
                {
                    var li = o as ListBoxItem;
                    if (li != null && string.Equals(Path.GetFileName(li.Tag as string), selectName, StringComparison.OrdinalIgnoreCase))
                    {
                        list.SelectedItem = li;
                        list.ScrollIntoView(li);
                        break;
                    }
                }
            }

            string msg = "文件夹 " + dl.Count + " · 视频 " + fl.Count;
            if (shown < fl.Count) msg += "（只显示前 " + shown + " 个）";
            if (fl.Count == 0 && dl.Count == 0) msg = "这个文件夹是空的";
            SetStatus(msg, false);
            UpdateOkState();
        }

        ListBoxItem MakeItem(string name, string full, bool isDir, long size, DateTime? modified)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = isDir ? "\uE8B7" : "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Width = 22,
                Foreground = isDir ? Theme.Amber : Theme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                Foreground = Theme.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var right = new TextBlock
            {
                Text = isDir ? "" : PathUtil.FormatSize(size < 0 ? 0 : size),
                FontSize = 12,
                Foreground = Theme.Muted2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            string tip = full;
            if (!isDir && modified.HasValue) tip += "\n修改时间：" + modified.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new ListBoxItem
            {
                Content = grid,
                Tag = full,
                ToolTip = tip,
                Padding = new Thickness(6, 5, 6, 5),
            };
        }

        void SetStatus(string text, bool error)
        {
            status.Text = text ?? "";
            status.Foreground = error ? Theme.Red : Theme.Muted2;
            status.ToolTip = text;
        }

        void UpdateOkState()
        {
            var sel = list.SelectedItem as ListBoxItem;
            string target = sel == null ? null : sel.Tag as string;
            if (mode == Mode.Folder)
            {
                okBtn.IsEnabled = currentDir != null && currentDir.Length > 0;
            }
            else
            {
                okBtn.IsEnabled = target != null && !PathUtil.IsDir(target);
            }
        }

        void GoUp()
        {
            if (string.IsNullOrEmpty(currentDir)) { FillComputer(); return; }
            string parent = PathUtil.ParentOf(currentDir);
            if (parent == null) return;
            if (parent.Length == 0) { FillComputer(); return; }
            Fill(parent, Path.GetFileName(currentDir.TrimEnd('\\')));
        }

        /// <summary>回车/双击/「打开」按钮：目录则进去，文件则确认。</summary>
        void OpenSelected()
        {
            var sel = list.SelectedItem as ListBoxItem;
            string target = sel == null ? null : sel.Tag as string;
            if (target == null)
            {
                // 没选条目：试试路径框里手输的内容
                if (!string.IsNullOrEmpty(pathBox.Text)) NavigateTo(pathBox.Text);
                return;
            }
            if (PathUtil.IsDir(target)) { Fill(target, null); return; }
            if (mode == Mode.Folder)
            {
                // 文件夹模式下，「打开」= 选中当前文件夹
                Accept(currentDir);
                return;
            }
            Accept(target);
        }

        void Accept(string path)
        {
            if (mode == Mode.Folder)
            {
                string dir = path;
                if (PathUtil.IsFile(dir)) dir = PathUtil.ParentOf(dir);
                if (string.IsNullOrEmpty(dir)) return;
                result = dir;
                try { AppSettings.LastDir = dir; AppSettings.Save(); } catch { }
            }
            else
            {
                result = path;
                try { AppSettings.LastDir = PathUtil.ParentOf(path); AppSettings.Save(); } catch { }
            }
            accepted = true;
            if (IsLoaded) Close();
        }

        public bool Accepted { get { return accepted; } }

        /// <summary>当前列表里的条目名（目录带结尾反斜杠）；max &lt;= 0 表示最多 5000 条。</summary>
        internal string[] Snapshot(int max)
        {
            var names = new List<string>();
            int cap = max > 0 ? max : 5000;
            foreach (object o in list.Items)
            {
                var li = o as ListBoxItem;
                if (li == null) continue;
                string p = li.Tag as string;
                if (p == null) continue;
                string name = Path.GetFileName(p.TrimEnd('\\'));
                names.Add(PathUtil.IsDir(p) ? name + "\\" : name);
                if (names.Count >= cap) break;
            }
            return names.ToArray();
        }

        /// <summary>列表里是否有这个名字（不分大小写）。</summary>
        internal bool HasName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (object o in list.Items)
            {
                var li = o as ListBoxItem;
                if (li == null) continue;
                string p = li.Tag as string;
                if (p == null) continue;
                if (string.Equals(Path.GetFileName(p.TrimEnd('\\')), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 对外入口

        public static string PickFile(Window owner, string startPath, string title)
        {
            var w = new FilePickerWindow(Mode.File, startPath, title);
            if (owner != null) w.Owner = owner;
            w.ShowDialog();
            return w.result;
        }

        public static string PickFolder(Window owner, string startPath, string title)
        {
            var w = new FilePickerWindow(Mode.Folder, startPath, title);
            if (owner != null) w.Owner = owner;
            w.ShowDialog();
            return w.result;
        }
    }
}
