"""ffmpeg / ffprobe 封装：定位可执行文件、解析元数据、抽帧、兼容转码。

ffprobe 能读的字段最全，因此只要找到 ffprobe 就优先用它；
找不到时上层会退回到 mediaprobe.py（纯 Python 解析）。
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time

def _app_dir() -> str:
    """exe 打包后是 exe 所在目录；源码运行时是脚本目录。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


# 常见安装位置（含用户指定的 C:\tools\ffmpeg-master-latest-win64-gpl），exe 同目录优先
def _resolve_app_data_dir() -> str:
    """应用自己的可写目录（缓存/日志）。

    本机曾出现「系统没有任何可用临时目录」的情况（tempfile.gettempdir() 直接抛错），
    所以这里完全不碰系统临时目录，按顺序找第一个真正可写的位置。
    """
    exe_dir = _app_dir()
    candidates = [
        os.environ.get("LOCALAPPDATA"),
        os.environ.get("APPDATA"),
        os.environ.get("USERPROFILE"),
        exe_dir,
        os.getcwd(),
    ]
    for base in candidates:
        if not base or not os.path.isabs(base):
            continue
        target = os.path.join(base, "VideoCompare")
        try:
            os.makedirs(target, exist_ok=True)
            probe = os.path.join(target, ".write-probe")
            with open(probe, "w", encoding="utf-8") as fh:
                fh.write("ok")
            os.remove(probe)
            return target
        except OSError:
            continue
    return exe_dir


APP_DATA_DIR = _resolve_app_data_dir()

_SEARCH_DIRS = [
    os.path.join(_app_dir(), "ffmpeg", "bin"),
    os.path.join(_app_dir(), "ffmpeg"),
    os.path.join(_app_dir(), "bin"),
    _app_dir(),
    r"C:\tools\ffmpeg-master-latest-win64-gpl\bin",
    r"C:\tools\ffmpeg\bin",
    r"C:\ffmpeg\bin",
    r"C:\Program Files\ffmpeg\bin",
]

_CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def _find_tool(name: str, env_var: str) -> str | None:
    env = os.environ.get(env_var)
    if env and os.path.isfile(env):
        return env
    exe = name + (".exe" if os.name == "nt" else "")
    custom_dir = os.environ.get("VC_FFMPEG_DIR")
    if custom_dir:
        candidate = os.path.join(custom_dir, exe)
        if os.path.isfile(candidate):
            return candidate
        candidate = os.path.join(custom_dir, "bin", exe)
        if os.path.isfile(candidate):
            return candidate
    found = shutil.which(name)
    if found:
        return found
    for directory in _SEARCH_DIRS:
        candidate = os.path.join(directory, exe)
        if os.path.isfile(candidate):
            return candidate
    return None


FFPROBE = _find_tool("ffprobe", "FFPROBE")
FFMPEG = _find_tool("ffmpeg", "FFMPEG")


def has_ffprobe() -> bool:
    return bool(FFPROBE)


def has_ffmpeg() -> bool:
    return bool(FFMPEG)


def _run(cmd, timeout=30):
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        creationflags=_CREATE_NO_WINDOW,
    )


# --------------------------------------------------------------------------
# 名称美化
# --------------------------------------------------------------------------

VIDEO_NAMES = {
    "h264": "H.264 / AVC",
    "hevc": "H.265 / HEVC",
    "h265": "H.265 / HEVC",
    "vp8": "VP8",
    "vp9": "VP9",
    "av1": "AV1",
    "mpeg4": "MPEG-4 Part 2",
    "mpeg2video": "MPEG-2",
    "mpeg1video": "MPEG-1",
    "prores": "Apple ProRes",
    "dnxhd": "Avid DNxHD/HR",
    "mjpeg": "Motion JPEG",
    "rawvideo": "未压缩视频",
    "theora": "Theora",
    "vc1": "VC-1",
    "wmv3": "WMV9 / VC-1",
    "msmpeg4v3": "MS MPEG-4 v3",
    "h263": "H.263",
    "flv1": "Sorenson H.263",
    "huffyuv": "HuffYUV 无损",
    "ffv1": "FFV1 无损",
    "cfhd": "CineForm",
    "dvvideo": "DV",
    "vvc": "H.266 / VVC",
    "hap": "Hap",
}

