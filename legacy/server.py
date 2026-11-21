"""视频对比工具的本地服务端。

只依赖 Python 标准库（可选 ffmpeg / OpenCV 增强）：
  * 静态页面            /
  * 视频流（支持 Range）  /stream?path=...
  * 元数据              /api/probe?path=...
  * 目录浏览            /api/list?dir=...
  * 文件操作            /api/fs/rename | move | delete | reveal
  * 系统对话框          /api/dialog/open | folder
  * 转码（兼容播放）     /api/transcode  /api/transcode/status
"""

from __future__ import annotations

import json
import mimetypes
import os
import posixpath
import queue
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import drop_zones
import mediaprobe
import reveal
from ffmpeg_utils import (
    APP_DATA_DIR,
    FFMPEG,
    FFPROBE,
    TRANSCODER,
    grab_frame_jpeg,
    has_ffmpeg,
    has_ffprobe,
    is_network_path,
    probe_ffprobe,
)

FROZEN = bool(getattr(sys, "frozen", False))
# exe 模式下：资源在 PyInstaller 解包目录，可写的临时/日志目录在 exe 旁边
BASE_DIR = os.path.dirname(sys.executable) if FROZEN else os.path.dirname(os.path.abspath(__file__))
RES_DIR = getattr(sys, "_MEIPASS", BASE_DIR) if FROZEN else BASE_DIR
APP_DIR = BASE_DIR
WEB_DIR = os.path.join(RES_DIR, "web")
LOG_PATH = os.path.join(APP_DATA_DIR, "app.log")
VIDEO_EXTENSIONS = mediaprobe.VIDEO_EXTENSIONS

CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0
MAX_JSON_BODY = 1 << 20

# 正在传输中的视频流连接（path -> set(handler)），删除/移动/重命名前主动断开
ACTIVE_STREAMS: dict = {}

MIME_TYPES = {
    ".html": "text/html; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    ".ico": "image/x-icon",
    ".mp4": "video/mp4",
    ".m4v": "video/mp4",
    ".mov": "video/quicktime",
    ".mkv": "video/x-matroska",
    ".webm": "video/webm",
    ".avi": "video/x-msvideo",
    ".ts": "video/mp2t",
    ".flv": "video/x-flv",
    ".wmv": "video/x-ms-wmv",
    ".mpg": "video/mpeg",
    ".mpeg": "video/mpeg",
}


# --------------------------------------------------------------------------
# 工具函数
# --------------------------------------------------------------------------

def _setup_streams():
    """--noconsole 打包后 sys.stdout/stderr 为 None，重定向到日志文件避免 print 崩溃。"""
    if sys.stdout is not None and sys.stderr is not None:
        return
    try:
        os.makedirs(os.path.dirname(LOG_PATH), exist_ok=True)
        stream = open(LOG_PATH, "a", encoding="utf-8", buffering=1)
    except OSError:
        class _Null:
            def write(self, *_a, **_k):
                return 0

            def flush(self):
                return None

        stream = _Null()
    if sys.stdout is None:
        sys.stdout = stream
    if sys.stderr is None:
        sys.stderr = stream


_setup_streams()


def norm_path(value: str | None) -> str:
    """把前端传来的路径规范化为本机绝对路径。

    兼容各种输入形态：
      \\\\NAS\\Media\\...   标准 UNC
      //NAS/Media/...      正斜杠 UNC
      \\NAS\\Media\\...     单反斜杠开头（用户从报错文案复制回来的常见形态）
      /c/Users/...         Git Bash / MSYS 风格
      C:/Users/...         正斜杠盘符
    """
    if not value:
        return ""
    path = urllib.parse.unquote(str(value)).strip().strip('"')
    if os.name == "nt":
        # 先统一斜杠方向（Windows 语义下把 / 当作 \）
        path = path.replace("/", "\\")

        # Git Bash / MSYS 风格：/c/Users/...（此时已变成 \c\Users\...）→ C:\Users\...
        # 必须在 UNC 归一化之前判断，否则 \c 会被当成服务器名
        if re.fullmatch(r"\\[a-zA-Z](\\.*)?", path):
            path = path[1].upper() + ":" + path[2:]

        # 开头不足/超过两个反斜杠的 \NAS\...、\\\NAS\... 归一成标准 \\NAS\...
        if path.startswith("\\"):
            path = "\\\\" + path.lstrip("\\")

        if re.fullmatch(r"[a-zA-Z]:", path):
            path = path + "\\"
    normalized = os.path.normpath(os.path.expanduser(path))
    # normpath 可能把 \\NAS\... 折叠，要保证 UNC 前缀还在
    if path.startswith("\\\\") and not normalized.startswith("\\\\"):
        normalized = "\\\\" + normalized.lstrip("\\")
    return normalized


def is_video(path: str) -> bool:
    return os.path.splitext(path)[1].lower() in VIDEO_EXTENSIONS


def human_size(num: float | int | None) -> str:
    if not num:
        return "-"
    units = ["B", "KB", "MB", "GB", "TB"]
    value = float(num)
    for unit in units:
        if value < 1024 or unit == units[-1]:
            return f"{value:.2f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{value:.2f} TB"


def list_drives() -> list[dict]:
    if os.name != "nt":
        return [{"path": "/", "label": "/"}]
    drives = []
    for letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ":
        root = f"{letter}:\\"
        if os.path.exists(root):
            drives.append({"path": root, "label": f"{letter}:"})
    return drives


