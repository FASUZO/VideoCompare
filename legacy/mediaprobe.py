"""媒体元数据解析（纯 Python 实现，不依赖 ffmpeg）。

支持容器：
  * MP4 / MOV / M4V / 3GP   —— ISO BMFF（moov/trak/stsd/esds/avcC/hvcC ...）
  * MKV / WebM              —— EBML（Info/Tracks）
  * AVI                     —— RIFF（hdrl/strl/strf）
其余格式或解析缺失的字段，用 OpenCV 兜底（分辨率、帧率、帧数、fourcc）。

对外只暴露两个函数：
    probe(path)          -> dict       完整元数据
    open_frame_reader(p) -> cv2.VideoCapture 的简单封装（用于兜底预览）
"""

from __future__ import annotations

import datetime as _dt
import mmap
import os
import struct

VIDEO_EXTENSIONS = {
    ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".m2ts", ".mts",
    ".mpg", ".mpeg", ".wmv", ".flv", ".3gp", ".3g2", ".ogv", ".asf", ".rmvb",
    ".rm", ".f4v", ".divx", ".vob", ".mxf",
}

# --------------------------------------------------------------------------
# 常用映射表
# --------------------------------------------------------------------------

CONTAINER_NAMES = {
    "isom": "MP4 (ISO 基础媒体)",
    "iso2": "MP4 (ISO 基础媒体)",
    "iso4": "MP4",
    "iso5": "MP4",
    "iso6": "MP4",
    "mp41": "MP4 v1",
    "mp42": "MP4 v2",
    "avc1": "MP4 (AVC)",
    "M4V ": "M4V (Apple)",
    "qt  ": "QuickTime MOV",
    "dash": "MP4 (DASH/分片)",
    "msnv": "MP4 (Sony)",
    "3gp4": "3GPP",
    "3gp5": "3GPP",
    "3g2a": "3GPP2",
    "f4v ": "F4V",
}

MP4_VIDEO_CODECS = {
    "avc1": ("H.264 / AVC", "AVC"),
    "avc2": ("H.264 / AVC", "AVC"),
    "avc3": ("H.264 / AVC", "AVC"),
    "avc4": ("H.264 / AVC", "AVC"),
    "hvc1": ("H.265 / HEVC", "HEVC"),
    "hev1": ("H.265 / HEVC", "HEVC"),
    "hvc2": ("H.265 / HEVC", "HEVC"),
    "dvhe": ("Dolby Vision (HEVC)", "HEVC"),
    "dvh1": ("Dolby Vision (HEVC)", "HEVC"),
    "vp08": ("VP8", "VP8"),
    "vp09": ("VP9", "VP9"),
    "av01": ("AV1", "AV1"),
    "mp4v": ("MPEG-4 Part 2", "MPEG4"),
    "s263": ("H.263", "H263"),
    "h263": ("H.263", "H263"),
    "jpeg": ("Motion JPEG", "MJPEG"),
    "mjpa": ("Motion JPEG", "MJPEG"),
    "mjpb": ("Motion JPEG", "MJPEG"),
    "png ": ("PNG 序列", "PNG"),
    "apch": ("ProRes 422 HQ", "ProRes"),
    "apcn": ("ProRes 422", "ProRes"),
    "apcs": ("ProRes 422 LT", "ProRes"),
    "apco": ("ProRes 422 Proxy", "ProRes"),
    "ap4h": ("ProRes 4444", "ProRes"),
    "ap4x": ("ProRes 4444 XQ", "ProRes"),
    "cvid": ("Cinepak", "Cinepak"),
    "theo": ("Theora", "Theora"),
    "raw ": ("未压缩 RGB", "RAWVIDEO"),
    "2vuy": ("未压缩 YUV 4:2:2", "RAWVIDEO"),
    "v210": ("未压缩 YUV 4:2:2 10bit", "RAWVIDEO"),
    "wmv3": ("WMV9 / VC-1", "VC1"),
    "vc-1": ("VC-1", "VC1"),
}

MP4_AUDIO_CODECS = {
    "mp4a": ("AAC", "AAC"),
    ".mp3": ("MP3", "MP3"),
    "mp3 ": ("MP3", "MP3"),
    "ac-3": ("AC-3 (Dolby Digital)", "AC3"),
    "ec-3": ("E-AC-3 (Dolby Digital Plus)", "EAC3"),
    "ac-4": ("AC-4", "AC4"),
    "opus": ("Opus", "Opus"),
    "Opus": ("Opus", "Opus"),
    "fLaC": ("FLAC", "FLAC"),
    "alac": ("ALAC", "ALAC"),
    "samr": ("AMR-NB", "AMR"),
    "sawb": ("AMR-WB", "AMR"),
    "lpcm": ("PCM 线性", "PCM"),
    "sowt": ("PCM (小端)", "PCM"),
    "twos": ("PCM (大端)", "PCM"),
    "in24": ("PCM 24bit", "PCM"),
    "in32": ("PCM 32bit", "PCM"),
    "fl32": ("PCM 浮点 32bit", "PCM"),
    "fl64": ("PCM 浮点 64bit", "PCM"),
    "raw ": ("PCM (无符号)", "PCM"),
    "ulaw": ("PCM μ-law", "PCM"),
    "alaw": ("PCM A-law", "PCM"),
    "dtsc": ("DTS", "DTS"),
    "dtsh": ("DTS-HD", "DTS"),
    "dtsl": ("DTS-HD MA", "DTS"),
    "dtse": ("DTS Express", "DTS"),
    "mlpa": ("MLP / TrueHD", "TRUEHD"),
}

MKV_VIDEO_CODECS = {
    "V_MPEG4/ISO/AVC": ("H.264 / AVC", "AVC"),
    "V_MPEGH/ISO/HEVC": ("H.265 / HEVC", "HEVC"),
    "V_MPEG4/ISO/ASP": ("MPEG-4 Part 2", "MPEG4"),
    "V_MPEG4/ISO/SP": ("MPEG-4 Part 2", "MPEG4"),
    "V_MPEG4/ISO/AP": ("MPEG-4 Part 2", "MPEG4"),
    "V_MPEG2": ("MPEG-2", "MPEG2"),
    "V_MPEG1": ("MPEG-1", "MPEG1"),
    "V_VP8": ("VP8", "VP8"),
    "V_VP9": ("VP9", "VP9"),
    "V_AV1": ("AV1", "AV1"),
    "V_THEORA": ("Theora", "Theora"),
    "V_PRORES": ("ProRes", "ProRes"),
    "V_MS/VFW/FOURCC": ("VFW 编码", None),
    "V_UNCOMPRESSED": ("未压缩", "RAWVIDEO"),
    "V_QUICKTIME": ("QuickTime 编码", None),
    "V_FFV1": ("FFV1 无损", "FFV1"),
}

