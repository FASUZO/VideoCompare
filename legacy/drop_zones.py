"""播放区透明落点层：让"从资源管理器拖到播放区"直接生效。

浏览器出于安全限制，页面 drop 事件只给 File（名字/大小），**拿不到真实路径**，
所以没法把拖进来的本地/NAS 文件交给后端播放。

这里在**播放区正上方**铺一层完全透明的 Win32 窗口（alpha=1，肉眼看不出），
只做两件事：
  1. 接收真正的 WM_DROPFILES（有完整路径）→ 回调上层，交给对应栏播放；
  2. 接收单击 → 回调上层，当作"点视频暂停/继续"（因为这一层挡住了浏览器的点击）。

窗口位置由页面把播放区的矩形报上来（CSS 像素），再换算成屏幕坐标，
所以它始终贴着播放区，不会多出任何可见的窗口。
"""

from __future__ import annotations

import ctypes
import os
import threading
import time
from ctypes import wintypes

WM_DESTROY = 0x0002
WM_PAINT = 0x000F
WM_ERASEBKGND = 0x0014
WM_DROPFILES = 0x0233
WM_LBUTTONDOWN = 0x0201
WM_NCHITTEST = 0x0084
HTCLIENT = 1

WS_POPUP = 0x80000000
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
WS_EX_ACCEPTFILES = 0x00000010
LWA_ALPHA = 0x00000002

SW_HIDE = 0
SW_SHOWNOACTIVATE = 4

VIDEO_EXTS = {
    ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".m2ts", ".mts",
    ".mpg", ".mpeg", ".wmv", ".flv", ".3gp", ".ogv", ".f4v", ".vob", ".mxf",
}

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
                             wintypes.WPARAM, wintypes.LPARAM)


def _is_video(path: str) -> bool:
    return os.path.splitext(path)[1].lower() in VIDEO_EXTS


class DropZones:
    """两块透明落点层（左栏/右栏播放区）。"""

    def __init__(self, on_drop, on_click, window_title="视频对比工具", browser_pid=0, owner_hwnd=0):
        self.on_drop = on_drop
        self.on_click = on_click
        self.window_title = window_title
        self.browser_pid = int(browser_pid or 0)
        self.owner_hwnd = int(owner_hwnd or 0)
        self.hwnds: dict[str, int] = {}
        self.started = False
        self._running = False
        self._thread = None
        self._wndproc = None
        self._pending: dict[str, tuple] = {}
        self._lock = threading.Lock()

    # ---------------- 对外 ----------------
    def start(self) -> bool:
        if os.name != "nt" or self.started:
            return self.started
        self.started = True
        self._thread = threading.Thread(target=self._run, daemon=True, name="drop-zones")
        self._thread.start()
        for _ in range(25):
            if self.hwnds.get("left") and self.hwnds.get("right"):
                return True
            time.sleep(0.1)
        return bool(self.hwnds)

    def set_zone(self, side: str, x: int, y: int, width: int, height: int):
        """更新某栏播放区的屏幕坐标（物理像素）。"""
        with self._lock:
            self._pending[side] = (int(x), int(y), int(width), int(height))

    def hide_all(self):
        with self._lock:
            self._pending["left"] = None
            self._pending["right"] = None

    def stop(self):
        self._running = False


class _ZoneWindow:
    """单块落点层的实现（避免 vtable/closure 复杂化，按 side 建两个实例）。"""


def _make_wndproc(pad: DropZones, side: str):
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)

    user32.DefWindowProcW.restype = ctypes.c_ssize_t
    user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    shell32.DragQueryFileW.argtypes = [wintypes.HANDLE, wintypes.UINT, wintypes.LPWSTR, wintypes.UINT]
    shell32.DragQueryFileW.restype = wintypes.UINT
    shell32.DragAcceptFiles.argtypes = [wintypes.HWND, wintypes.BOOL]

    def wndproc(hwnd, msg, wparam, lparam):
        try:
            if msg == WM_DROPFILES:
                try:
                    count = shell32.DragQueryFileW(wparam, 0xFFFFFFFF, None, 0)
                    paths = []
                    for i in range(count):
                        buf = ctypes.create_unicode_buffer(32768)
                        if shell32.DragQueryFileW(wparam, i, buf, 32768):
                            paths.append(buf.value)
                finally:
                    try:
                        shell32.DragFinish(wparam)
                    except Exception:  # noqa: BLE001
                        pass
                videos = [p for p in paths if _is_video(p)] or paths
                if videos:
                    try:
                        pad.on_drop(side, videos)
                    except Exception:  # noqa: BLE001
                        pass
                return 0
            if msg == WM_LBUTTONDOWN:
                try:
                    pad.on_click(side)
                except Exception:  # noqa: BLE001
                    pass
                return 0
            if msg == WM_NCHITTEST:
                return HTCLIENT   # 这一层自己处理点击（当作点视频暂停）
            if msg == WM_ERASEBKGND:
                return 1
            if msg == WM_PAINT:
                user32.ValidateRect(hwnd, None)
                return 0
            if msg == WM_DESTROY:
                pad.hwnds.pop(side, None)
                return 0
        except Exception:  # noqa: BLE001
            pass
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    return WNDPROC(wndproc)


