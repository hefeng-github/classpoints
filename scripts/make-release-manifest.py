#!/usr/bin/env python3
"""生成自动更新用的清单 release-manifest.json。

清单是客户端判断"要不要更新"的唯一依据，所以这里做的事情只有一件：
把准备发布的文件真实量一遍（大小、SHA-256），写成客户端能严格校验的形状。
任何一处对不上就退出非零，让发布停在这一步 —— 宁可这次不发版，
也不要发出一个"装上去版本号对不上"的包。

用法：
  python3 scripts/make-release-manifest.py \
      --version 1.2.0 \
      --setup dist/ClassPoints-Setup-1.2.0.exe \
      --portable dist/ClassPoints-win-x64-1.2.0.zip \
      --notes "支持安装包与自动更新" \
      --out dist/release-manifest.json

产物：
  dist/release-manifest.json   中间那个 stable 标签挂的就是它
  dist/SHA256SUMS.txt          附带给爱手工核对的人
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sys

# Windows 上 Python 的 stdout 跟着系统代码页走（英文系统是 cp1252），一 print 中文就
# 抛 UnicodeEncodeError。GitHub 的 windows runner 恰好是英文系统：清单和校验文件
# 其实都已经写好了，最后几行摘要把整步弄红。这里把输出流强制成 UTF-8，不靠外部环境变量。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):  # 老版本 Python 或被重定向成不可配置的流
        pass

VERSION_RE = re.compile(r"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$")
MAX_ASSET_BYTES = 1024 * 1024 * 1024


def die(message):
    print("错误：" + message, file=sys.stderr)
    sys.exit(1)


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def asset_of(path, expect_name=None):
    if not os.path.isfile(path):
        die(f"找不到文件：{path}")

    name = os.path.basename(path)
    if expect_name and name != expect_name:
        die(f"文件名必须是 {expect_name}，实际是 {name}")

    size = os.path.getsize(path)
    if size <= 0 or size > MAX_ASSET_BYTES:
        die(f"{name} 的大小不正常：{size} 字节")

    return {"name": name, "size": size, "sha256": sha256_of(path)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, help="正式版本号，形如 1.2.0")
    parser.add_argument("--setup", required=True, help="安装包路径")
    parser.add_argument("--portable", help="便携版 zip 路径（可选）")
    parser.add_argument("--notes", default="", help="更新说明")
    parser.add_argument("--notes-file", help="从文件读更新说明")
    parser.add_argument("--min-version", default="", help="低于此版本必须更新（可选）")
    parser.add_argument("--out", default="dist/release-manifest.json")
    parser.add_argument("--sums", default="dist/SHA256SUMS.txt")
    args = parser.parse_args()

    version = args.version.strip().lstrip("v")
    if not VERSION_RE.match(version):
        die(f"版本号必须是三段数字（主.次.修），当前是 '{args.version}'")

    # 安装包名由版本号唯一决定，客户端算出来的也是这个名字。
    setup_name = f"ClassPoints-Setup-{version}.exe"
    setup = asset_of(args.setup, setup_name)

    portable = asset_of(args.portable) if args.portable else None

    notes = args.notes
    if args.notes_file:
        with open(args.notes_file, encoding="utf-8") as handle:
            notes = handle.read()
    # 客户端会把说明原样显示，太长会撑破那个小面板
    notes = notes.strip()[:4000]

    if args.min_version.strip():
        min_version = args.min_version.strip().lstrip("v")
        if not VERSION_RE.match(min_version):
            die(f"minVersion 必须是三段数字，当前是 '{args.min_version}'")
    else:
        min_version = ""

    manifest = {
        "channel": "stable",
        "version": version,
        "tag": f"v{version}",
        "notes": notes,
        "publishedAt": datetime.datetime.now(datetime.timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z"),
        "setup": setup,
    }
    if min_version:
        manifest["minVersion"] = min_version
    if portable:
        manifest["portable"] = portable

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        # ensure_ascii=False 让说明里的中文可读；末尾补换行，diff 才好看
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    lines = [f"{setup['sha256']}  {setup['name']}"]
    if portable:
        lines.append(f"{portable['sha256']}  {portable['name']}")
    with open(args.sums, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")

    print(f"版本 {version}")
    print(f"清单 {args.out}")
    for item in [setup] + ([portable] if portable else []):
        print(f"  {item['name']}  {item['size'] / 1048576:.1f} MB  {item['sha256'][:16]}…")


if __name__ == "__main__":
    main()