MKV_AUDIO_CODECS = {
    "A_AAC": ("AAC", "AAC"),
    "A_AAC/MPEG4/LC": ("AAC-LC", "AAC"),
    "A_AAC/MPEG4/LC/SBR": ("HE-AAC", "AAC"),
    "A_AC3": ("AC-3 (Dolby Digital)", "AC3"),
    "A_EAC3": ("E-AC-3 (Dolby Digital Plus)", "EAC3"),
    "A_DTS": ("DTS", "DTS"),
    "A_TRUEHD": ("TrueHD", "TRUEHD"),
    "A_FLAC": ("FLAC", "FLAC"),
    "A_OPUS": ("Opus", "Opus"),
    "A_VORBIS": ("Vorbis", "Vorbis"),
    "A_MPEG/L3": ("MP3", "MP3"),
    "A_MPEG/L2": ("MP2", "MP2"),
    "A_PCM/INT/LIT": ("PCM (小端)", "PCM"),
    "A_PCM/INT/BIG": ("PCM (大端)", "PCM"),
    "A_PCM/FLOAT/IEEE": ("PCM 浮点", "PCM"),
    "A_ALAC": ("ALAC", "ALAC"),
    "A_MS/ACM": ("ACM 音频", None),
}

AVC_PROFILES = {
    66: "Baseline",
    77: "Main",
    88: "Extended",
    100: "High",
    110: "High 10",
    122: "High 4:2:2",
    244: "High 4:4:4 Predictive",
    44: "CAVLC 4:4:4",
}
AVC_BITDEPTH = {110: 10, 122: 10, 244: 14}

HEVC_PROFILES = {
    1: "Main",
    2: "Main 10",
    3: "Main Still Picture",
    4: "Range Extensions",
    5: "High Throughput",
    9: "Screen Content",
}

AAC_OBJECT_TYPES = {
    1: "AAC Main",
    2: "AAC LC",
    3: "AAC SSR",
    4: "AAC LTP",
    5: "HE-AAC (SBR)",
    6: "AAC Scalable",
    17: "ER AAC LC",
    23: "AAC LD",
    29: "HE-AAC v2 (PS)",
    39: "xHE-AAC",
    42: "USAC",
}

# 采样率索引表（ISO/IEC 14496-3）
AAC_SAMPLE_RATES = [
    96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
    16000, 12000, 11025, 8000, 7350,
]

CHANNEL_LAYOUTS = {
    1: "单声道 (1.0)",
    2: "立体声 (2.0)",
    3: "2.1 声道",
    4: "4.0 声道",
    5: "5.0 声道",
    6: "5.1 声道",
    7: "6.1 声道",
    8: "7.1 声道",
    12: "7.1.4 声道",
}

LANGUAGES = {
    "und": "未标注",
    "en": "英语",
    "zh": "中文",
    "ja": "日语",
    "ko": "韩语",
    "fr": "法语",
    "de": "德语",
    "es": "西班牙语",
    "ru": "俄语",
}


def channel_layout(channels):
    if not channels:
        return None
    return CHANNEL_LAYOUTS.get(int(channels), f"{int(channels)} 声道")


def _codec_level(level_idc, divisor=10.0):
    if not level_idc:
        return None
    try:
        return f"{level_idc / divisor:g}"
    except Exception:
        return None


# --------------------------------------------------------------------------
# 通用工具
# --------------------------------------------------------------------------

def _boxes(data, start, end):
    """遍历一个 ISO BMFF 容器内的直接子 box。"""
    pos = start
    while pos + 8 <= end:
        size = int.from_bytes(data[pos:pos + 4], "big")
        typ = bytes(data[pos + 4:pos + 8]).decode("latin-1", "replace")
        header = 8
        if size == 1:
            if pos + 16 > end:
                return
            size = int.from_bytes(data[pos + 8:pos + 16], "big")
            header = 16
        elif size == 0:
            size = end - pos
        if size < header:
            return
        stop = pos + size
        if stop > end:
            stop = end
        yield typ, pos + header, stop
        pos = stop


def _find_boxes(data, start, end, wanted):
    out = {}
    for typ, s, e in _boxes(data, start, end):
        if typ in wanted:
            out.setdefault(typ, []).append((s, e))
    return out


class _BitReader:
    """按位读取，用于解析 AudioSpecificConfig。"""

    def __init__(self, buf):
        self.buf = bytes(buf)
        self.pos = 0

    def read(self, bits):
        value = 0
        for _ in range(bits):
            byte = self.pos >> 3
            if byte >= len(self.buf):
                raise ValueError("比特流越界")
            bit = (self.buf[byte] >> (7 - (self.pos & 7))) & 1
            value = (value << 1) | bit
            self.pos += 1
        return value


def _parse_audio_specific_config(buf):
    """解析 AAC AudioSpecificConfig → (object_type, sample_rate, channels)。"""
    try:
        br = _BitReader(buf)
        object_type = br.read(5)
        if object_type == 31:
            object_type = 32 + br.read(6)
        freq_index = br.read(4)
        if freq_index == 15:
            sample_rate = br.read(24)
        else:
            sample_rate = AAC_SAMPLE_RATES[freq_index] if freq_index < len(AAC_SAMPLE_RATES) else None
        channels = br.read(4)
        return object_type, sample_rate, channels
    except Exception:
        return None, None, None


# --------------------------------------------------------------------------
# MP4 / MOV
# --------------------------------------------------------------------------