AUDIO_NAMES = {
    "aac": "AAC",
    "mp3": "MP3",
    "mp2": "MP2",
    "ac3": "AC-3 (Dolby Digital)",
    "eac3": "E-AC-3 (Dolby Digital Plus)",
    "ac4": "AC-4",
    "dts": "DTS",
    "truehd": "Dolby TrueHD",
    "flac": "FLAC 无损",
    "opus": "Opus",
    "vorbis": "Vorbis",
    "alac": "ALAC 无损",
    "pcm_s16le": "PCM 16bit",
    "pcm_s24le": "PCM 24bit",
    "pcm_s32le": "PCM 32bit",
    "pcm_f32le": "PCM 浮点 32bit",
    "pcm_mulaw": "PCM μ-law",
    "pcm_alaw": "PCM A-law",
    "amr_nb": "AMR-NB",
    "wmav2": "WMA v2",
    "speex": "Speex",
}

CHANNEL_LAYOUT_CN = {
    "mono": "单声道 (1.0)",
    "stereo": "立体声 (2.0)",
    "2.1": "2.1 声道",
    "3.0": "3.0 声道",
    "4.0": "4.0 声道",
    "5.0": "5.0 声道",
    "5.1": "5.1 声道",
    "5.1(side)": "5.1 声道",
    "6.1": "6.1 声道",
    "7.1": "7.1 声道",
    "7.1(wide)": "7.1 声道",
    "downmix": "下混",
}

LANG_CN = {
    "und": "未标注",
    "en": "英语",
    "zh": "中文",
    "ja": "日语",
    "ko": "韩语",
    "fr": "法语",
    "de": "德语",
    "es": "西班牙语",
    "ru": "俄语",
    "ita": "意大利语",
}

WEB_SAFE_VIDEO = {"h264", "vp8", "vp9", "av1", "theora"}
WEB_SAFE_AUDIO = {"aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le"}
WEB_SAFE_CONTAINERS = {"mp4", "mov", "webm", "matroska", "m4a", "3gp"}

HDR_TRANSFERS = {"smpte2084": "HDR10 (PQ)", "arib-std-b67": "HLG"}


def _clean_tag(value) -> str:
    """mp4 的 fourcc 有意义；mkv/avi 常见 [0][0][0][0] 之类的占位，过滤掉。"""
    tag = (value or "").strip()
    if not tag or "[" in tag:
        return ""
    return tag


def _num(value, default=None):
    try:
        if value in (None, "", "N/A"):
            return default
        return float(value)
    except (TypeError, ValueError):
        return default


def _int(value, default=None):
    f = _num(value)
    return int(f) if f is not None else default


def _fraction(value):
    """'30000/1001' → 29.97"""
    if not value or value in ("0/0", "N/A"):
        return None
    if "/" in value:
        num, _, den = value.partition("/")
        try:
            den_f = float(den)
            return float(num) / den_f if den_f else None
        except ValueError:
            return None
    return _num(value)


LEVELED_CODECS = {"h264", "hevc", "h265", "av1", "vvc", "mpeg2video", "vc1", "vp9", "vp8"}


def _codec_level(codec: str, level) -> str | None:
    lv = _int(level)
    if lv is None or lv < 0 or codec not in LEVELED_CODECS:
        return None
    if codec == "h264":
        return f"{lv / 10:g}"
    if codec in ("hevc", "h265"):
        return f"{lv / 30:g}"
    return f"{lv}"


CONTAINER_LABELS = {
    "mov": "MP4",
    "mp4": "MP4",
    "m4a": "M4A",
    "matroska": "Matroska (MKV)",
    "webm": "WebM",
    "avi": "AVI",
    "mpegts": "MPEG-TS",
    "mpeg": "MPEG-PS",
    "flv": "FLV",
    "asf": "ASF / WMV",
    "ogg": "Ogg",
    "3gp": "3GPP",
    "wav": "WAV",
    "mxf": "MXF",
    "rm": "RealMedia",
    "dv": "DV",
}

