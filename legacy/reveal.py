"""用 Shell API 在资源管理器中定位文件。

本机环境的进程令牌受限，直接 `subprocess.Popen(["explorer", ...])` 会报
`explorer.exe - 应用程序错误 0xc0000142`（DLL 初始化失败）。
改用 shell32 的 `SHOpenFolderAndSelectItems`：通过 COM 请求"已经在运行"的
资源管理器去打开目录并选中文件，不需要新起进程。
"""

from __future__ import annotations

import os
import subprocess
import sys

CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def _reveal_via_shell(path: str) -> bool:
    """SHOpenFolderAndSelectItems —— 首选方案。"""
    try:
        import ctypes
        from ctypes import wintypes

        ole32 = ctypes.WinDLL("ole32", use_last_error=True)
        shell32 = ctypes.WinDLL("shell32", use_last_error=True)

        ole32.CoInitialize(None)
        shell32.ILCreateFromPathW.argtypes = [wintypes.LPCWSTR]
        shell32.ILCreateFromPathW.restype = ctypes.c_void_p
        shell32.SHOpenFolderAndSelectItems.argtypes = [
            ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_uint
        ]
        shell32.SHOpenFolderAndSelectItems.restype = ctypes.c_long
        shell32.ILFree.argtypes = [ctypes.c_void_p]

        pidl = shell32.ILCreateFromPathW(path)
        if not pidl:
            return False
        try:
            hr = shell32.SHOpenFolderAndSelectItems(pidl, 0, None, 0)
        finally:
            shell32.ILFree(pidl)
        return hr == 0
    except Exception:  # noqa: BLE001
        return False


def _open_folder_via_shell(folder: str) -> bool:
    """退一步：只打开目录（ShellExecute，走已运行的资源管理器）。"""
    try:
        os.startfile(folder)  # noqa: S606
        return True
    except Exception:  # noqa: BLE001
        pass
    try:
        subprocess.Popen(["explorer", folder], creationflags=CREATE_NO_WINDOW)
        return True
    except OSError:
        return False


def reveal(path: str) -> tuple[bool, str]:
    """打开资源管理器并选中 path；返回 (是否成功, 说明)。"""
    if os.name != "nt":
        return False, "仅支持 Windows"
    if not path or not os.path.exists(path):
        return False, "路径不存在"

    if _reveal_via_shell(path):
        return True, "shell"

    folder = path if os.path.isdir(path) else os.path.dirname(path)
    if _open_folder_via_shell(folder):
        return True, "folder-only"

    return False, "资源管理器没有响应"


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    print(reveal(target))