def _mp4_visual_config(tag, payload):
    """从 sample entry 的子 box 中提取 profile / level / 位深。"""
    info = {}
    boxes = {t: (s, e) for t, s, e in _boxes(payload, 0, len(payload))}
    if "avcC" in boxes:
        s, _ = boxes["avcC"]
        raw = payload[s:s + 4]
        if len(raw) >= 4:
            profile_idc = raw[1]
            info["profile"] = AVC_PROFILES.get(profile_idc, f"profile {profile_idc}")
            info["level"] = _codec_level(raw[3], 10.0)
            info["bit_depth"] = AVC_BITDEPTH.get(profile_idc, 8)
    elif "hvcC" in boxes:
        s, _ = boxes["hvcC"]
        raw = payload[s:s + 13]
        if len(raw) >= 13:
            space_tier_profile = raw[1]
            profile_idc = space_tier_profile & 0x1F
            tier = (space_tier_profile >> 5) & 0x01
            info["profile"] = HEVC_PROFILES.get(profile_idc, f"profile {profile_idc}")
            info["level"] = _codec_level(raw[12], 30.0)
            info["bit_depth"] = 10 if profile_idc in (2, 4, 9) else 8
            info["tier"] = "High" if tier else "Main"
    elif "av1C" in boxes:
        s, _ = boxes["av1C"]
        raw = payload[s:s + 3]
        if len(raw) >= 3:
            info["profile"] = f"profile {raw[1] >> 5}"
            level_idx = raw[1] & 0x1F
            info["level"] = f"{2 + (level_idx >> 2)}.{level_idx & 3}"
            info["bit_depth"] = 12 if (raw[2] >> 6) & 1 else (10 if (raw[2] >> 5) & 1 else 8)
    elif "vpcC" in boxes:
        s, _ = boxes["vpcC"]
        raw = payload[s:s + 6]
        if len(raw) >= 6:
            info["profile"] = f"profile {raw[4]}"
            info["level"] = f"{raw[5] / 10:g}"
            info["bit_depth"] = raw[6] >> 4 if len(raw) > 6 else 8
    elif "esds" in boxes:
        s, e = boxes["esds"]
        obj, avg_br = _parse_esds(payload[s:e])
        if obj:
            info["object_type"] = obj
        if avg_br:
            info["bitrate"] = avg_br
    elif "btrt" in boxes:
        s, _ = boxes["btrt"]
        raw = payload[s:s + 12]
        if len(raw) == 12:
            avg = int.from_bytes(raw[8:12], "big")
            if avg:
                info["bitrate"] = avg
    if "btrt" in boxes and "bitrate" not in info:
        s, _ = boxes["btrt"]
        raw = payload[s:s + 12]
        if len(raw) == 12:
            avg = int.from_bytes(raw[8:12], "big")
            if avg:
                info["bitrate"] = avg
    return info


def _parse_esds(payload):
    """ESDS → (objectTypeIndication, avgBitrate)；同时返回 AAC 细节由调用方使用。"""
    obj = None
    avg_br = None
    asc = None
    i = 4  # version + flags
    while i < len(payload):
        tag = payload[i]
        i += 1
        length = 0
        for _ in range(4):
            if i >= len(payload):
                break
            b = payload[i]
            i += 1
            length = (length << 7) | (b & 0x7F)
            if not b & 0x80:
                break
        if tag == 0x03:  # ES_Descriptor
            i += 2  # ES_ID
            flags = payload[i] if i < len(payload) else 0
            i += 1
            if flags & 0x80:
                i += 2
            if flags & 0x40:
                i += 1 + (payload[i] if i < len(payload) else 0)
            if flags & 0x20:
                i += 2
        elif tag == 0x04:  # DecoderConfigDescriptor
            if i + 13 > len(payload):
                break
            obj = payload[i]
            avg_br = int.from_bytes(payload[i + 9:i + 13], "big")
            i += 13
        elif tag == 0x05:  # DecoderSpecificInfo (AudioSpecificConfig)
            asc = payload[i:i + length]
            i += length
        else:
            i += length
    return obj, avg_br, asc


def _mp4_audio_config(tag, payload):
    info = {}
    boxes = {t: (s, e) for t, s, e in _boxes(payload, 0, len(payload))}
    if "esds" in boxes:
        s, e = boxes["esds"]
        obj, avg_br, asc = _parse_esds(payload[s:e])
        if avg_br:
            info["bitrate"] = avg_br
        if obj == 0x40:  # MPEG-4 Audio (AAC)
            if asc:
                otype, srate, chans = _parse_audio_specific_config(asc)
                if otype:
                    info["profile"] = AAC_OBJECT_TYPES.get(otype, f"类型 {otype}")
                    info["codec_name"] = AAC_OBJECT_TYPES.get(otype, "AAC")
                if srate:
                    info["sample_rate"] = srate
                if chans:
                    info["channels_hint"] = chans
        elif obj == 0x6B:
            info["codec_name"] = "MP3"
            info["codec_tag_name"] = "MP3"
        elif obj == 0x69:
            info["codec_name"] = "MP3 (MPEG-2)"
        elif obj == 0xA5:
            info["codec_name"] = "AC-3"
    elif "dOps" in boxes:
        s, _ = boxes["dOps"]
        raw = payload[s:s + 8]
        if len(raw) >= 8:
            info["channels"] = raw[1]
            info["sample_rate"] = int.from_bytes(raw[4:8], "little")
            info["profile"] = "Opus"
    elif "dac3" in boxes:
        s, _ = boxes["dac3"]
        raw = payload[s:s + 3]
        if len(raw) == 3:
            bits = int.from_bytes(raw, "big")
            fscod = (bits >> 22) & 0x03
            acmod = (bits >> 11) & 0x07
            info["sample_rate"] = {0: 48000, 1: 44100, 2: 32000}.get(fscod)
            info["channels"] = {0: 2, 1: 1, 2: 2, 3: 3, 4: 3, 5: 4, 6: 4, 7: 5}.get(acmod)
    elif "dfLa" in boxes:
        s, e = boxes["dfLa"]
        raw = payload[s + 4:e]  # 跳过 version/flags，跳过 METADATA_BLOCK_HEADER 后是 STREAMINFO
        idx = raw.find(b"fLaC")
        if idx >= 0:
            stream = raw[idx + 4 + 4:idx + 4 + 4 + 34]
            if len(stream) >= 18:
                v = int.from_bytes(stream[10:18], "big")
                sample_rate = (v >> 44) & 0xFFFFF
                channels = ((v >> 41) & 0x07) + 1
                bit_depth = ((v >> 36) & 0x1F) + 1
                info["sample_rate"] = sample_rate
                info["channels"] = channels
                info["bit_depth"] = bit_depth
    return info


