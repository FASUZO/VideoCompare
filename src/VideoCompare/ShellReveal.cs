using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VideoCompare
{
    /// <summary>
    /// 在资源管理器里定位文件。
    /// 旧实现 spawn 了 `explorer.exe /select,"..."`，在部分机器上它以 0xc0000142
    /// （应用初始化失败）报错；改为调用 shell32 的 SHOpenFolderAndSelectItems，
    /// 由桌面 shell 自己处理，不需要起子进程。
    /// </summary>
    internal static class ShellReveal
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr ILCreateFromPath(string pszPath);

        [DllImport("shell32.dll")]
        static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

        /// <summary>成功返回 true；失败时降级为「只打开所在文件夹」。</summary>
        public static bool SelectInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            IntPtr pidl = IntPtr.Zero;
            try
            {
                pidl = ILCreateFromPath(path);
                if (pidl != IntPtr.Zero)
                {
                    int hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                    if (hr == 0) return true;
                }
            }
            catch { }
            finally
            {
                if (pidl != IntPtr.Zero) { try { ILFree(pidl); } catch { } }
            }

            return OpenFolderOnly(path);
        }

        /// <summary>只打开文件夹（网络路径、长路径等定位失败时的兜底）。</summary>
        public static bool OpenFolderOnly(string path)
        {
            try
            {
                string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}
