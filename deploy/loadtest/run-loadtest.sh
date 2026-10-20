#!/usr/bin/env bash
#
# M6.3 压测（Linux / WSL）。与 run-loadtest.ps1 做同样四件事：
#   挑空闲端口 → 临时数据目录起服务 → 等 /healthz → 压完停服、删目录、只留报告。
#
# 用法：
#   ./run-loadtest.sh 20 1        # 20 台、1 分钟
#   AGENTS=100 MINUTES=30 RAMP=10 TAG=100 ./run-loadtest.sh
#
set -euo pipefail

AGENTS="${1:-${AGENTS:-20}}"
MINUTES="${2:-${MINUTES:-5}}"
RAMP="${RAMP:-5}"
COMMANDS_PER_MIN="${COMMANDS_PER_MIN:-0}"
STREAMS="${STREAMS:-0}"
TAG="${TAG:-$AGENTS}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="${OUT_DIR:-$SCRIPT_DIR/reports}"

SERVER_DLL="$REPO_ROOT/src/ControlServer.Server/bin/Debug/net8.0/ControlServer.Server.dll"
LOADTEST_DLL="$REPO_ROOT/deploy/loadtest/ControlServer.LoadTest/bin/Debug/net8.0/ControlServer.LoadTest.dll"
DOTNET="${DOTNET:-dotnet}"

if [[ ! -f "$SERVER_DLL" ]]; then
    echo "[x] 找不到中心产物：$SERVER_DLL" >&2
    echo "    先 dotnet build src/ControlServer.Server" >&2
    exit 1
fi

if [[ ! -f "$LOADTEST_DLL" ]]; then
    echo "[*] 编译压测工具……"
    "$DOTNET" build "$REPO_ROOT/deploy/loadtest/ControlServer.LoadTest/ControlServer.LoadTest.csproj" -m:1 -v:q --nologo
fi

# 挑一个当时空闲的端口（铁律 12：不绑固定端口）。
PORT="$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()' 2>/dev/null \
    || echo $((20000 + RANDOM % 20000)))"

STAMP="$(date +%Y%m%d-%H%M%S)"
DATA_DIR="$(mktemp -d "${TMPDIR:-/tmp}/controlserver-loadtest-XXXXXX")"
mkdir -p "$OUT_DIR"

echo "[*] 端口 $PORT / 数据目录 $DATA_DIR"

export Server__Storage__DataDir="$DATA_DIR"
export Server__Http__Urls="http://127.0.0.1:$PORT"
export Server__Https__Enabled=false
export Server__Startup__AutoOpenBrowser=false
export Server__Tray__Enabled=false
export Server__Startup__SingleInstance=false
export Server__Startup__AttachConsole=true
export Server__Time__AutoSync=false
export Server__Timer__Enabled=false
export Server__Discovery__Port=0
export Server__Agent__MaxHosts=$((AGENTS + 50))
export Server__Auth__LoginRateLimit__PermitLimit=1000

SERVER_PID=""

cleanup() {
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "[*] 停止中心……"
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    rm -rf "$DATA_DIR"
}
trap cleanup EXIT

echo "[*] 启动中心……"
"$DOTNET" "$SERVER_DLL" > "$DATA_DIR/server.log" 2>&1 &
SERVER_PID=$!

READY=0

# 探活两种写法都试：Windows Git Bash 里的 curl 受代理环境影响，
# 对 127.0.0.1 会返回 000（实测），而 python3 的裸 socket 不受影响。
probe_health() {
    if command -v curl >/dev/null 2>&1 \
        && curl -fsS --max-time 2 --noproxy '*' "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then
        return 0
    fi

    if command -v python3 >/dev/null 2>&1 \
        && python3 -c "import socket; s=socket.create_connection(('127.0.0.1',$PORT),2); s.close()" >/dev/null 2>&1; then
        return 0
    fi

    return 1
}

for _ in $(seq 1 120); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
    if probe_health; then READY=1; break; fi
    sleep 0.5
done

if [[ "$READY" != "1" ]]; then
    echo "[x] 中心没起来。服务端日志末尾：" >&2
    tail -40 "$DATA_DIR/server.log" >&2 || true
    exit 1
fi

echo "[*] 中心就绪（PID $SERVER_PID），开始压测"

"$DOTNET" "$LOADTEST_DLL" \
    --server "http://127.0.0.1:$PORT" \
    --agents "$AGENTS" \
    --minutes "$MINUTES" \
    --ramp "$RAMP" \
    --commands-per-min "$COMMANDS_PER_MIN" \
    --streams "$STREAMS" \
    --server-pid "$SERVER_PID" \
    --out "$OUT_DIR" \
    --tag "$TAG"
