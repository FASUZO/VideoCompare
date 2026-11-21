using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoCompare
{
    /// <summary>简易输入对话框（重命名用）。</summary>
    public static class Prompt
    {
        public static string Show(Window owner, string title, string label, string initial)
        {
            var win = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Background = Theme.Panel,
                ResizeMode = ResizeMode.NoResize,
            };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Theme.Label(label, 12, Theme.Muted2));
            var box = new TextBox
            {
                Text = initial ?? "",
                Margin = new Thickness(0, 8, 0, 14),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Theme.Panel2,
                Foreground = Theme.Text,
                BorderBrush = Theme.Line,
                FontSize = 13,
            };
            box.SelectAll();
            panel.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = Theme.Btn("确定", true);
            var cancel = Theme.Btn("取消");
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            string result = null;
            ok.Click += (s, e) => { result = box.Text; win.Close(); };
            cancel.Click += (s, e) => { win.Close(); };
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { result = box.Text; win.Close(); }
                if (e.Key == Key.Escape) win.Close();
            };
            win.Content = panel;
            win.SourceInitialized += (s, e) => MainWindow.DarkTitleBar(win);
            win.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();
            return result;
        }
    }
}