LOSSLESS_AUDIO = {"flac", "alac", "truehd", "mlp"}


def _container_label(fmt_names: list[str], tags: dict, path: str = "") -> str:
    names = [n for n in fmt_names if n]
    major_raw = (tags.get("major_brand") or "")
    major = major_raw.strip()
    ext = os.path.splitext(path)[1].lower()
    if "matroska" in names or "webm" in names:
        # ffprobe 对 mkv/webm 都报 "matroska,webm"，用扩展名与 DocType 区分
        if ext == ".webm":
            return "WebM"
        if ext in (".mkv", ".mka", ".mk3d"):
            return "Matroska (MKV)"
        return "WebM" if "webm" in names and "matroska" not in names else "Matroska (MKV)"
    if "mov" in names or "mp4" in names:
        if major == "qt":
            return "QuickTime MOV"
        if major.startswith("M4V"):
            return "M4V"
        if major.startswith("3g") or ext in (".3gp", ".3g2"):
            return "3GPP"
        return "MP4"
    if names:
        return CONTAINER_LABELS.get(names[0], names[0].upper())
    return "未知容器"


def _bit_depth(stream, pix_fmt: str | None) -> int | None:
    for key in ("bits_per_raw_sample", "bits_per_sample"):
        val = _int(stream.get(key))
        if val and val > 0:
            return val
    if pix_fmt:
        m = re.search(r"p(?:(\d+))?(?:le|be)?$", pix_fmt)
        if m and m.group(1) and int(m.group(1)) > 8:
            return int(m.group(1))
        if "10" in pix_fmt:
            return 10
        if "12" in pix_fmt:
            return 12
        if "16" in pix_fmt:
            return 16
        if "9" in pix_fmt:
            return 9
        return 8
    return None


def _color_desc(video: dict) -> str | None:
    transfer = (video.get("color_transfer") or "").lower()
    primaries = (video.get("color_primaries") or "").lower()
    space = (video.get("color_space") or "").lower()
    parts = []
    hdr = HDR_TRANSFERS.get(transfer)
    if hdr:
        parts.append(hdr)
    if primaries.startswith("bt2020"):
        parts.append("BT.2020 广色域")
    elif primaries.startswith("bt709"):
        parts.append("BT.709")
    if space.startswith("bt2020"):
        pass
    elif space in ("bt709", "smpte170m", "bt470bg"):
        pass
    if not parts:
        return None
    return " / ".join(dict.fromkeys(parts))


def is_network_path(path: str) -> bool:
    r"""UNC 路径（\\server\share\...）视为网络路径。"""
    return bool(path) and (path.startswith("\\\\") or path.startswith("//"))


