"""把「便携版」目录打包进自解压启动器，生成单文件 exe。

产物：dist\\视频对比工具.exe
格式：[launcher.exe][payload.zip][int64 zip长度][16字节标记 VCPAYLOAD-V1-END]
"""

import os
import struct
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PAYLOAD_DIR = os.path.join(ROOT, "dist", "便携版", "视频对比工具")
LAUNCHER = os.path.join(HERE, "launcher.exe")
ZIP_PATH = os.path.join(HERE, "payload.zip")
OUT = os.path.join(ROOT, "dist", "视频对比工具.exe")
MAGIC = b"VCPAYLOAD-V1-END"
assert len(MAGIC) == 16


def build_zip():
    if not os.path.isdir(PAYLOAD_DIR):
        sys.exit(f"找不到便携版目录：{PAYLOAD_DIR}\n请先运行 PyInstaller 的 --onedir 打包。")
    files = 0
    with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for base, _dirs, names in os.walk(PAYLOAD_DIR):
            for name in sorted(names):
                src = os.path.join(base, name)
                rel = os.path.relpath(src, PAYLOAD_DIR).replace(os.sep, "/")
                zf.write(src, rel)
                files += 1
    size = os.path.getsize(ZIP_PATH)
    print(f"payload: {files} 个文件, {size / 1024 / 1024:.1f} MB")
    return size


def main():
    if not os.path.isfile(LAUNCHER):
        sys.exit("找不到 build/launcher.exe，请先编译启动器。")
    build_zip()
    with open(LAUNCHER, "rb") as fh:
        launcher = fh.read()
    with open(ZIP_PATH, "rb") as fh:
        payload = fh.read()
    with open(OUT, "wb") as fh:
        fh.write(launcher)
        fh.write(payload)
        fh.write(struct.pack("<q", len(payload)))
        fh.write(MAGIC)
    total = os.path.getsize(OUT)
    print(f"已生成：{OUT}  ({total / 1024 / 1024:.1f} MB)")


if __name__ == "__main__":
    main()