class DialogWorker:
    """在独立线程里跑 tkinter 系统对话框，避免阻塞 HTTP 线程。"""

    def __init__(self):
        self.q: queue.Queue = queue.Queue()
        self.available = True
        try:
            import tkinter  # noqa: F401
        except Exception:
            self.available = False
        if self.available:
            threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        import tkinter as tk
        from tkinter import filedialog

        while True:
            item = self.q.get()
            if item is None:
                break
            kind, opts, result = item
            try:
                root = tk.Tk()
                root.withdraw()
                root.attributes("-topmost", True)
                root.update_idletasks()
                initial = opts.get("dir") or os.path.expanduser("~")
                title = opts.get("title") or "请选择"
                if kind == "open":
                    path = filedialog.askopenfilename(
                        parent=root,
                        title=title,
                        initialdir=initial if os.path.isdir(initial) else os.path.expanduser("~"),
                        filetypes=[
                            ("视频文件", " ".join("*" + ext for ext in sorted(VIDEO_EXTENSIONS))),
                            ("所有文件", "*.*"),
                        ],
                    )
                else:
                    path = filedialog.askdirectory(
                        parent=root, title=title, initialdir=initial, mustexist=True
                    )
                root.destroy()
                result["path"] = path or None
            except Exception as exc:  # noqa: BLE001
                result["error"] = str(exc)
            finally:
                result["done"].set()

    def ask(self, kind: str, opts: dict, timeout: float = 600.0):
        if not self.available:
            return None, "系统对话框不可用（缺少 tkinter）"
        result = {"done": threading.Event(), "path": None, "error": None}
        self.q.put((kind, opts, result))
        if not result["done"].wait(timeout):
            return None, "对话框超时"
        return result["path"], result["error"]


DIALOGS = DialogWorker()

# 页面每隔几秒发一次心跳；exe（无控制台）模式下长时间没有心跳就自动退出，
# 避免用户关掉浏览器窗口后后台残留一个看不见的进程。
LAST_PING = {"t": time.time(), "seen": False}
IDLE_EXIT_SECONDS = 300


def _message(text: str, title: str = "视频对比工具"):
    """无控制台时用系统弹窗提示。"""
    try:
        import tkinter as tk
        from tkinter import messagebox

        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)
        messagebox.showinfo(title, text, parent=root)
        root.destroy()
    except Exception:  # noqa: BLE001
        print(text)


def _ping_watchdog(server):
    while True:
        time.sleep(20)
        if time.time() - LAST_PING["t"] > IDLE_EXIT_SECONDS:
            print("[video-compare] 长时间没有页面心跳，自动退出")
            try:
                server.shutdown()
            except Exception:  # noqa: BLE001
                pass
            return


def delete_to_recycle_bin(path: str) -> tuple[bool, str]:
    """调用 Shell 接口把文件放进回收站。"""
    if os.name != "nt":
        try:
            os.remove(path)
            return True, ""
        except OSError as exc:
            return False, str(exc)
    escaped = path.replace("'", "''")
    script = (
        "Add-Type -AssemblyName Microsoft.VisualBasic;"
        f"[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('{escaped}',"
        "'OnlyErrorDialogs','SendToRecycleBin')"
    )
    try:
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=60,
            creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, str(exc)
    if proc.returncode == 0:
        return True, ""
    return False, (proc.stderr.decode("utf-8", "replace").strip() or "回收站操作失败")[:400]


_PROBE_CACHE: dict[tuple, dict] = {}
_PROBE_CACHE_MAX = 64


def release_file_locks(path: str, timeout: float = 3.0) -> dict:
    """释放服务器侧对该文件的全部占用：
    1) 杀掉读该文件的转码 ffmpeg；
    2) 断开该文件的视频流连接（浏览器侧由前端配合清除 src）；
    3) 等句柄真正落下。
    """
    result = {"transcodes": 0, "streams": 0}
    key = os.path.normcase(os.path.abspath(path))

    for job_id, job in list(TRANSCODER.jobs.items()):
        if getattr(job, "path", None) and os.path.normcase(job.path) == key and job.state in ("queued", "running"):
            TRANSCODER.cancel(job_id)
            result["transcodes"] += 1

    handlers = ACTIVE_STREAMS.get(key)
    if handlers:
        result["streams"] = len(handlers)
        for h in list(handlers):
            try:
                h.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                try:
                    h.connection.close()
                except OSError:
                    pass

    deadline = time.time() + timeout
    while time.time() < deadline:
        if not ACTIVE_STREAMS.get(key):
            break
        time.sleep(0.1)
    return result


# ---------------------------------------------------------------- 拖放 / 播放区落点
DROP_LOCK = threading.Lock()
DROP_QUEUE: list = []
DROP_ZONES: dict = {"instance": None, "browser_hwnd": None, "browser_pid": 0}


def _expand_dropped(path: str) -> list:
    """拖进来的是文件夹时，取里面的视频（最多 2 个）。"""
    if os.path.isdir(path):
        found = []
        try:
            for name in sorted(os.listdir(path)):
                full = os.path.join(path, name)
                if os.path.isfile(full) and is_video(full):
                    found.append(full)
                    if len(found) >= 2:
                        break
        except OSError:
            return []
        return found
    return [path]


def _queue_item(item: dict):
    with DROP_LOCK:
        DROP_QUEUE.append(item)


def _on_zone_drop(side: str, paths: list):
    """播放区收到拖放：把文件排进队列，页面轮询后加载。"""
    files: list = []
    for raw in paths:
        files.extend(_expand_dropped(raw))
    if not files:
        return
    if len(files) >= 2:
        _queue_item({"type": "load", "side": "left", "path": files[0]})
        _queue_item({"type": "load", "side": "right", "path": files[1]})
    else:
        _queue_item({"type": "load", "side": side, "path": files[0]})


def _on_zone_click(side: str):
    """播放区被单击：落点层挡住了浏览器点击，这里补上"暂停/继续"。"""
    _queue_item({"type": "toggle", "side": side})


