#!/usr/bin/env bash
#
# 中控服务版 —— Linux 交付构建脚本
#
# 在任意平台（Windows Git Bash / macOS / Linux）交叉编译出 linux-x64 自包含包。
# 产出：
#   dist/linux-x64/                            发布目录（解包即用，不依赖系统 .NET）
#   dist/controlserver-linux-x64-<日期>.tar.gz  交付包（已剔除 pdb）
#
# 用法：
#   bash deploy/linux/build-linux.sh [--skip-web] [--out <目录>]
#
#   --skip-web   跳过前端构建（wwwroot 已是新的，或只想重打后端）
#   --out <目录>  发布目录，默认 dist/linux-x64
#
# 说明：
#   · 前端产物会被【嵌入 exe】，所以改前端后必须重跑前端构建再 publish。
#   · appsettings.json 不随发布输出（配置全有代码默认值）；要改端口/账号，
#     把 appsettings.json 放到发布目录根（= exe 同目录）即可被自动加载。
#   · 本脚本只产出 linux-x64；Windows 交付走 deploy/windows/build-portable-exe.ps1。
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

RID="linux-x64"
OUT_DIR="dist/$RID"
SKIP_WEB=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-web) SKIP_WEB=1; shift ;;
    --out) OUT_DIR="${2:?--out 需要一个目录参数}"; shift 2 ;;
    -h|--help) sed -n '2,20p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知参数：$1（-h 看用法）" >&2; exit 1 ;;
  esac
done

# ── 定位 dotnet ────────────────────────────────────────────────
# ⚠️ PATH 里可能有一个「只装运行时、没有 SDK」的 dotnet（本机就是这样），
#    直接用它会在 publish 时以 "The application 'publish' does not exist" 失败。
#    因此逐个候选验证「确实带 SDK」（--list-sdks 非空）后再采用。
DOTNET="${DOTNET:-}"
if [[ -z "$DOTNET" ]]; then
  candidates=()
  [[ -x "/c/Users/IYLONG/.workbuddy/binaries/dotnet8/dotnet.exe" ]] \
    && candidates+=("/c/Users/IYLONG/.workbuddy/binaries/dotnet8/dotnet.exe")
  if command -v dotnet >/dev/null 2>&1; then candidates+=("$(command -v dotnet)"); fi
  [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]] && candidates+=("$DOTNET_ROOT/dotnet")

  if (( ${#candidates[@]} )); then
    for c in "${candidates[@]}"; do
      if [[ -n "$("$c" --list-sdks 2>/dev/null)" ]]; then DOTNET="$c"; break; fi
    done
  fi
fi

if [[ -z "$DOTNET" ]]; then
  echo "[fail] 找不到带 SDK 的 dotnet（只装运行时是不够的）。" >&2
  echo "       请安装 .NET 8 SDK，或用 DOTNET=<dotnet路径> 显式指定。" >&2
  exit 1
fi
echo "==> 使用 dotnet：$DOTNET（$("$DOTNET" --version 2>/dev/null || echo '?')）"

# ── 前端构建 ───────────────────────────────────────────────────
if [[ $SKIP_WEB -eq 0 ]]; then
  if command -v npm >/dev/null 2>&1; then
    echo "==> 构建前端（web/ -> src/ControlServer.Server/wwwroot）"
    ( cd web && npm run build )
  else
    echo "[warn] 未找到 npm，跳过前端构建（沿用现有 wwwroot）。"
    echo "       要更新界面请先执行：cd web && npm install && npm run build"
    echo "       或显式加 --skip-web 表明是有意跳过。"
  fi
else
  echo "==> 跳过前端构建（--skip-web）"
fi

# ── 发布 ───────────────────────────────────────────────────────
echo "==> 发布 linux-x64 自包含包 -> $OUT_DIR"
rm -rf "$OUT_DIR"
"$DOTNET" publish src/ControlServer.Server \
  -c Release -r "$RID" --self-contained true \
  -o "$OUT_DIR" --nologo

# 交付不需要调试符号（可省 ~10MB，也避免把源码路径带出去）
find "$OUT_DIR" -name '*.pdb' -delete

# ── 打包 ───────────────────────────────────────────────────────
# ⚠️ Windows 上打 tar 会丢失可执行位：NTFS 没有 exec 概念、MSYS 的 chmod 也写不进扩展属性，
#    结果包里的 ControlServer.Server 是 0644 —— 在 Linux 上解包后直接运行会
#    Permission denied（目录若也丢了 x 甚至连进都进不去）。
#    故打包时用 --mode 统一兜底：目录可进入、可执行文件可运行。
#    （install.sh 另有 chmod +x、Docker 走的是 Linux 构建容器，均不受此影响。）
STAMP="$(date +%Y%m%d)"
TARBALL="dist/controlserver-$RID-$STAMP.tar.gz"
echo "==> 打包 -> $TARBALL"
tar -czf "$TARBALL" --mode='u+rwx,go+rx' -C "$OUT_DIR" .

SIZE="$(du -sh "$OUT_DIR" | cut -f1)"
echo
echo "==> 完成"
echo "    发布目录：$OUT_DIR（$SIZE）"
echo "    交付包　：$TARBALL"
echo
echo "部署到 Linux（二选一）："
echo "  · systemd： sudo bash deploy/linux/install.sh $OUT_DIR"
echo "  · Docker　： docker build -f deploy/linux/Dockerfile -t controlserver ."