def probe_ffprobe(path: str, timeout: float = 30) -> dict | None:
    """调用 ffprobe 得到结构化元数据；失败返回 None。"""
    if not FFPROBE:
        return None
    cmd = [
        FFPROBE, "-v", "error", "-print_format", "json",
        "-show_format", "-show_streams", "-show_chapters", path,
    ]
    try:
        proc = _run(cmd, timeout=timeout)
    except (subprocess.TimeoutExpired, OSError):
        return None
    if proc.returncode != 0 or not proc.stdout:
        return None
    try:
        data = json.loads(proc.stdout.decode("utf-8", "replace"))
    except json.JSONDecodeError:
        return None

    fmt = data.get("format") or {}
    streams = data.get("streams") or []
    fmt_names = (fmt.get("format_name") or "").split(",")
    fmt_name = fmt_names[0]
    fmt_tags = fmt.get("tags") or {}
    duration = _num(fmt.get("duration"))
    size = _int(fmt.get("size"))

    result = {
        "ok": True,
        "engine": "ffprobe",
        "container": _container_label(fmt_names, fmt_tags, path),
        "container_id": fmt_name,
        "format_long": fmt.get("format_long_name"),
        "major_brand": (fmt_tags.get("major_brand") or "").strip() or None,
        "duration": duration,
        "size": size,
        "bitrate_total": _int(fmt.get("bit_rate")),
        "stream_count": _int(fmt.get("nb_streams")),
        "tags": fmt_tags,
        "chapters": len(data.get("chapters") or []),
        "warnings": [],
        "video": None,
        "audio": None,
        "streams": [],
    }

    audio_streams = []
    for st in streams:
        codec_type = st.get("codec_type")
        codec = (st.get("codec_name") or "").lower()
        tags = st.get("tags") or {}
        entry = {
            "index": st.get("index"),
            "type": codec_type,
            "codec": codec,
            "codec_tag": _clean_tag(st.get("codec_tag_string")),
        }
        result["streams"].append(entry)
        if codec_type == "video" and not st.get("disposition", {}).get("attached_pic"):
            if result["video"] is not None:
                continue
            pix_fmt = st.get("pix_fmt")
            rotation = None
            for side in st.get("side_data_list") or []:
                if "rotation" in side:
                    rotation = abs(_int(side.get("rotation")) or 0) % 360
            if rotation is None:
                rotation = _int(tags.get("rotate"))
                rotation = rotation % 360 if rotation else None
            width = _int(st.get("width"))
            height = _int(st.get("height"))
            disp_w, disp_h = width, height
            if rotation in (90, 270) and width and height:
                disp_w, disp_h = height, width
            result["video"] = {
                "codec": VIDEO_NAMES.get(codec, codec.upper() or "未知"),
                "codec_id": codec,
                "codec_tag": _clean_tag(st.get("codec_tag_string")),
                "profile": st.get("profile"),
                "level": _codec_level(codec, st.get("level")),
                "pix_fmt": pix_fmt,
                "width": width,
                "height": height,
                "display_width": disp_w,
                "display_height": disp_h,
                "display_aspect": st.get("display_aspect_ratio"),
                "sample_aspect": st.get("sample_aspect_ratio"),
                "fps": _fraction(st.get("r_frame_rate")) or _fraction(st.get("avg_frame_rate")),
                "avg_fps": _fraction(st.get("avg_frame_rate")),
                "bitrate": _int(st.get("bit_rate")),
                "frames": _int(st.get("nb_frames")),
                "bit_depth": _bit_depth(st, pix_fmt),
                "rotation": rotation or 0,
                "color_space": st.get("color_space"),
                "color_transfer": st.get("color_transfer"),
                "color_primaries": st.get("color_primaries"),
                "color_range": st.get("color_range"),
                "color_desc": _color_desc(st),
                "field_order": st.get("field_order"),
                "duration": _num(st.get("duration")),
                "language": tags.get("language"),
                "title": tags.get("title"),
            }
        elif codec_type == "audio":
            channels = _int(st.get("channels"))
            layout = st.get("channel_layout") or ""
            audio_streams.append({
                "index": st.get("index"),
                "codec": AUDIO_NAMES.get(codec, codec.upper() or "未知"),
                "codec_id": codec,
                "codec_tag": _clean_tag(st.get("codec_tag_string")),
                "profile": st.get("profile"),
                "channels": channels,
                "channel_layout": CHANNEL_LAYOUT_CN.get(layout, layout or (f"{channels} 声道" if channels else None)),
                "sample_rate": _int(st.get("sample_rate")),
                "bit_depth": (
                    _bit_depth(st, st.get("sample_fmt"))
                    if (codec.startswith("pcm") or codec in LOSSLESS_AUDIO)
                    else None
                ),
                "bitrate": _int(st.get("bit_rate")),
                "language": tags.get("language"),
                "title": tags.get("title"),
                "duration": _num(st.get("duration")),
            })

    if audio_streams:
        result["audio"] = audio_streams[0]
    result["audio_stream_count"] = len(audio_streams)
    result["subtitle_stream_count"] = sum(1 for s in streams if s.get("codec_type") == "subtitle")
    result["attachment_count"] = sum(1 for s in streams if s.get("codec_type") == "attachment")

    # 浏览器能否直接播放的推测（真正的判定交给前端 error 事件）
    video = result["video"]
    if video:
        video_id = video.get("codec_id") or ""
        audio_id = (result["audio"] or {}).get("codec_id") or ""
        playable = (
            video_id in WEB_SAFE_VIDEO
            and fmt_name in WEB_SAFE_CONTAINERS
            and (not audio_id or audio_id in WEB_SAFE_AUDIO)
        )
        result["browser_playable"] = playable
    else:
        result["browser_playable"] = False

    if not result["duration"] and video and video.get("duration"):
        result["duration"] = video["duration"]
    if result["size"] and result["duration"]:
        result["bitrate_total"] = result["bitrate_total"] or int(result["size"] * 8 / result["duration"])
    if video:
        if not video.get("bitrate"):
            audio_br = (result["audio"] or {}).get("bitrate") or 0
            total = result["bitrate_total"] or 0
            if total > audio_br:
                video["bitrate"] = total - audio_br
        if not video.get("frames") and video.get("fps") and result["duration"]:
            video["frames"] = int(round(video["fps"] * result["duration"]))
        if video.get("fps"):
            video["fps"] = round(video["fps"], 4)
        if video.get("avg_fps"):
            video["avg_fps"] = round(video["avg_fps"], 4)
    return result