def start_drop_zones() -> bool:
    if os.name != "nt":
        return False
    if DROP_ZONES["instance"] is not None:
        return True
    try:
        zones = drop_zones.DropZones(on_drop=_on_zone_drop, on_click=_on_zone_click,
                                     browser_pid=DROP_ZONES.get("browser_pid") or 0)
        DROP_ZONES["instance"] = zones
        ok = zones.start()
        print(f"[drop-zones] 播放区落点层{'已就绪' if ok else '启动失败'}", flush=True)
        return ok
    except Exception as exc:  # noqa: BLE001
        print(f"[drop-zones] 启动失败: {exc}", flush=True)
        return False


def _client_rect_screen(hwnd: int):
    """顶层窗口客户区在屏幕上的 (left, top, width, height)。"""
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
        rect = wintypes.RECT()
        if not user32.GetClientRect(wintypes.HWND(hwnd), ctypes.byref(rect)):
            return None
        pt = wintypes.POINT(0, 0)
        if not user32.ClientToScreen(wintypes.HWND(hwnd), ctypes.byref(pt)):
            return None
        return (pt.x, pt.y, rect.right - rect.left, rect.bottom - rect.top)
    except Exception:  # noqa: BLE001
        return None


def apply_zone_layout(payload: dict) -> dict:
    """页面把播放区矩形（viewport CSS 像素）报上来，换算成屏幕物理像素。"""
    if DROP_ZONES["instance"] is None:
        return {"ok": False, "reason": "not-started"}

    # 找到本程序浏览器窗口（标题含"视频对比工具"）
    hwnds = _app_windows()
    if not hwnds:
        return {"ok": False, "reason": "no-window"}
    try:
        import ctypes
        from ctypes import wintypes

        _u = ctypes.WinDLL("user32", use_last_error=True)
        _u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        _pid = wintypes.DWORD(0)
        _u.GetWindowThreadProcessId(wintypes.HWND(hwnds[0]), ctypes.byref(_pid))
        if _pid.value and _pid.value != DROP_ZONES.get("browser_pid"):
            DROP_ZONES["browser_pid"] = int(_pid.value)
            if DROP_ZONES["instance"] is not None:
                DROP_ZONES["instance"].browser_pid = int(_pid.value)
    except Exception:  # noqa: BLE001
        pass

    if DROP_ZONES["instance"] is not None and DROP_ZONES.get("browser_hwnd") != int(hwnds[0]):
        DROP_ZONES["browser_hwnd"] = int(hwnds[0])
        DROP_ZONES["instance"].owner_hwnd = int(hwnds[0])

    client = _client_rect_screen(hwnds[0])
    if not client:
        return {"ok": False, "reason": "no-client-rect"}
    cl, ct, cw, ch = client

    vw = float(payload.get("vw") or 0) or 1.0
    vh = float(payload.get("vh") or 0) or 1.0
    sx = cw / vw
    sy = ch / vh

    zones = DROP_ZONES["instance"]
    applied = {}
    for side in ("left", "right"):
        box = payload.get(side)
        if not box:
            zones.set_zone(side, 0, 0, 0, 0)
            applied[side] = None
            continue
        x = cl + int(round(float(box.get("x", 0)) * sx))
        y = ct + int(round(float(box.get("y", 0)) * sy))
        w = int(round(float(box.get("w", 0)) * sx))
        h = int(round(float(box.get("h", 0)) * sy))
        zones.set_zone(side, x, y, w, h)
        applied[side] = [x, y, w, h]
    DROP_ZONES["last_layout"] = {"request": {"vw": vw, "vh": vh}, "applied": applied}
    return {"ok": True, "rects": applied}


def probe_path(path: str) -> dict:
    """读取元数据；同一文件（大小/修改时间未变）在本次会话内只探测一次。

    局域网大文件（几十 GB）探测虽快，但反复探测会拖慢界面，因此做内存缓存。
    """
    key = None
    try:
        st = os.stat(path)
        key = (os.path.normcase(path), st.st_size, int(st.st_mtime))
        cached = _PROBE_CACHE.get(key)
        if cached:
            return cached
    except OSError:
        st = None

    timeout = 120 if is_network_path(path) else 30
    meta = probe_ffprobe(path, timeout=timeout) if has_ffprobe() else None
    if not meta:
        meta = mediaprobe.probe(path)
        meta.setdefault("engine", "python")
    try:
        st = os.stat(path)
        meta["size"] = meta.get("size") or st.st_size
        meta["size_human"] = human_size(st.st_size)
        meta["mtime"] = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(st.st_mtime))
        meta["mtime_ts"] = st.st_mtime
    except OSError:
        meta.setdefault("size_human", "-")
    meta["path"] = path
    meta["name"] = os.path.basename(path)
    meta["dir"] = os.path.dirname(path)
    meta["ext"] = os.path.splitext(path)[1].lower()
    meta["warnings"] = [w for w in (meta.get("warnings") or []) if w]
    meta["network"] = is_network_path(path)
    if key is not None:
        if len(_PROBE_CACHE) >= _PROBE_CACHE_MAX:
            _PROBE_CACHE.clear()
        _PROBE_CACHE[key] = meta
    return meta


