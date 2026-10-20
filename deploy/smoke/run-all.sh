#!/bin/sh
# ============================================================================
#  冒烟总入口（Linux / macOS）—— 与 run-all.cmd 同一件事：找到能用的 python3。
#
#  为什么需要它：deploy/smoke/*.py 只用标准库，但精简版 python 经常没有
#  sqlite3（pairing-smoke 要直接写 SQLite）。于是"换了个解释器就红"
#  会被记成"冒烟坏了"，其实只是解释器不对。
#
#  优先级：SMOKE_PYTHON > 项目内 .venv > PATH 上的 python3
#  输出全 ASCII：不同 locale 下中文可能变问号，反而看不清哪一步失败。
# ============================================================================
set -e

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SCRIPT="$SCRIPT_DIR/run-all.py"

if [ ! -f "$SCRIPT" ]; then
  echo "[smoke] missing $SCRIPT"
  exit 2
fi

PY=""

if [ -n "${SMOKE_PYTHON:-}" ]; then
  PY="$SMOKE_PYTHON"
fi

if [ -z "$PY" ] && [ -x "$SCRIPT_DIR/../../.venv/bin/python" ]; then
  PY="$SCRIPT_DIR/../../.venv/bin/python"
fi

if [ -z "$PY" ] && command -v python3 >/dev/null 2>&1; then
  PY=$(command -v python3)
fi

if [ -z "$PY" ]; then
  echo "[smoke] python3 not found. install python 3.8+ or set SMOKE_PYTHON."
  exit 2
fi

if ! "$PY" -c "import sys,sqlite3;sys.exit(0 if sys.version_info>=(3,8) else 1)" >/dev/null 2>&1; then
  echo "[smoke] unusable python: $PY"
  echo "[smoke] need python 3.8+ WITH the sqlite3 module."
  echo "[smoke] hint: set SMOKE_PYTHON=/path/to/python3 and retry."
  exit 2
fi

echo "[smoke] python=$PY"

exec "$PY" "$SCRIPT" "$@"