def _mp4_parse_track(data, start, end, mvhd_timescale, mvhd_duration):
    track = {"type": None, "warnings": []}
    boxes = _find_boxes(data, start, end, {"tkhd", "mdia", "edts", "udta"})
    if "tkhd" in boxes:
        s, e = boxes["tkhd"][0]
        version = data[s]
        if e - s >= 8:
            width = int.from_bytes(data[e - 8:e - 4], "big") / 65536.0
            height = int.from_bytes(data[e - 4:e], "big") / 65536.0
            if width:
                track["width"] = round(width, 2)
            if height:
                track["height"] = round(height, 2)
        matrix_start = e - 44
        if matrix_start > s:
            a = int.from_bytes(data[matrix_start:matrix_start + 4], "big", signed=True) / 65536.0
            b = int.from_bytes(data[matrix_start + 4:matrix_start + 8], "big", signed=True) / 65536.0
            import math
            angle = round(math.degrees(math.atan2(b, a)))
            angle = (angle + 360) % 360
            if angle in (90, 180, 270):
                track["rotation"] = angle
        flags = int.from_bytes(bytes(data[s + 1:s + 4]), "big")
        track["enabled"] = bool(flags & 0x1)

    if "mdia" not in boxes:
        return track
    mstart, mend = boxes["mdia"][0]
    mdia = _find_boxes(data, mstart, mend, {"mdhd", "hdlr", "minf"})

    timescale = None
    duration = None
    if "mdhd" in mdia:
        s, e = mdia["mdhd"][0]
        version = data[s]
        if version == 1:
            timescale = int.from_bytes(data[s + 20:s + 24], "big")
            duration = int.from_bytes(data[s + 24:s + 32], "big")
            lang_off = s + 32
        else:
            timescale = int.from_bytes(data[s + 12:s + 16], "big")
            duration = int.from_bytes(data[s + 16:s + 20], "big")
            lang_off = s + 20
        if lang_off + 2 <= e:
            packed = int.from_bytes(data[lang_off:lang_off + 2], "big")
            lang = "".join(chr(0x60 + ((packed >> shift) & 0x1F)) for shift in (10, 5, 0))
            track["language"] = lang

    if "hdlr" in mdia:
        s, e = mdia["hdlr"][0]
        if e - s >= 12:
            track["type"] = bytes(data[s + 8:s + 12]).decode("latin-1", "replace").strip()

    if "minf" in mdia:
        m2s, m2e = mdia["minf"][0]
        minf = _find_boxes(data, m2s, m2e, {"stbl"})
        if "stbl" in minf:
            s2, e2 = minf["stbl"][0]
            stbl = _find_boxes(data, s2, e2, {"stsd", "stts", "stsz", "stz2", "stsc"})
            track.update(_mp4_parse_stbl(data, stbl, track.get("type")))

    if duration and timescale:
        track["duration"] = duration / timescale
    elif mvhd_duration and mvhd_timescale:
        track["duration"] = mvhd_duration / mvhd_timescale
    track["timescale"] = timescale
    return track


