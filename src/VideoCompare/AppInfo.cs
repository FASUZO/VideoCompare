using System;
using System.Reflection;

namespace VideoCompare
{
    /// <summary>程序信息：版本号、名称、仓库地址（关于对话框与崩溃日志共用）。</summary>
    public static class AppInfo
    {
        public const string Name = "视频对比工具";
        public const string Repo = "https://github.com/FASUZO/VideoCompare";

        static string _version;

        /// <summary>形如 0.1.0（取自 AssemblyInformationalVersion，取不到则用 AssemblyVersion）。</summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var s = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                        if (!string.IsNullOrEmpty(s)) { _version = s; return _version; }
                    }
                    _version = asm.GetName().Version.ToString(3);
                }
                catch { _version = "0.0.0"; }
                return _version;
            }
        }

        /// <summary>形如 0.1.0.0。</summary>
        public static string VersionFull
        {
            get
            {
                try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch { return "0.0.0.0"; }
            }
        }
    }
}