# --------------------------------------------------------------------------
# 抽帧（缩略图 / 帧预览）
# --------------------------------------------------------------------------

def grab_frame_jpeg(path: str, seconds: float = 0.0, width: int = 960, quality: int = 4) -> bytes | None:
    if not FFMPEG:
        return None
    timeout = 120 if is_network_path(path) else 45
    cmd = [
        FFMPEG, "-v", "error", "-nostdin",
        "-ss", f"{max(0.0, seconds):.3f}",
        "-i", path,
        "-frames:v", "1",
        "-vf", f"scale='min({width},iw)':-2",
        "-q:v", str(quality),
        "-f", "image2", "-c:v", "mjpeg", "pipe:1",
    ]
    try:
        proc = _run(cmd, timeout=timeout)
    except (subprocess.TimeoutExpired, OSError):
        return None
    return proc.stdout or None


# --------------------------------------------------------------------------
# 兼容转码（把浏览器不支持的编码转成 H.264 + AAC 的 MP4）
# --------------------------------------------------------------------------

CACHE_DIR = os.path.join(APP_DATA_DIR, "cache")
try:
    os.makedirs(CACHE_DIR, exist_ok=True)
except OSError:
    CACHE_DIR = ""  # 目录不可写时不影响主程序，只是不能转码


class TranscodeJob:
    def __init__(self, path: str, out_path: str, duration: float | None,
                 start: float = 0.0, window: float | None = None):
        self.path = path
        self.out_path = out_path
        self.duration = window or duration   # 进度按实际转码时长计算
        self.start = max(0.0, float(start or 0.0))
        self.window = window
        self.state = "queued"
        self.progress = 0.0
        self.message = ""
        self.started = time.time()
        self.proc: subprocess.Popen | None = None
        self.thread: threading.Thread | None = None

    def as_dict(self):
        return {
            "jobId": getattr(self, "_key", ""),
            "start": self.start,
            "window": self.window,
            "state": self.state,
            "progress": round(self.progress, 4),
            "message": self.message,
            "out": self.out_path if self.state == "done" else None,
        }