def _mp4_parse_stbl(data, stbl, handler):
    out = {}
    if "stsd" in stbl:
        s, e = stbl["stsd"][0]
        count = int.from_bytes(data[s + 4:s + 8], "big")
        pos = s + 8
        for _ in range(max(0, min(count, 8))):
            if pos + 8 > e:
                break
            size = int.from_bytes(data[pos:pos + 4], "big")
            fmt = bytes(data[pos + 4:pos + 8]).decode("latin-1", "replace")
            entry_end = min(pos + size, e)
            payload = data[pos + 8:entry_end]
            out["codec_tag"] = fmt
            if handler in ("vide", None):
                if len(payload) >= 78:
                    w = int.from_bytes(payload[24:26], "big")
                    h = int.from_bytes(payload[26:28], "big")
                    if w:
                        out["stsd_width"] = w
                    if h:
                        out["stsd_height"] = h
                    out["bit_depth_hint"] = int.from_bytes(payload[74:76], "big") or None
                    child_start = 78
                    out.update(_mp4_visual_config(fmt, payload[child_start:]))
            else:
                if len(payload) >= 28:
                    version = int.from_bytes(payload[8:10], "big")
                    out["channels"] = int.from_bytes(payload[16:18], "big")
                    out["sample_size"] = int.from_bytes(payload[18:20], "big")
                    sr = int.from_bytes(payload[24:28], "big") >> 16
                    if sr:
                        out["sample_rate"] = sr
                    child_start = 28 if version == 0 else (44 if version == 1 else 64)
                    out.update(_mp4_audio_config(fmt, payload[child_start:]))
            pos = entry_end

    frames = None
    if "stts" in stbl:
        s, e = stbl["stts"][0]
        entry_count = int.from_bytes(data[s + 4:s + 8], "big")
        total = 0
        for i in range(min(entry_count, 100000)):
            base = s + 8 + i * 8
            if base + 8 > e:
                break
            total += int.from_bytes(data[base:base + 4], "big")
        if total:
            frames = total
            out["sample_count"] = total

    sizes_total = 0
    if "stsz" in stbl:
        s, e = stbl["stsz"][0]
        sample_size = int.from_bytes(data[s + 4:s + 8], "big")
        sample_count = int.from_bytes(data[s + 8:s + 12], "big")
        if frames is None:
            frames = sample_count
        if sample_size:
            sizes_total = sample_size * sample_count
        else:
            limit = min(sample_count, 5_000_000)
            base = s + 12
            avail = min(limit, max(0, (e - base) // 4))
            if avail:
                sizes_total = sum(int.from_bytes(data[base + i * 4:base + i * 4 + 4], "big") for i in range(avail))
                if avail < sample_count and sample_count:
                    sizes_total = int(sizes_total * sample_count / avail)
    if frames:
        out["frames"] = frames
    if sizes_total:
        out["bytes"] = sizes_total
    return out


def probe_mp4(path, data, end):
    info = {"warnings": [], "engine": "python-mp4"}
    moov = None
    major = None
    compat = []
    for typ, s, e in _boxes(data, 0, end):
        if typ == "ftyp":
            major = bytes(data[s:s + 4]).decode("latin-1", "replace")
            compat = [bytes(data[i:i + 4]).decode("latin-1", "replace") for i in range(s + 8, e, 4)]
        elif typ == "moov":
            moov = (s, e)
        elif typ == "moof":
            info["fragmented"] = True
    if moov is None:
        info["warnings"].append("未找到 moov（可能是不完整或分片文件）")
        return info

    mstart, mend = moov
    moov_boxes = _find_boxes(data, mstart, mend, {"mvhd", "trak", "mvex"})
    timescale = duration = None
    if "mvhd" in moov_boxes:
        s, e = moov_boxes["mvhd"][0]
        version = data[s]
        if version == 1:
            timescale = int.from_bytes(data[s + 20:s + 24], "big")
            duration = int.from_bytes(data[s + 24:s + 32], "big")
        else:
            timescale = int.from_bytes(data[s + 12:s + 16], "big")
            duration = int.from_bytes(data[s + 16:s + 20], "big")
        info["timescale"] = timescale
        if duration:
            info["duration"] = duration / timescale
    if "mvex" in moov_boxes:
        info["fragmented"] = True

    tracks = []
    for s, e in moov_boxes.get("trak", []):
        tracks.append(_mp4_parse_track(data, s, e, timescale, duration))
    info["tracks"] = tracks

    video = next((t for t in tracks if t.get("type") == "vide"), None)
    audio = next((t for t in tracks if t.get("type") == "soun"), None)

    if video:
        tag = video.get("codec_tag") or ""
        name, family = MP4_VIDEO_CODECS.get(tag, (f"未知编码 ({tag})", None))
        if video.get("object_type"):
            video["codec_object_type"] = video["object_type"]
        width = video.get("stsd_width") or video.get("width")
        height = video.get("stsd_height") or video.get("height")
        rotation = video.get("rotation") or 0
        if rotation in (90, 270) and width and height:
            width, height = height, width
        duration_s = video.get("duration")
        frames = video.get("frames")
        fps = None
        if frames and duration_s:
            fps = frames / duration_s
        info["video"] = {
            "codec": name,
            "codec_tag": tag.strip(),
            "codec_family": family,
            "profile": video.get("profile"),
            "level": video.get("level"),
            "width": int(width) if width else None,
            "height": int(height) if height else None,
            "fps": fps,
            "frames": frames,
            "bitrate": video.get("bitrate") or (
                int(video["bytes"] * 8 / duration_s) if video.get("bytes") and duration_s else None
            ),
            "bit_depth": video.get("bit_depth") or video.get("bit_depth_hint"),
            "rotation": rotation,
            "duration": duration_s,
        }
    if audio:
        tag = audio.get("codec_tag") or ""
        name, family = MP4_AUDIO_CODECS.get(tag, (f"未知编码 ({tag})", None))
        if audio.get("codec_name"):
            name = audio["codec_name"]
        channels = audio.get("channels_hint") or audio.get("channels")
        sample_rate = audio.get("sample_rate")
        duration_s = audio.get("duration") or info.get("duration")
        bitrate = audio.get("bitrate")
        if not bitrate and audio.get("bytes") and duration_s:
            bitrate = int(audio["bytes"] * 8 / duration_s)
        info["audio"] = {
            "codec": name,
            "codec_tag": tag.strip(),
            "codec_family": family,
            "profile": audio.get("profile"),
            "channels": int(channels) if channels else None,
            "channel_layout": channel_layout(channels),
            "sample_rate": int(sample_rate) if sample_rate else None,
            "bit_depth": audio.get("bit_depth") or (audio.get("sample_size") if (audio.get("sample_size") or 0) > 8 else None),
            "bitrate": bitrate,
            "language": audio.get("language"),
        }

    container = CONTAINER_NAMES.get(major or "", None)
    if container is None:
        container = "QuickTime MOV" if (major == "qt  ") else f"MP4 容器 ({major or '未知'})"
    info["container"] = container
    info["major_brand"] = major
    info["compatible_brands"] = [b for b in compat if b.strip()]
    return info


# --------------------------------------------------------------------------
# MKV / WebM（EBML）
# --------------------------------------------------------------------------

EBML_HEADER = 0x1A45DFA3
SEGMENT = 0x18538067
INFO = 0x1549A966
TRACKS = 0x1654AE6B
TRACK_ENTRY = 0xAE
CLUSTER = 0x1F43B675

MASTER_ELEMENTS = {
    SEGMENT, INFO, TRACKS, TRACK_ENTRY, 0xE0, 0xE1, 0x55B0, 0x1F43B675, 0x1254C367,
    0x1043A770, 0x1941A469, 0x7373, 0x63C0, 0x67C8, 0x6D80, 0x6240, 0x6924,
}


def _read_element_id(data, pos):
    first = data[pos]
    mask = 0x80
    length = 1
    while length <= 4 and not (first & mask):
        mask >>= 1
        length += 1
    if length > 4:
        raise ValueError("EBML ID 过长")
    return int.from_bytes(data[pos:pos + length], "big"), length


def _read_element_size(data, pos):
    first = data[pos]
    mask = 0x80
    length = 1
    while length <= 8 and not (first & mask):
        mask >>= 1
        length += 1
    if length > 8:
        raise ValueError("EBML 尺寸过长")
    value = first & (mask - 1)
    unknown = value == (mask - 1)
    for i in range(1, length):
        b = data[pos + i]
        value = (value << 8) | b
        unknown = unknown and (b == 0xFF)
    return value, length, unknown


def _ebml_int(data, s, e):
    raw = bytes(data[s:e])
    if not raw:
        return None
    return int.from_bytes(raw, "big", signed=False)


def _ebml_float(data, s, e):
    raw = bytes(data[s:e])
    if len(raw) == 4:
        return struct.unpack(">f", raw)[0]
    if len(raw) == 8:
        return struct.unpack(">d", raw)[0]
    return None


def _ebml_str(data, s, e):
    return bytes(data[s:e]).decode("utf-8", "replace").strip("\x00").strip()


def probe_mkv(path, data, end):
    info = {"warnings": [], "engine": "python-mkv"}
    pos = 0
    doc_type = "matroska"
    try:
        # EBML 头
        eid, ilen = _read_element_id(data, pos)
        if eid != EBML_HEADER:
            raise ValueError("不是 EBML 文件")
        size, slen, unknown = _read_element_size(data, pos + ilen)
        pos = pos + ilen + slen + (0 if unknown else size)
        seg_start = seg_end = None
        while pos + 4 < end:
            eid, ilen = _read_element_id(data, pos)
            size, slen, unknown = _read_element_size(data, pos + ilen)
            body = pos + ilen + slen
            if eid == SEGMENT:
                seg_start = body
                seg_end = end if unknown else min(body + size, end)
                break
            if unknown:
                break
            pos = body + size
        if seg_start is None:
            info["warnings"].append("未找到 Segment")
            return info
        info["doc_type"] = doc_type
        tracks = []
        current = None
        stack = [(seg_start, seg_end)]
        timecode_scale = 1_000_000
        duration_ticks = None
        while stack:
            start, stop = stack.pop()
            p = start
            while p + 2 < stop:
                try:
                    eid, ilen = _read_element_id(data, p)
                    size, slen, unknown = _read_element_size(data, p + ilen)
                except (ValueError, IndexError):
                    break
                body = p + ilen + slen
                elem_end = stop if unknown else min(body + size, stop)
                if eid == CLUSTER:
                    p = elem_end if not unknown else stop
                    continue
                if eid in (0x2AD7B1,):
                    timecode_scale = _ebml_int(data, body, elem_end) or timecode_scale
                elif eid == 0x4489:
                    duration_ticks = _ebml_float(data, body, elem_end)
                elif eid == 0x4282:
                    doc_type = _ebml_str(data, body, elem_end)
                elif eid == 0x7BA9:
                    info["title"] = _ebml_str(data, body, elem_end)
                elif eid == 0x4D80:
                    info["muxing_app"] = _ebml_str(data, body, elem_end)
                elif eid == 0x5741:
                    info["writing_app"] = _ebml_str(data, body, elem_end)
                elif eid == TRACK_ENTRY:
                    current = {"warnings": []}
                    tracks.append(current)
                    stack.append((body, elem_end))
                elif current is not None and eid in MASTER_ELEMENTS and eid != TRACK_ENTRY:
                    stack.append((body, elem_end))
                elif current is not None:
                    _mkv_track_field(current, eid, data, body, elem_end)
                p = elem_end
                if unknown:
                    break
        if duration_ticks:
            info["duration"] = duration_ticks * timecode_scale / 1e9
        info["container"] = "WebM" if doc_type == "webm" else "Matroska (MKV)"
        info["tracks"] = tracks
    except Exception as exc:  # noqa: BLE001
        info["warnings"].append(f"MKV 解析失败: {exc}")
        return info

    video = next((t for t in tracks if t.get("track_type") == 1), None)
    audio = next((t for t in tracks if t.get("track_type") == 2), None)
    if video:
        codec_id = video.get("codec_id") or ""
        name, family = MKV_VIDEO_CODECS.get(codec_id, (codec_id or "未知编码", None))
        profile = level = bit_depth = None
        if codec_id == "V_MPEG4/ISO/AVC" and video.get("codec_private"):
            cp = video["codec_private"]
            if len(cp) >= 4:
                profile = AVC_PROFILES.get(cp[1], f"profile {cp[1]}")
                level = _codec_level(cp[3])
                bit_depth = AVC_BITDEPTH.get(cp[1], 8)
        elif codec_id == "V_MPEGH/ISO/HEVC" and video.get("codec_private"):
            cp = video["codec_private"]
            if len(cp) >= 13:
                profile = HEVC_PROFILES.get(cp[1] & 0x1F, f"profile {cp[1] & 0x1F}")
                level = _codec_level(cp[12], 30.0)
                bit_depth = 10 if (cp[1] & 0x1F) in (2, 4, 9) else 8
        fps = None
        if video.get("default_duration"):
            fps = 1e9 / video["default_duration"]
        duration_s = info.get("duration")
        info["video"] = {
            "codec": name,
            "codec_tag": codec_id,
            "codec_family": family,
            "profile": profile,
            "level": level,
            "width": video.get("pixel_width"),
            "height": video.get("pixel_height"),
            "display_width": video.get("display_width"),
            "display_height": video.get("display_height"),
            "fps": fps,
            "frames": int(round(fps * duration_s)) if fps and duration_s else None,
            "bitrate": video.get("bitrate"),
            "bit_depth": video.get("bit_depth") or bit_depth,
            "interlaced": video.get("interlaced"),
            "color_range": video.get("color_range"),
            "duration": duration_s,
        }
    if audio:
        codec_id = audio.get("codec_id") or ""
        name, family = MKV_AUDIO_CODECS.get(codec_id, (codec_id or "未知编码", None))
        profile = None
        if codec_id.startswith("A_AAC") and audio.get("codec_private"):
            otype, srate, chans = _parse_audio_specific_config(audio["codec_private"])
            if otype:
                profile = AAC_OBJECT_TYPES.get(otype)
            if srate and not audio.get("sample_rate"):
                audio["sample_rate"] = srate
            if chans and not audio.get("channels"):
                audio["channels"] = chans
        channels = audio.get("channels")
        info["audio"] = {
            "codec": name,
            "codec_tag": codec_id,
            "codec_family": family,
            "profile": profile,
            "channels": channels,
            "channel_layout": channel_layout(channels),
            "sample_rate": audio.get("sample_rate") or audio.get("output_sample_rate"),
            "bit_depth": audio.get("bit_depth"),
            "bitrate": audio.get("bitrate"),
            "language": audio.get("language"),
        }
    return info


def _mkv_track_field(track, eid, data, s, e):
    if eid == 0xD7:
        track["track_number"] = _ebml_int(data, s, e)
    elif eid == 0x83:
        track["track_type"] = _ebml_int(data, s, e)
    elif eid == 0x86:
        track["codec_id"] = _ebml_str(data, s, e)
    elif eid == 0x63A2:
        track["codec_private"] = bytes(data[s:e])
    elif eid == 0x23E383:
        track["default_duration"] = _ebml_int(data, s, e)
    elif eid == 0x536E:
        track["name"] = _ebml_str(data, s, e)
    elif eid == 0x22B59C:
        track["language"] = _ebml_str(data, s, e)
    elif eid == 0x22B59D:
        track["language_ietf"] = _ebml_str(data, s, e)
    elif eid == 0xB0:
        track["pixel_width"] = _ebml_int(data, s, e)
    elif eid == 0xBA:
        track["pixel_height"] = _ebml_int(data, s, e)
    elif eid == 0x54B0:
        track["display_width"] = _ebml_int(data, s, e)
    elif eid == 0x54BA:
        track["display_height"] = _ebml_int(data, s, e)
    elif eid == 0x9A:
        track["interlaced"] = _ebml_int(data, s, e)
    elif eid == 0x55B9:
        track["color_range"] = _ebml_int(data, s, e)
    elif eid == 0x55B2:
        track["bit_depth"] = _ebml_int(data, s, e)
    elif eid == 0xB5:
        track["sample_rate"] = _ebml_float(data, s, e)
    elif eid == 0x78B5:
        track["output_sample_rate"] = _ebml_float(data, s, e)
    elif eid == 0x9F:
        track["channels"] = _ebml_int(data, s, e)
    elif eid == 0x6264:
        track["bit_depth"] = _ebml_int(data, s, e)
    elif eid == 0x4489:
        track["duration"] = _ebml_float(data, s, e)


# --------------------------------------------------------------------------
# AVI（RIFF）
# --------------------------------------------------------------------------

AVI_CODECS = {
    b"H264": ("H.264 / AVC", "AVC"),
    b"X264": ("H.264 / AVC", "AVC"),
    b"h264": ("H.264 / AVC", "AVC"),
    b"HEVC": ("H.265 / HEVC", "HEVC"),
    b"XVID": ("MPEG-4 Part 2 (Xvid)", "MPEG4"),
    b"DIVX": ("MPEG-4 Part 2 (DivX)", "MPEG4"),
    b"DX50": ("MPEG-4 Part 2", "MPEG4"),
    b"mp4v": ("MPEG-4 Part 2", "MPEG4"),
    b"MJPG": ("Motion JPEG", "MJPEG"),
    b"VP80": ("VP8", "VP8"),
    b"VP90": ("VP9", "VP9"),
    b"AV01": ("AV1", "AV1"),
    b"WMV3": ("WMV9 / VC-1", "VC1"),
}


def probe_avi(path, data, end):
    info = {"warnings": [], "engine": "python-avi", "container": "AVI"}
    if bytes(data[8:12]) != b"AVI ":
        info["warnings"].append("不是有效的 AVI 文件")
        return info
    pos = 12
    streams = []
    while pos + 8 <= end:
        cid = bytes(data[pos:pos + 4])
        size = int.from_bytes(data[pos + 4:pos + 8], "little")
        body = pos + 8
        stop = min(body + size, end)
        if cid == b"LIST":
            list_type = bytes(data[body:body + 4])
            if list_type == b"hdrl":
                p = body + 4
                while p + 8 <= stop:
                    sid = bytes(data[p:p + 4])
                    ssize = int.from_bytes(data[p + 4:p + 8], "little")
                    sbody = p + 8
                    send = min(sbody + ssize, stop)
                    if sid == b"LIST" and bytes(data[sbody:sbody + 4]) == b"strl":
                        streams.append(_avi_stream(data, sbody + 4, send))
                    elif sid == b"avih":
                        micro = int.from_bytes(data[sbody:sbody + 4], "little")
                        info["_micro_per_frame"] = micro
                        info["frame_count"] = int.from_bytes(data[sbody + 16:sbody + 20], "little")
                        info["_width"] = int.from_bytes(data[sbody + 32:sbody + 36], "little")
                        info["_height"] = int.from_bytes(data[sbody + 36:sbody + 40], "little")
                    p = send + (ssize & 1)
            elif list_type == b"INFO":
                info["container"] = "AVI (含 RIFF INFO)"
        pos = stop + (size & 1)

    micro = info.pop("_micro_per_frame", 0)
    if micro:
        fps = 1e6 / micro
        info["_fps"] = fps
    if info.get("frame_count") and info.get("_fps"):
        info["duration"] = info["frame_count"] / info["_fps"]

    video_stream = next((s for s in streams if s.get("type") == "vids"), None)
    audio_stream = next((s for s in streams if s.get("type") == "auds"), None)
    for stream in streams:
        stream.pop("codec_private", None)

    if video_stream:
        handler = bytes(video_stream.get("handler") or b"")
        name, family = AVI_CODECS.get(handler, (handler.decode("latin-1", "replace").strip() or "未知编码", None))
        info["video"] = {
            "codec": name,
            "codec_tag": handler.decode("latin-1", "replace").strip(),
            "codec_family": family,
            "width": video_stream.get("width") or info.get("_width"),
            "height": video_stream.get("height") or info.get("_height"),
            "fps": video_stream.get("fps") or info.get("_fps"),
            "frames": info.get("frame_count"),
            "bitrate": video_stream.get("bitrate"),
            "bit_depth": video_stream.get("bit_count"),
            "duration": info.get("duration"),
        }
    if audio_stream:
        codec_id = audio_stream.get("format_tag")
        channels = audio_stream.get("channels")
        info["audio"] = {
            "codec": {1: "PCM", 85: "MP3", 0x55: "MP3", 0x2000: "AC-3", 0xFF: "AAC", 0x161: "WMA"}.get(codec_id, f"音频 (0x{codec_id:04X})" if codec_id else "未知编码"),
            "codec_tag": f"0x{codec_id:04X}" if codec_id else None,
            "channels": channels,
            "channel_layout": channel_layout(channels),
            "sample_rate": audio_stream.get("sample_rate"),
            "bit_depth": audio_stream.get("bits_per_sample"),
            "bitrate": audio_stream.get("bitrate"),
        }
    info.pop("_fps", None)
    info.pop("_width", None)
    info.pop("_height", None)
    return info


def _avi_stream(data, start, end):
    stream = {}
    pos = start
    while pos + 8 <= end:
        cid = bytes(data[pos:pos + 4])
        size = int.from_bytes(data[pos + 4:pos + 8], "little")
        body = pos + 8
        stop = min(body + size, end)
        if cid == b"strh":
            stream["type"] = bytes(data[body:body + 4]).decode("latin-1", "replace")
            stream["handler"] = bytes(data[body + 4:body + 8])
            scale = int.from_bytes(data[body + 20:body + 24], "little")
            rate = int.from_bytes(data[body + 24:body + 28], "little")
            length = int.from_bytes(data[body + 32:body + 36], "little")
            sample_size = int.from_bytes(data[body + 36:body + 40], "little")
            if scale and rate:
                fps = rate / scale
                if stream.get("type") == "vids":
                    stream["fps"] = fps
                    stream["frame_count"] = length
            if sample_size and rate:
                stream["bitrate"] = sample_size * rate
        elif cid == b"strf":
            if stream.get("type") == "vids":
                stream["width"] = int.from_bytes(data[body + 4:body + 8], "little", signed=True)
                stream["height"] = int.from_bytes(data[body + 8:body + 12], "little", signed=True)
                stream["bit_count"] = int.from_bytes(data[body + 14:body + 16], "little")
                comp = bytes(data[body + 16:body + 20])
                if comp != b"\x00\x00\x00\x00":
                    stream["handler"] = comp
            else:
                stream["format_tag"] = int.from_bytes(data[body:body + 2], "little")
                stream["channels"] = int.from_bytes(data[body + 2:body + 4], "little")
                stream["sample_rate"] = int.from_bytes(data[body + 4:body + 8], "little")
                stream["bitrate"] = int.from_bytes(data[body + 8:body + 12], "little") * 8
                stream["bits_per_sample"] = int.from_bytes(data[body + 14:body + 16], "little")
        pos = stop + (size & 1)
    return stream


# --------------------------------------------------------------------------
# OpenCV 兜底
# --------------------------------------------------------------------------

def _opencv_probe(path):
    try:
        import cv2  # 延迟导入：没有 OpenCV 时也能跑
    except Exception:
        return {}
    cap = None
    try:
        cap = cv2.VideoCapture(path)
        if not cap.isOpened():
            return {}
        width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        fps = float(cap.get(cv2.CAP_PROP_FPS) or 0)
        frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        fourcc_int = int(cap.get(cv2.CAP_PROP_FOURCC) or 0)
        fourcc = "".join(chr((fourcc_int >> (8 * i)) & 0xFF) for i in range(4)).strip()
        bitrate = float(cap.get(cv2.CAP_PROP_BITRATE) or 0)
        duration = frames / fps if fps and frames else 0
        return {
            "width": width or None,
            "height": height or None,
            "fps": fps if fps and 0 < fps < 1000 else None,
            "frames": frames or None,
            "fourcc": fourcc or None,
            "bitrate": int(bitrate) if bitrate else None,
            "duration": duration or None,
        }
    except Exception:
        return {}
    finally:
        if cap is not None:
            try:
                cap.release()
            except Exception:
                pass


# --------------------------------------------------------------------------
# 对外接口
# --------------------------------------------------------------------------

def _sniff(data, end):
    head = bytes(data[0:min(end, 16)])
    if len(head) >= 12 and head[4:8] == b"ftyp":
        return "mp4"
    if head[:4] == b"\x1aE\xdf\xa3":
        return "mkv"
    if len(head) >= 12 and head[:4] == b"RIFF" and head[8:12] == b"AVI ":
        return "avi"
    return None


def probe(path):
    """解析视频文件，返回结构化元数据字典。"""
    path = os.path.abspath(path)
    result = {
        "ok": False,
        "path": path,
        "name": os.path.basename(path),
        "dir": os.path.dirname(path),
        "ext": os.path.splitext(path)[1].lower(),
        "warnings": [],
        "video": None,
        "audio": None,
    }
    try:
        st = os.stat(path)
        result["size"] = st.st_size
        result["mtime"] = _dt.datetime.fromtimestamp(st.st_mtime).isoformat(timespec="seconds")
    except OSError as exc:
        result["warnings"].append(f"无法访问文件: {exc}")
        return result

    data = None
    try:
        with open(path, "rb") as fh:
            if result["size"] == 0:
                result["warnings"].append("文件为空")
                return result
            data = mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ)
            kind = _sniff(data, len(data))
            parsed = {}
            try:
                if kind == "mp4":
                    parsed = probe_mp4(path, data, len(data))
                elif kind == "mkv":
                    parsed = probe_mkv(path, data, len(data))
                elif kind == "avi":
                    parsed = probe_avi(path, data, len(data))
                else:
                    result["warnings"].append("未识别的容器格式，使用 OpenCV 探测")
            except Exception as exc:  # noqa: BLE001
                result["warnings"].append(f"{kind or '未知'} 解析异常: {exc}")
            result.update({k: v for k, v in parsed.items() if k not in ("warnings", "tracks")})
            result["warnings"].extend(parsed.get("warnings", []))

            # 缺字段时用 OpenCV 补齐
            need_cv = not result.get("video") or not result["video"].get("width") or not result["video"].get("fps")
            if need_cv:
                cv = _opencv_probe(path)
                if cv:
                    result.setdefault("engine", "opencv")
                    video = result.get("video") or {}
                    for key, src in (("width", "width"), ("height", "height"), ("fps", "fps"),
                                     ("frames", "frames"), ("bitrate", "bitrate")):
                        if not video.get(key) and cv.get(src):
                            video[key] = cv[src]
                    if cv.get("fourcc") and not video.get("codec_tag"):
                        video["codec_tag"] = cv["fourcc"]
                    if not video.get("codec"):
                        video["codec"] = f"未知编码 ({cv['fourcc']})" if cv.get("fourcc") else "未知编码"
                    video.setdefault("codec_family", None)
                    result["video"] = video
                    if not result.get("duration") and cv.get("duration"):
                        result["duration"] = cv["duration"]
                    if video.get("fps"):
                        video["fps"] = round(video["fps"], 4)
    except (OSError, ValueError) as exc:
        result["warnings"].append(f"读取失败: {exc}")
    finally:
        if data is not None:
            try:
                data.close()
            except Exception:
                pass

    # 整理派生字段
    video = result.get("video")
    duration = result.get("duration") or (video or {}).get("duration") or 0
    if duration and result.get("size"):
        result["bitrate_total"] = int(result["size"] * 8 / duration)
    if video:
        if not video.get("bitrate"):
            audio_br = ((result.get("audio") or {}).get("bitrate") or 0)
            total = result.get("bitrate_total") or 0
            if total and total > audio_br:
                video["bitrate"] = total - audio_br
        if video.get("fps"):
            video["fps"] = round(float(video["fps"]), 4)
        if not video.get("frames") and video.get("fps") and duration:
            video["frames"] = int(round(video["fps"] * duration))
        if not video.get("duration"):
            video["duration"] = duration or None
        if (video.get("rotation") or 0) in (90, 270) and video.get("width") and video.get("height"):
            pass  # 已在解析阶段交换过
        w, h = video.get("width"), video.get("height")
        if w and h:
            from math import gcd
            g = gcd(int(w), int(h)) or 1
            video["aspect_ratio"] = f"{int(w) // g}:{int(h) // g}"
    result["duration"] = duration or None
    result["ok"] = bool(video)
    if not result["ok"]:
        result["warnings"].append("未能识别视频流")
    return result


class FrameReader:
    """按时间戳取帧（OpenCV 解码），供浏览器无法解码时兜底预览。"""

    def __init__(self, path):
        import cv2
        self.cv2 = cv2
        self.path = path
        self.cap = cv2.VideoCapture(path)
        self.size = None
        if self.cap.isOpened():
            self.size = (
                int(self.cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0),
                int(self.cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0),
            )

    @property
    def ok(self):
        return self.cap is not None and self.cap.isOpened()

    def frame_jpeg(self, seconds, max_width=1280, quality=80):
        if not self.ok:
            return None
        try:
            self.cap.set(self.cv2.CAP_PROP_POS_MSEC, max(0.0, float(seconds)) * 1000.0)
            ok, frame = self.cap.read()
            if not ok or frame is None:
                self.cap.set(self.cv2.CAP_PROP_POS_FRAMES, 0)
                ok, frame = self.cap.read()
            if not ok or frame is None:
                return None
            h, w = frame.shape[:2]
            if w > max_width:
                scale = max_width / w
                frame = self.cv2.resize(frame, (int(w * scale), int(h * scale)))
            ok, buf = self.cv2.imencode(".jpg", frame, [int(self.cv2.IMWRITE_JPEG_QUALITY), quality])
            return buf.tobytes() if ok else None
        except Exception:
            return None

    def close(self):
        try:
            if self.cap is not None:
                self.cap.release()
        except Exception:
            pass
        self.cap = None


if __name__ == "__main__":
    import json
    import sys

    for arg in sys.argv[1:]:
        print(json.dumps(probe(arg), ensure_ascii=False, indent=2))
