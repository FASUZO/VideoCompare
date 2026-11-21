using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VideoCompare
{
    /// <summary>
    /// 单实例：第二个实例不再开新窗口，而是把已在运行的那个窗口提到前台后退出，
    /// 避免两个进程同时对同一个文件做删除/重命名。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        const string MutexName = "Local\\VideoCompare.SingleInstance.v1";

        Mutex _mutex;
        bool _owned;

        public bool IsFirst { get { return _owned; } }

        public static SingleInstance TryAcquire()
        {
            var si = new SingleInstance();
            try
            {
                bool created;
                si._mutex = new Mutex(true, MutexName, out created);
                si._owned = created;
                if (!created)
                {
                    // 可能是上一次异常退出留下的句柄，等一小会儿再试
                    try { si._owned = si._mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                    catch (AbandonedMutexException) { si._owned = true; }
                }
            }
            catch
            {
                si._owned = true;   // 拿不到互斥量就当它是第一个，别挡住启动
            }
            return si;
        }

        /// <summary>把已在运行的实例窗口提到前台（尽力而为，找不到就算了）。</summary>
        public static void ActivateExisting()
        {
            try
            {
                string me = Process.GetCurrentProcess().ProcessName;
                var targets = new HashSet<int>();
                foreach (var p in Process.GetProcessesByName(me))
                {
                    try { if (p.Id != Process.GetCurrentProcess().Id) targets.Add(p.Id); }
                    catch { }
                }
                if (targets.Count == 0) return;

                IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (!targets.Contains((int)pid)) return true;
                    if (GetWindowTextLength(hWnd) == 0) return true;
                    found = hWnd;
                    return false;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    if (IsIconic(found)) ShowWindowAsync(found, SW_RESTORE);
                    SetForegroundWindow(found);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_mutex == null) return;
                if (_owned) _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            catch { }
            _mutex = null;
            _owned = false;
        }

        const int SW_RESTORE = 9;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
}