def _run(self: DropZones):
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)

    user32.DefWindowProcW.restype = ctypes.c_ssize_t
    user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    user32.CreateWindowExW.restype = wintypes.HWND
    user32.CreateWindowExW.argtypes = [
        wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
    ]
    user32.CreateWindowExW.restype = wintypes.HWND
    user32.RegisterClassExW.restype = wintypes.ATOM
    user32.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_byte, wintypes.DWORD]
    user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int, wintypes.UINT]
    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.GetForegroundWindow.restype = wintypes.HWND
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.SetWindowLongPtrW.restype = ctypes.c_ssize_t
    user32.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_ssize_t]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.IsIconic.argtypes = [wintypes.HWND]
    user32.PeekMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND,
                                    wintypes.UINT, wintypes.UINT, wintypes.UINT]
    user32.PeekMessageW.restype = wintypes.BOOL
    user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
    user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
    user32.ValidateRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    shell32.DragAcceptFiles.argtypes = [wintypes.HWND, wintypes.BOOL]

    class WNDCLASSEXW(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.UINT), ("style", wintypes.UINT),
            ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
            ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE),
            ("hIcon", wintypes.HICON), ("hCursor", wintypes.HANDLE),
            ("hbrBackground", wintypes.HBRUSH), ("lpszMenuName", wintypes.LPCWSTR),
            ("lpszClassName", wintypes.LPCWSTR), ("hIconSm", wintypes.HICON),
        ]

    hinstance = kernel32.GetModuleHandleW(None)
    class_name = f"VCDropZone{os.getpid()}"

    procs = {}
    for side in ("left", "right"):
        procs[side] = _make_wndproc(self, side)

    wc = WNDCLASSEXW()
    wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
    wc.lpfnWndProc = procs["left"]
    wc.hInstance = hinstance
    wc.lpszClassName = class_name
    user32.RegisterClassExW(ctypes.byref(wc))

    # 不作为 TOPMOST（避免盖住别的程序），而是挂成浏览器窗口的从属窗口：
    # 从属窗口永远在自己的 owner 之上、且随 owner 一起被别的程序遮住。
    ex_style = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_ACCEPTFILES
    for side in ("left", "right"):
        hwnd = user32.CreateWindowExW(
            ex_style, class_name, f"VCZone-{side}", WS_POPUP,
            0, 0, 10, 10, wintypes.HWND(self.owner_hwnd) if self.owner_hwnd else None,
            None, hinstance, None,
        )
        if not hwnd:
            continue
        user32.SetLayeredWindowAttributes(wintypes.HWND(hwnd), 0, 1, LWA_ALPHA)  # 几乎全透明
        shell32.DragAcceptFiles(wintypes.HWND(hwnd), True)
        user32.ShowWindow(wintypes.HWND(hwnd), SW_HIDE)
        self.hwnds[side] = int(hwnd)

    self._running = True
    buf = ctypes.create_unicode_buffer(512)
    last_visible = {s: False for s in self.hwnds}

    msg = wintypes.MSG()
    PM_REMOVE = 0x0001
    while self._running:
        # 必须有消息泵：否则窗口收不到 WM_DROPFILES / 点击（SendMessage 还会卡住）
        # 每次最多处理 200 条，避免消息不断时卡住上面的定位逻辑
        for _ in range(200):
            if not user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
                break
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.05)

        # 落点层不需要抢前台：它挂成浏览器窗口的从属窗口，
        # 浏览器被别的程序遮住时它也会一起被遮住，所以这里只判断窗口本身的状态。
        now = time.time()
        if now - getattr(self, "_last_check", 0) < 0.3:
            continue
        self._last_check = now
        active = True
        if self.owner_hwnd:
            try:
                active = bool(user32.IsWindowVisible(wintypes.HWND(self.owner_hwnd))) and not bool(
                    user32.IsIconic(wintypes.HWND(self.owner_hwnd)))
            except Exception:  # noqa: BLE001
                active = True

        with self._lock:
            pending = dict(self._pending)

        for side, hwnd in list(self.hwnds.items()):
            rect = pending.get(side)
            if not rect or not active:
                if last_visible.get(side):
                    user32.ShowWindow(wintypes.HWND(hwnd), SW_HIDE)
                    last_visible[side] = False
                continue
            x, y, w, h = rect
            if w < 40 or h < 40:
                if last_visible.get(side):
                    user32.ShowWindow(wintypes.HWND(hwnd), SW_HIDE)
                    last_visible[side] = False
                continue
            user32.SetWindowPos(wintypes.HWND(hwnd), wintypes.HWND(0), x, y, w, h, 0x0004 | 0x0010)
            if not last_visible.get(side):
                user32.ShowWindow(wintypes.HWND(hwnd), SW_SHOWNOACTIVATE)
                last_visible[side] = True

    for hwnd in list(self.hwnds.values()):
        try:
            user32.DestroyWindow(wintypes.HWND(hwnd))
        except Exception:  # noqa: BLE001
            pass
    self.hwnds.clear()


DropZones._run = _run
