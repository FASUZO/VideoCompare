using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

/// <summary>
/// 视频对比工具的自解压启动器：
/// 自身尾部附带了应用目录的 zip 数据，首次运行解压到
/// %LOCALAPPDATA%\VideoCompare\app-&lt;hash&gt; 再启动真正的程序。
/// 使用 .NET 的常规解压 API，不依赖 PyInstaller 的临时目录机制。
/// </summary>
static class Program
{
    const string Magic = "VCPAYLOAD-V1-END";
    const int MagicLen = 16;
    const int LenLen = 8;
    const string AppExeName = "视频对比工具.exe";

    static string AppRoot()
    {
        string exeDir = null;
        try { exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); }
        catch { }

        string[] candidates = new string[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetEnvironmentVariable("APPDATA"),
            Environment.GetEnvironmentVariable("USERPROFILE"),
            exeDir
        };

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                Log("候选目录为空（环境变量缺失）");
                continue;
            }
            if (!Path.IsPathRooted(candidate))
            {
                Log("跳过非绝对路径：" + candidate);
                continue;
            }
            try
            {
                string dir = Path.Combine(candidate, "VideoCompare");
                Directory.CreateDirectory(dir);
                Log("使用目录：" + dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log("不可用 " + candidate + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        throw new Exception("找不到可写的本地目录（LOCALAPPDATA / TEMP 均不可用）。");
    }

    static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppRoot(), "launcher.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string selfPath = Process.GetCurrentProcess().MainModule.FileName;
            Log("启动：" + selfPath);
            byte[] all = File.ReadAllBytes(selfPath);
            Log("自身大小：" + all.Length);
            if (all.Length < MagicLen + LenLen + 1)
                throw new Exception("启动器文件不完整。");

            // 布局：[启动器][payload.zip][int64 长度][16 字节标记]
            string magic = Encoding.ASCII.GetString(all, all.Length - MagicLen, MagicLen);
            if (magic != Magic)
                throw new Exception("没有在启动器中找到内置的程序数据（文件可能被截断或被压缩软件修改）。");

            long zipLen = BitConverter.ToInt64(all, all.Length - MagicLen - LenLen);
            long zipStart = all.Length - MagicLen - LenLen - zipLen;
            if (zipLen <= 0 || zipStart < 0)
                throw new Exception("内置数据损坏，请重新下载完整的 exe。");

            Log("payload: start=" + zipStart + " len=" + zipLen);
            string hash = PayloadHash(all, zipStart, zipLen);
            Log("payload hash: " + hash);

            string root = AppRoot();
            string appDir = Path.Combine(root, "app-" + hash);
            string appExe = Path.Combine(appDir, AppExeName);

            if (!File.Exists(appExe))
            {
                Log("解压到：" + appDir);
                Directory.CreateDirectory(appDir);
                Extract(all, zipStart, zipLen, appDir);
                Log("解压完成");
            }

            var psi = new ProcessStartInfo(appExe);
            psi.WorkingDirectory = appDir;
            psi.UseShellExecute = false;
            if (args != null && args.Length > 0)
                psi.Arguments = string.Join(" ", Array.ConvertAll(args, Quote));
            Process.Start(psi);
            Log("已启动主程序");

            CleanupOldVersions(root, appDir);
            return 0;
        }
        catch (Exception ex)
        {
            Log("失败：" + ex.ToString());
            MessageBox.Show(
                "启动失败：" + ex.Message + Environment.NewLine + Environment.NewLine +
                "可以改用同目录下的「便携版」文件夹直接运行。",
                "视频对比工具",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    static string Quote(string value)
    {
        if (value == null) return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    static string PayloadHash(byte[] all, long start, long len)
    {
        using (var sha = SHA256.Create())
        {
            sha.TransformBlock(all, (int)start, (int)len, null, 0);
            sha.TransformFinalBlock(new byte[0], 0, 0);
            string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            return hex.Substring(0, 10);
        }
    }

    static void Extract(byte[] all, long start, long len, string dest)
    {
        using (var ms = new MemoryStream(all, (int)start, (int)len, false))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.Combine(dest, name);
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(target, true);
            }
        }
    }

    static void CleanupOldVersions(string root, string keepDir)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root, "app-*"))
            {
                if (string.Equals(dir, keepDir, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch { /* 正在被占用就跳过 */ }
            }
        }
        catch { /* 清理失败不影响使用 */ }
    }
}