class TranscodeManager:
    def __init__(self):
        self.jobs: dict[str, TranscodeJob] = {}
        self.lock = threading.Lock()
        self._seq = 0

    def _cache_key(self, path: str, duration: float | None,
                   start: float = 0.0, window: float | None = None) -> str:
        st = os.stat(path)
        raw = f"{os.path.abspath(path)}|{st.st_size}|{int(st.st_mtime)}|v3|{start:.2f}|{window}"
        return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:20]

    def start(self, path: str, duration: float | None,
              start: float = 0.0, window: float | None = None) -> dict:
        if not CACHE_DIR:
            return {"jobId": "", "state": "error", "progress": 0.0,
                    "message": "程序目录不可写，无法使用转码播放（可改用帧预览）", "out": None}
        key = self._cache_key(path, duration, start, window)
        out_dir = os.path.join(CACHE_DIR, key)
        os.makedirs(out_dir, exist_ok=True)
        out_path = os.path.join(out_dir, "transcoded.mp4")
        marker = os.path.join(out_dir, "done")

        with self.lock:
            job = self.jobs.get(key)
            if job and job.state in ("queued", "running"):
                return job.as_dict()
            if os.path.isfile(marker) and os.path.isfile(out_path):
                job = TranscodeJob(path, out_path, duration, start, window)
                job._key = key
                job.state = "done"
                job.progress = 1.0
                self.jobs[key] = job
                return job.as_dict()

            job = TranscodeJob(path, out_path, duration, start, window)
            job._key = key
            self.jobs[key] = job

        job.thread = threading.Thread(target=self._run_job, args=(job, marker), daemon=True)
        job.thread.start()
        return job.as_dict()

    def _run_job(self, job: TranscodeJob, marker: str):
        if not FFMPEG:
            job.state = "error"
            job.message = "未找到 ffmpeg，无法转码"
            return
        job.state = "running"
        job.message = "转码中…"
        cmd = [FFMPEG, "-v", "error", "-nostdin", "-y"]
        if job.start > 0:
            cmd += ["-ss", f"{job.start:.3f}"]
        if job.window:
            cmd += ["-t", f"{job.window:.3f}"]
        cmd += [
            "-i", job.path,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", "scale='min(1920,iw)':'min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2",
            "-movflags", "+faststart",
            "-progress", "pipe:1", "-nostats",
            job.out_path,
        ]
        try:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=_CREATE_NO_WINDOW,
            )
            job.proc = proc
            assert proc.stdout is not None
            for raw in proc.stdout:
                line = raw.decode("utf-8", "replace").strip()
                if line.startswith("out_time_us="):
                    us = _num(line.split("=", 1)[1])
                    if us and job.duration:
                        job.progress = min(0.999, (us / 1e6) / job.duration)
                elif line.startswith("progress=end"):
                    job.progress = 1.0
            code = proc.wait(timeout=30)
            if code == 0 and os.path.isfile(job.out_path) and os.path.getsize(job.out_path) > 1024:
                with open(marker, "w", encoding="utf-8") as fh:
                    fh.write(str(time.time()))
                job.state = "done"
                job.progress = 1.0
                job.message = "转码完成"
            else:
                err = b""
                if proc.stderr is not None:
                    err = proc.stderr.read() or b""
                job.state = "error"
                job.message = "转码失败：" + err.decode("utf-8", "replace").strip().splitlines()[-1][:300] if err else "转码失败"
        except Exception as exc:  # noqa: BLE001
            job.state = "error"
            job.message = f"转码异常: {exc}"

    def status(self, job_id: str) -> dict | None:
        with self.lock:
            job = self.jobs.get(job_id)
        return job.as_dict() if job else None

    def cancel(self, job_id: str) -> bool:
        with self.lock:
            job = self.jobs.get(job_id)
        if job and job.proc and job.state == "running":
            try:
                job.proc.kill()
            except Exception:
                pass
            job.state = "error"
            job.message = "已取消"
            return True
        return False

    def clear_cache(self) -> int:
        removed = 0
        if not CACHE_DIR:
            return 0
        for name in os.listdir(CACHE_DIR):
            target = os.path.join(CACHE_DIR, name)
            try:
                if os.path.isdir(target):
                    shutil.rmtree(target, ignore_errors=True)
                    removed += 1
            except OSError:
                pass
        return removed


TRANSCODER = TranscodeManager()


if __name__ == "__main__":
    import sys

    print("ffprobe:", FFPROBE)
    print("ffmpeg:", FFMPEG)
    for arg in sys.argv[1:]:
        print(json.dumps(probe_ffprobe(arg), ensure_ascii=False, indent=2))