# --------------------------------------------------------------------------
# HTTP 处理
# --------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    server_version = "VideoCompare/1.0"
    protocol_version = "HTTP/1.1"

    # ---- 基础响应 ----
    def _send_json(self, data, status=200):
        payload = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _send_error_json(self, message: str, status=400, **extra):
        body = {"ok": False, "error": message}
        body.update(extra)
        self._send_json(body, status)

    def _send_bytes(self, payload: bytes, content_type: str, status=200, cache=False):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "public, max-age=60" if cache else "no-store")
        self.end_headers()
        try:
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass

    def log_message(self, fmt, *args):  # 简化日志
        if os.environ.get("VC_VERBOSE"):
            sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    # ---- 路由 ----
    def do_GET(self):
        parsed = urllib.parse.urlsplit(self.path)
        route = parsed.path
        query = urllib.parse.parse_qs(parsed.query)
        try:
            if route in ("/", "/index.html"):
                return self._serve_static("index.html")
            if route.startswith("/assets/"):
                return self._serve_static(route[len("/assets/"):])
            if route == "/api/drives":
                return self._send_json({"ok": True, "drives": list_drives()})
            if route == "/api/list":
                return self._api_list(query)
            if route == "/api/stat":
                return self._api_stat(query)
            if route == "/api/probe":
                return self._api_probe(query)
            if route == "/api/thumb":
                return self._api_frame(query, default_width=640)
            if route == "/api/frame":
                return self._api_frame(query, default_width=1600)
            if route == "/api/transcode/status":
                return self._api_transcode_status(query)
            if route == "/api/drop-zones":
                return self._send_json(apply_zone_layout(body))
            if route == "/api/focus-window":
                hwnds = _app_windows()
                if not hwnds:
                    return self._send_error_json("没有找到界面窗口", 404)
                try:
                    import ctypes
                    from ctypes import wintypes

                    user32 = ctypes.WinDLL("user32", use_last_error=True)
                    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
                    user32.SetForegroundWindow.argtypes = [wintypes.HWND]
                    user32.BringWindowToTop.argtypes = [wintypes.HWND]
                    h = wintypes.HWND(hwnds[0])
                    user32.ShowWindow(h, 9)
                    user32.BringWindowToTop(h)
                    ok = bool(user32.SetForegroundWindow(h))
                except Exception:  # noqa: BLE001
                    ok = False
                return self._send_json({"ok": True, "focused": ok})
            if route == "/api/open-window":
                port = self.server.server_address[1]
                threading.Thread(target=open_window, args=(f"http://127.0.0.1:{port}/",), daemon=True).start()
                return self._send_json({"ok": True, "opened": True})
            if route == "/api/drop/poll":
                with DROP_LOCK:
                    items = list(DROP_QUEUE)
                    DROP_QUEUE.clear()
                return self._send_json({"ok": True, "items": items})

            if route == "/api/health":
                age = time.time() - LAST_PING["t"]
                return self._send_json({
                    "ok": True,
                    "pageSeen": LAST_PING["seen"],
                    "lastPingAge": round(age, 1),
                    "webDir": WEB_DIR,
                    "appData": APP_DATA_DIR,
                    "dropZones": DROP_ZONES.get("last_layout"),
                    "browserPid": DROP_ZONES.get("browser_pid"),
                })
            if route == "/api/ping":
                LAST_PING["t"] = time.time()
                LAST_PING["seen"] = True
                return self._send_json({"ok": True})
            if route == "/api/env":
                return self._send_json({
                    "ok": True,
                    "ffmpeg": FFMPEG,
                    "ffprobe": FFPROBE,
                    "has_ffmpeg": has_ffmpeg(),
                    "has_ffprobe": has_ffprobe(),
                    "platform": sys.platform,
                })
            if route == "/stream":
                return self._serve_stream(query)
            return self._send_error_json("未知路径: " + route, 404)
        except (BrokenPipeError, ConnectionResetError):
            return
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            return self._send_error_json(f"服务端异常: {exc}", 500)

    def do_HEAD(self):
        parsed = urllib.parse.urlsplit(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path == "/stream":
            self._serve_stream(query, head_only=True)
            return
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        parsed = urllib.parse.urlsplit(self.path)
        route = parsed.path
        try:
            body = self._read_json()
            if body is None:
                return self._send_error_json("请求体不是合法 JSON")
            if route == "/api/fs/rename":
                return self._api_rename(body)
            if route == "/api/fs/move":
                return self._api_move(body)
            if route == "/api/fs/delete":
                return self._api_delete(body)
            if route == "/api/fs/reveal":
                return self._api_reveal(body)
            if route == "/api/dialog/open":
                return self._api_dialog(body, "open")
            if route == "/api/dialog/folder":
                return self._api_dialog(body, "folder")
            if route == "/api/transcode":
                return self._api_transcode(body)
            if route == "/api/drop-zones":
                return self._send_json(apply_zone_layout(body))
            if route == "/api/focus-window":
                hwnds = _app_windows()
                if not hwnds:
                    return self._send_error_json("没有找到界面窗口", 404)
                try:
                    import ctypes
                    from ctypes import wintypes

                    user32 = ctypes.WinDLL("user32", use_last_error=True)
                    user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
                    user32.SetForegroundWindow.argtypes = [wintypes.HWND]
                    user32.BringWindowToTop.argtypes = [wintypes.HWND]
                    h = wintypes.HWND(hwnds[0])
                    user32.ShowWindow(h, 9)
                    user32.BringWindowToTop(h)
                    ok = bool(user32.SetForegroundWindow(h))
                except Exception:  # noqa: BLE001
                    ok = False
                return self._send_json({"ok": True, "focused": ok})
            if route == "/api/open-window":
                port = self.server.server_address[1]
                threading.Thread(target=open_window, args=(f"http://127.0.0.1:{port}/",), daemon=True).start()
                return self._send_json({"ok": True, "opened": True})
            if route == "/api/transcode/cancel":
                return self._api_transcode_cancel(body)
            if route == "/api/shutdown":
                threading.Timer(0.3, self.server.shutdown).start()
                return self._send_json({"ok": True})
            return self._send_error_json("未知接口: " + route, 404)
        except (BrokenPipeError, ConnectionResetError):
            return
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            return self._send_error_json(f"服务端异常: {exc}", 500)

    def _read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        if length > MAX_JSON_BODY:
            return None
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            return None

    # ---- 静态文件 ----
    def _serve_static(self, relative: str):
        safe = posixpath.normpath("/" + relative).lstrip("/")
        target = os.path.join(WEB_DIR, *safe.split("/"))
        if not os.path.isfile(target):
            return self._send_error_json("文件不存在: " + relative, 404)
        ext = os.path.splitext(target)[1].lower()
        with open(target, "rb") as fh:
            payload = fh.read()
        self._send_bytes(payload, MIME_TYPES.get(ext, "application/octet-stream"))

    # ---- API：目录 / 元数据 ----
    def _api_list(self, query):
        target = norm_path((query.get("dir") or [""])[0])
        # \\server  形式：列出该服务器的共享
        if re.fullmatch(r"\\\\[^\\/]+\\?", target):
            return self._list_shares(target.rstrip("\\") )
        if not target:
            return self._send_json({
                "ok": True, "dir": "", "parent": None,
                "entries": [{"name": d["label"], "path": d["path"], "type": "drive"} for d in list_drives()],
            })
        if not os.path.isdir(target):
            return self._send_error_json(f"目录不存在: {target}", 404)
        entries = []
        try:
            with os.scandir(target) as it:
                for entry in it:
                    try:
                        if entry.is_dir():
                            entries.append({"name": entry.name, "path": entry.path, "type": "dir"})
                        elif is_video(entry.name):
                            stat = entry.stat()
                            entries.append({
                                "name": entry.name,
                                "path": entry.path,
                                "type": "file",
                                "size": stat.st_size,
                                "size_human": human_size(stat.st_size),
                                "mtime": time.strftime("%Y-%m-%d %H:%M", time.localtime(stat.st_mtime)),
                            })
                    except OSError:
                        continue
        except PermissionError:
            return self._send_error_json(f"没有访问权限: {target}", 403)
        except OSError as exc:
            return self._send_error_json(f"读取目录失败: {exc}", 500)
        entries.sort(key=lambda e: (e["type"] != "dir", e["name"].lower()))
        # UNC 的上级：\\server\share\dir → \\server\share → … → \\server\share → \\NAS（服务器共享列表）→ 无
        m = re.fullmatch(r"(\\\\[^\\/]+)(.*)", target)
        if m:
            rest = m.group(2).rstrip("\\/")
            if rest:
                parts = rest.split("\\")
                parts.pop()
                parent = m.group(1) + ("\\" + "\\".join(parts) if parts else "")
            else:
                parent = None  # 已经是 \\server\share（服务器根共享），没有上一级
        else:
            parent = os.path.dirname(target.rstrip("\\/")) or None
            if parent == target:
                parent = None
        return self._send_json({"ok": True, "dir": target, "parent": parent, "entries": entries})

    def _list_shares(self, server: str):
        try:
            proc = subprocess.run(
                ["net", "view", server],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                timeout=25, creationflags=CREATE_NO_WINDOW,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            return self._send_error_json(f"无法访问 {server}：{exc}", 500)
        text = proc.stdout.decode("mbcs", "replace")
        entries = []
        started = False
        for line in text.splitlines():
            if set(line.strip()) <= {"-"} and line.strip():
                started = True
                continue
            if not started:
                continue
            parts = re.split(r"\s{2,}", line.strip())
            name = parts[0].strip() if parts else ""
            if not name or name.startswith("命令") or name.lower().startswith("the command"):
                continue
            entries.append({"name": name, "path": f"{server}\\{name}", "type": "dir"})
        entries.sort(key=lambda e: e["name"].lower())
        return self._send_json({
            "ok": True, "dir": server, "parent": None,
            "entries": entries,
            "hint": "这是局域网服务器的共享列表" if entries else "没有发现共享（可能需要凭据或权限）",
        })

    def _api_stat(self, query):
        path = norm_path((query.get("path") or [""])[0])
        if not path:
            return self._send_json({"ok": True, "type": "missing", "path": path})
        # \\server 形式（含尾反斜杠）：属于“服务器的共享列表”，不是普通目录
        if re.fullmatch(r"\\\\[^\\/]+\\?", path):
            return self._send_json({"ok": True, "type": "server", "path": path.rstrip("\\")})
        if os.path.isdir(path):
            return self._send_json({"ok": True, "type": "dir", "path": path})
        if os.path.isfile(path):
            return self._send_json({
                "ok": True, "type": "file", "path": path,
                "video": is_video(path),
                "size": os.path.getsize(path),
            })
        return self._send_json({"ok": True, "type": "missing", "path": path})

    def _api_probe(self, query):
        path = norm_path((query.get("path") or [""])[0])
        if not path or not os.path.isfile(path):
            return self._send_error_json(f"文件不存在: {path}", 404)
        return self._send_json({"ok": True, "meta": probe_path(path)})

    def _api_frame(self, query, default_width=960):
        path = norm_path((query.get("path") or [""])[0])
        if not path or not os.path.isfile(path):
            return self._send_error_json("文件不存在", 404)
        time_s = float((query.get("t") or ["0"])[0] or 0)
        width = int((query.get("w") or [str(default_width)])[0] or default_width)
        payload = grab_frame_jpeg(path, time_s, width=width) if has_ffmpeg() else None
        if not payload:
            reader = mediaprobe.FrameReader(path)
            payload = reader.frame_jpeg(time_s, max_width=width)
            reader.close()
        if not payload:
            return self._send_error_json("无法解码该视频帧", 500)
        self._send_bytes(payload, "image/jpeg", cache=True)

    # ---- API：文件操作 ----
    def _check_file(self, body):
        path = norm_path(body.get("path"))
        if not path or not os.path.isfile(path):
            self._send_error_json(f"文件不存在: {path}", 404)
            return None
        return path

    def _api_rename(self, body):
        path = self._check_file(body)
        if not path:
            return
        released = release_file_locks(path)
        new_name = str(body.get("newName") or "").strip()
        if not new_name:
            return self._send_error_json("新文件名不能为空")
        if new_name in (".", "..") or re.search(r'[\\/:*?"<>|\r\n\t]', new_name):
            return self._send_error_json('文件名不能包含 \\ / : * ? " < > | 等字符')
        target = os.path.join(os.path.dirname(path), new_name)
        if os.path.normcase(target) == os.path.normcase(path):
            return self._send_json({"ok": True, "path": path, "renamed": False})
        if os.path.exists(target):
            return self._send_error_json("目标文件名已存在")
        try:
            os.rename(path, target)
        except OSError as exc:
            return self._send_error_json(f"重命名失败: {exc}", 500)
        return self._send_json({"ok": True, "path": target, "renamed": True, "oldPath": path})

    def _api_move(self, body):
        path = self._check_file(body)
        if not path:
            return
        released = release_file_locks(path)
        dest_dir = norm_path(body.get("destDir"))
        if not dest_dir or not os.path.isdir(dest_dir):
            return self._send_error_json(f"目标目录不存在: {dest_dir}")
        if os.path.normcase(dest_dir) == os.path.normcase(os.path.dirname(path)):
            return self._send_json({"ok": True, "path": path, "moved": False})
        name = os.path.basename(path)
        target = os.path.join(dest_dir, name)
        if os.path.exists(target):
            stem, ext = os.path.splitext(name)
            for index in range(1, 1000):
                candidate = os.path.join(dest_dir, f"{stem} ({index}){ext}")
                if not os.path.exists(candidate):
                    target = candidate
                    break
            else:
                return self._send_error_json("目标目录存在过多同名文件")
        try:
            shutil.move(path, target)
        except OSError as exc:
            return self._send_error_json(f"移动失败: {exc}", 500)
        return self._send_json({"ok": True, "path": target, "moved": True, "oldPath": path})

    def _api_delete(self, body):
        path = self._check_file(body)
        if not path:
            return
        released = release_file_locks(path)
        mode = str(body.get("mode") or "recycle")
        if mode == "permanent":
            try:
                os.remove(path)
            except OSError as exc:
                return self._send_error_json(f"删除失败: {exc}", 500)
            return self._send_json({"ok": True, "mode": "permanent", "path": path})
        if is_network_path(path):
            return self._send_error_json(
                "网络共享没有回收站，删除将不可恢复（outOfRecycle）", 500, canPermanent=True
            )
        ok, err = delete_to_recycle_bin(path)
        if not ok:
            return self._send_error_json(f"移入回收站失败: {err}", 500, canPermanent=True)
        return self._send_json({"ok": True, "mode": "recycle", "path": path})

    def _api_reveal(self, body):
        path = norm_path(body.get("path"))
        if not path or not os.path.exists(path):
            return self._send_error_json("路径不存在", 404)
        ok, how = reveal.reveal(path)
        if not ok:
            return self._send_error_json(f"无法打开资源管理器：{how}", 500)
        return self._send_json({"ok": True, "mode": how})

    def _api_dialog(self, body, kind):
        initial = norm_path(body.get("dir")) or os.path.expanduser("~")
        path, error = DIALOGS.ask(kind, {"dir": initial, "title": body.get("title") or "请选择"})
        if error:
            return self._send_error_json(error, 500)
        return self._send_json({"ok": True, "path": path})

    # ---- API：转码 ----
    def _api_transcode(self, body):
        path = self._check_file(body)
        if not path:
            return
        duration = body.get("duration")
        start = body.get("start") or 0
        window = body.get("window")
        if not has_ffmpeg():
            return self._send_error_json("未找到 ffmpeg，无法转码", 500)
        job = TRANSCODER.start(
            path,
            float(duration) if duration else None,
            start=float(start) if start else 0.0,
            window=float(window) if window else None,
        )
        return self._send_json({"ok": True, "job": job})

    def _api_transcode_status(self, query):
        job_id = (query.get("jobId") or [""])[0]
        job = TRANSCODER.status(job_id)
        if not job:
            return self._send_json({"ok": True, "job": {"jobId": job_id, "state": "unknown"}})
        return self._send_json({"ok": True, "job": job})

    def _api_transcode_cancel(self, body):
        job_id = str(body.get("jobId") or "")
        return self._send_json({"ok": TRANSCODER.cancel(job_id)})

    # ---- 视频流（Range） ----
    def _serve_stream(self, query, head_only=False):
        path = norm_path((query.get("path") or [""])[0])
        if not path or not os.path.isfile(path):
            return self._send_error_json("文件不存在", 404)
        try:
            size = os.path.getsize(path)
        except OSError as exc:
            return self._send_error_json(f"无法读取文件: {exc}", 500)

        ext = os.path.splitext(path)[1].lower()
        content_type = MIME_TYPES.get(ext) or mimetypes.guess_type(path)[0] or "application/octet-stream"
        range_header = self.headers.get("Range")
        start, end = 0, size - 1
        status = 200

        if range_header:
            match = re.match(r"bytes=(\d*)-(\d*)", range_header.strip())
            if match:
                if match.group(1):
                    start = int(match.group(1))
                    if match.group(2):
                        end = min(int(match.group(2)), size - 1)
                elif match.group(2):
                    length = int(match.group(2))
                    start = max(0, size - length)
                if start > end or start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                status = 206

        length = end - start + 1
        self.close_connection = True  # 大文件流式传输后立即关连接，避免 keep-alive 半途被浏览器中止时堆积写错误
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        self.send_header("Connection", "close")
        self.send_header("Cache-Control", "no-store")
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        if head_only:
            return

        chunk_size = (4 * 1024 * 1024) if is_network_path(path) else (512 * 1024)
        stream_key = os.path.normcase(os.path.abspath(path))
        ACTIVE_STREAMS.setdefault(stream_key, set()).add(self)
        try:
            with open(path, "rb") as fh:
                fh.seek(start)
                remaining = length
                while remaining > 0:
                    chunk = fh.read(min(chunk_size, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            # 浏览器中止（换 Range/暂停/关闭页面）是正常行为，静默即可
            pass
        finally:
            handlers = ACTIVE_STREAMS.get(stream_key)
            if handlers:
                handlers.discard(self)
                if not handlers:
                    ACTIVE_STREAMS.pop(stream_key, None)


# --------------------------------------------------------------------------
# 启动
# --------------------------------------------------------------------------

def port_available(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def pick_port(preferred: int = 8765) -> int:
    for port in range(preferred, preferred + 40):
        if port_available(port):
            return port
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def find_running_instance(preferred_port: int, span: int = 4) -> int | None:
    """已经有实例在跑就复用它，避免重复开窗口。"""
    for port in range(preferred_port, preferred_port + span):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/ping", timeout=0.25) as resp:
                if json.loads(resp.read().decode("utf-8")).get("ok"):
                    return port
        except Exception:  # noqa: BLE001
            continue
    return None


BROWSER_ROOT = os.path.join(APP_DATA_DIR, "browser")


def _kill_stale_browsers():
    """清掉之前用我们自己的配置目录启动、可能僵住的 Edge 进程。

    只匹配命令行里带我们 browser 目录的进程，不会影响用户自己的 Edge。
    """
    if os.name != "nt" or not os.path.isdir(BROWSER_ROOT):
        return 0
    script = (
        "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | "
        "Where-Object { $_.CommandLine -like '*VideoCompare*browser*' } | "
        "ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {} }"
    )
    try:
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            timeout=25, creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.TimeoutExpired):
        return 0
    return 1


def _clean_old_profiles(keep_seconds: float = 86400):
    """删除一天前的浏览器配置目录，避免越攒越多。"""
    try:
        now = time.time()
        for name in os.listdir(BROWSER_ROOT):
            path = os.path.join(BROWSER_ROOT, name)
            try:
                if os.path.isdir(path) and now - os.path.getmtime(path) > keep_seconds:
                    shutil.rmtree(path, ignore_errors=True)
            except OSError:
                continue
    except OSError:
        pass


def _new_profile_dir() -> str:
    """每次启动都用一个全新的 Edge 配置目录。

    复用同一个目录时，如果已有一个「以管理员权限运行的 Edge」占着它，
    再启动就会弹「是否用普通权限重启现有实例」，窗口还会白屏。
    """
    os.makedirs(BROWSER_ROOT, exist_ok=True)
    tag = time.strftime("%Y%m%d-%H%M%S") + f"-{os.getpid()}"
    path = os.path.join(BROWSER_ROOT, f"profile-{tag}")
    os.makedirs(path, exist_ok=True)
    # 放一个 First Run 哨兵：否则 Edge 会额外弹「同步确认」的白色窗口盖在界面上面
    try:
        with open(os.path.join(path, "First Run"), "w", encoding="utf-8"):
            pass
    except OSError:
        pass
    return path


def _reset_ping():
    LAST_PING["t"] = 0.0
    LAST_PING["seen"] = False


def _wait_page_ping(timeout: float = 12.0) -> bool:
    """等页面真正发来心跳（说明 UI 真的加载出来了，不是白屏）。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if LAST_PING["seen"] and (time.time() - LAST_PING["t"]) < 8:
            return True
        time.sleep(0.3)
    return False


def wait_for_server(url: str, timeout: float = 8.0) -> bool:
    """确认本地服务真的能响应，再去开窗口。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url + "api/ping", timeout=1.5) as resp:
                if json.loads(resp.read().decode("utf-8")).get("ok"):
                    return True
        except Exception:  # noqa: BLE001
            time.sleep(0.4)
    return False


def _app_windows() -> list[int]:
    """找出属于本程序的可见窗口（标题为页面标题，或尚未加载完时是 127.0.0.1）。

    注意：HWND 是 64 位，ctypes 必须显式声明 argtypes/restype，
    否则句柄会被截断，ShowWindow/SetForegroundWindow 会作用到错误的窗口上。
    """
    if os.name != "nt":
        return []
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
        user32.EnumWindows.restype = wintypes.BOOL
        user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        user32.IsWindowVisible.argtypes = [wintypes.HWND]
        user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]

        handles: list[int] = []
        buf = ctypes.create_unicode_buffer(512)
        rect = wintypes.RECT()
        CB = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def _cb(hwnd, _lparam):
            if not user32.IsWindowVisible(hwnd):
                return True
            user32.GetWindowTextW(hwnd, buf, 512)
            title = buf.value or ""
            if "视频对比工具" in title or "127.0.0.1" in title:
                user32.GetWindowRect(hwnd, ctypes.byref(rect))
                # 太小的（托盘/提示类）忽略
                if (rect.right - rect.left) > 300 and (rect.bottom - rect.top) > 200:
                    handles.append(int(hwnd))
            return True

        user32.EnumWindows(CB(_cb), 0)
        return handles
    except Exception:  # noqa: BLE001
        return []


def _focus_window(hwnd: int) -> bool:
    """还原并置前窗口，返回是否处于正常（非最小化）状态。"""
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        user32.ShowWindow.restype = wintypes.BOOL
        user32.SetForegroundWindow.argtypes = [wintypes.HWND]
        user32.BringWindowToTop.argtypes = [wintypes.HWND]
        user32.IsIconic.argtypes = [wintypes.HWND]

        handle = wintypes.HWND(hwnd)
        if user32.IsIconic(handle):
            user32.ShowWindow(handle, 9)      # SW_RESTORE
        user32.BringWindowToTop(handle)
        user32.SetForegroundWindow(handle)
        return not bool(user32.IsIconic(handle))
    except Exception:  # noqa: BLE001
        return False


def _spawn_edge_app(url: str) -> bool:
    """尝试用 Edge/Chrome 的应用窗口打开，并用页面心跳确认真的加载出来了。"""
    candidates = [
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ]
    _kill_stale_browsers()
    for exe in candidates:
        if not os.path.isfile(exe):
            continue
        profile = _new_profile_dir()
        args = [
            exe,
            f"--app={url}",
            "--window-size=1680,1020",
            f"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            # 本机会话对子进程权限有额外限制，Chromium 的网络/渲染沙箱起不来，
            # 表现为窗口打开但页面一直白屏、连不上 127.0.0.1；关掉沙箱即可正常加载。
            "--no-sandbox",
            "--autoplay-policy=no-user-gesture-required",
            "--disable-features=Translate,MediaRouter,msEdgeSyncConfirmation,SigninAllowed,msImplicitSignin",
        ]
        print(f"[window] 启动 {exe}", flush=True)
        print(f"[window] 配置目录 {profile}", flush=True)
        log_path = os.path.join(profile, "edge-launch.log")
        try:
            log = open(log_path, "wb")
        except OSError:
            log = subprocess.DEVNULL
        _reset_ping()
        try:
            proc = subprocess.Popen(args, creationflags=CREATE_NO_WINDOW,
                                    stdout=log, stderr=subprocess.STDOUT)
        except OSError as exc:
            print(f"[window] 启动失败: {exc}", flush=True)
            continue

        window_ok = False
        for _ in range(40):
            time.sleep(0.4)
            handles = _app_windows()
            if handles and not window_ok:
                restored = _focus_window(handles[0])
                print(f"[window] 窗口 {handles[0]}，还原/置前={restored}", flush=True)
                window_ok = restored
            if LAST_PING["seen"]:
                print("[window] 页面已加载（收到心跳）", flush=True)
                _clean_old_profiles()
                return True
            if proc.poll() is not None and proc.returncode not in (None, 0):
                print(f"[window] 进程退出 code={proc.returncode}", flush=True)
                break
        print("[window] 该方式未能加载界面，结束进程并换方案", flush=True)
        try:
            if proc.poll() is None:
                proc.terminate()
        except OSError:
            pass
        time.sleep(1)
    return False


def _shell_open(url: str) -> bool:
    """用系统 Shell 打开（走用户的默认浏览器，不受本进程权限影响）。"""
    print("[window] 改用系统 Shell 打开默认浏览器", flush=True)
    _reset_ping()
    try:
        if os.name == "nt":
            os.startfile(url)  # noqa: S606  (ShellExecute，等同双击)
        else:
            import webbrowser

            webbrowser.open(url)
    except Exception as exc:  # noqa: BLE001
        print(f"[window] Shell 打开失败: {exc}", flush=True)
        return False
    return _wait_page_ping(15.0)


def open_window(url: str):
    """打开界面：优先独立应用窗口，失败则退回默认浏览器，都不行就弹窗告知地址。"""
    print(f"[window] 目标地址 {url}", flush=True)
    if not wait_for_server(url):
        print("[window] 本地服务没有响应，放弃开窗口", flush=True)
        return False

    if _spawn_edge_app(url):
        return True
    if _shell_open(url):
        print("[window] 已在默认浏览器中打开", flush=True)
        return True

    _message(
        "界面没能自动打开。\n\n"
        f"请在浏览器地址栏手动访问：\n{url}\n\n"
        "（本程序需要保持运行，关闭这个提示后服务仍在后台）"
    )
    return False


def main(argv=None):
    import argparse

    parser = argparse.ArgumentParser(description="视频对比工具")
    parser.add_argument("--port", type=int, default=8765, help="监听端口（默认 8765）")
    parser.add_argument("--no-browser", action="store_true", help="不自动打开窗口")
    args = parser.parse_args(argv)

    # 已经有实例在跑：直接把窗口指过去，不再重复启动
    if not args.no_browser:
        running = find_running_instance(args.port)
        if running:
            url = f"http://127.0.0.1:{running}/"
            print(f"已有实例在运行：{url}", flush=True)
            open_window(url)
            return 0

    try:
        if not FROZEN and os.name == "nt":
            try:
                import ctypes

                ctypes.windll.kernel32.SetConsoleTitleW("视频对比工具")
            except Exception:  # noqa: BLE001
                pass
        port = pick_port(args.port)
        server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    except OSError as exc:
        _message(f"启动失败：{exc}\n\n端口可能被占用，可尝试 --port 指定其它端口。")
        return 1

    server.daemon_threads = True
    url = f"http://127.0.0.1:{port}/"
    print("=" * 60)
    print("  视频对比工具已启动")
    print(f"  地址: {url}")
    print(f"  ffmpeg: {FFMPEG or '未找到（元数据将用内置解析器）'}")
    print("  关闭本窗口或点击界面右上角「退出」即可停止")
    print("=" * 60, flush=True)

    serving = threading.Thread(target=server.serve_forever, daemon=True)
    serving.start()

    try:
        start_drop_zones()
    except Exception:  # noqa: BLE001
        pass

    if not args.no_browser:
        time.sleep(0.4)
        if not open_window(url):
            _message(f"没能自动打开浏览器窗口。\n\n请手动在浏览器访问：\n{url}")

    if FROZEN:
        threading.Thread(target=_ping_watchdog, args=(server,), daemon=True).start()

    try:
        while serving.is_alive():
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n已退出")
    finally:
        try:
            if DROP_ZONES["instance"]:
                DROP_ZONES["instance"].stop()
        except Exception:  # noqa: BLE001
            pass
        try:
            server.shutdown()
        except Exception:  # noqa: BLE001
            pass
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
