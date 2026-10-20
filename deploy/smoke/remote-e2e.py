#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M5.4-b 第 9 步 · 远程桌面端到端（**观看端真解码出画面**）。

验的是 M5 那条链的**最后一米**：中心 → 被控端抓屏硬编 → SignalR → 浏览器 WebCodecs
→ canvas 上屏。前面所有用例（DXGI 抓屏、NV12/硬编、协商、排期）都是**半边的**：
它们各自证明"我这一段对"，而没有一条证明"合起来画面真的出得来"。

本脚本回答六个问题：

  ① **能看见 H.264 画面吗** —— 读页面上的「解码器（产出 / 解不出 / 重建）」，
     要求 `产出 > 0`、`解不出 == 0`、`重建` 不涨（重建 ≠ 0 ⇒ 解码链断过，画面花）。
  ② **指针跟着动吗** —— 脚本驱动 `SetCursorPos` 到几个已知位置，
     比对观看端画面上指针像素的出现/消失。
  ③ **深底 / 浅底上都正常吗**（`--backdrops`）—— 在真实桌面上找一块**确凿很暗**的
     平坦区与一块**确凿很亮**的平坦区，各判一次"指针进画面"。见 `verify_backdrops`。
  ④ **延迟是多少** —— 就是页面上那个「帧时延」（被控端抓屏时刻 → 本机画上），
     由 `collect_metrics` 采 min / avg / max。⚠️ 它**依赖两侧时钟一致**：
     单机自环下可信；跨机时先确认两边 NTP 都到位，否则页面自己会显示「时钟未同步」。
  ⑤ **真机上拿到的是哪种指针形状**（`--shapes`，**需 `--headful`**）—— 桌面上的箭头与
     文本区上的 I 型是两种不同的形状类型，各自走合成器里**不同的分支**。
  ⑥ **帧率 / 码率 / 丢帧 / 帧时延是多少** —— 读观看页那排卡片，并与中心接口交叉核对。
     见 `collect_metrics`。
  ⑦ **桌面静止时真的会降帧吗、动一下会立刻回来吗**（`--idle SEC`）—— 用一个置顶纯色
     全屏窗口把桌面**真的盖住**，再看托盘日志里有没有「退避」/「恢复变化」。
     🔴 这一节治的是"单测验过 ≠ 真机验过"：第一次 18/18 冒烟全程没触发过退避，
     因为桌面一直在动。见 `verify_idle_backoff` 与 `SolidOverlay`。

🔴 **前置约定（铁律 13/14 的形状，别踩）**
  · 被测端一律走**源码态**：`src/ControlServer.Server/bin/Debug/...` 与
    `agents/ControlServer.Agent.Service/bin/Debug/...`。
  · **绝不允许**落到 `dist/win-x64/`（上一次打包的产物）—— 那会出现
    "服务端是新代码、客户机是旧包"，而结果看起来**全绿**（没测到新代码）。
  · 浏览器内核必须是**系统 Edge**（`channel="msedge"`）：
    playwright 自带那套 Chromium 是开源构建，**不含 H.264**
    ⇒ `isConfigSupported('avc1...')` 直接 false，整条 H.264 路根本不会建。
    （实测：Edge 155 对 avc1.42E01E/4D401E/640028/avc3 全 true，对 hvc1/hev1 全 false。）

用法：
    python deploy/smoke/remote-e2e.py                 # 画面 + 指针 + 读数
    python deploy/smoke/remote-e2e.py --no-pointer     # 只验画面出得来
    python deploy/smoke/remote-e2e.py --headful        # 看得见的窗口（人工复核用）
    python deploy/smoke/remote-e2e.py --backdrops      # 追加深/浅底 + 延迟上界
    python deploy/smoke/remote-e2e.py --headful --shapes   # 追加"形状类型"实验
    python deploy/smoke/remote-e2e.py --idle 10        # 追加"静止降帧"（盖屏 10 秒）
    python deploy/smoke/remote-e2e.py --panels         # 追加"多屏 / 质量档 / 剪贴板 / 全屏"四个入口

🔴 **`--backdrops` 是零副作用的**：它**不改**壁纸、不改任何系统设置。
   （曾经打算"临时把壁纸换成纯色"来铺一块深底 / 浅底，实测在本机**不生效** ——
   `SystemParametersInfoW` 回 True、读回也变了，但直抓屏比对屏幕主体亮度没动。
   详见 `find_flat_zones` 的注释。改成"在真实桌面上找确凿很暗 / 很亮的平坦块"。）
   桌面上找不到那样的平坦块（窗口铺满时）就**如实跳过**，不报假 FAIL。

🔴 **`--shapes` 必须配 `--headful`**：I 型光标只出现在窗口里的文本区上，
   而无头模式**屏幕上没有那个窗口**，真实光标根本悬不到它上面。
"""

import argparse
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import urllib.error
import urllib.request
from http.cookiejar import CookieJar

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

DOTNET_ROOT = os.environ.get("DOTNET_ROOT") or r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8"

SERVER_EXE = os.path.join(REPO, "src", "ControlServer.Server", "bin", "Debug", "net8.0",
                          "ControlServer.Server.exe")
AGENT_EXE = os.path.join(REPO, "agents", "ControlServer.Agent.Service", "bin", "Debug", "net8.0",
                         "ControlServer.Agent.Service.exe")

# 🔴 **托盘不是可选的**：抓屏与编码**只能**在有桌面的进程里做，而客户端服务按设计
#    跑在 Session 0（没有桌面）。所以 Tray 持有 `RemoteStreamHost`，
#    服务经命名管道把远控指令转给它。
#
#    ⚠️ **只起 Service 的症状非常有迷惑性**：`POST /api/remote/sessions` 返回 200、
#    协商日志齐全、状态停在 `requested`，然后 **20 秒后**被巡检清掉，
#    理由是"没有收到被控端的任何状态上报"。看起来像"客户端版本过旧不认识这条指令"，
#    其实是**管道那头根本没人**——托盘没起。
TRAY_EXE = os.path.join(REPO, "agents", "ControlServer.Agent.Tray", "bin", "Debug", "net8.0",
                        "ControlServer.Agent.Tray.exe")

# 浏览器内核：必须系统 Edge（见文件头 🔴）。
EDGE_CHANNEL = "msedge"

ADMIN_USER = "admin"
ADMIN_PASS = "admin123"

OUT_DIR = os.path.join(REPO, ".tools", "remote-e2e")

# ── 深 / 浅底受控实验：**不改任何系统设置** ─────────────────────────
#
# 🔴 原本的打算是"临时把桌面壁纸换成纯深 / 纯浅色"，实测**在本机不生效**：
#    调 `SystemParametersInfoW(SPI_SETDESKWALLPAPER)` 返回 True、`SPI_GET` 读回也变了，
#    但直抓屏比对后屏幕主体的亮度**没变**（238.3 → 238.5），只有 9% 的像素在动
#    （那是窗口自己的刷新）。
#    ⇒ 与其去猜"是填充方式设成了纯色 / 还是 Explorer 不响应"，不如换一条**零副作用**
#      的路：**在真实桌面里直接找"确凿很暗的平坦块"和"确凿很亮的平坦块"**，
#      把光标放上去判。底色不再是我们设定的精确值，但判据本来就不需要精确值 ——
#      它需要的是"一块很暗的平坦区"和"一块很亮的平坦区"，而这两样真实桌面上都有。
DARK_ZONE_MAX = 80.0        # 暗块：平均亮度 ≤ 此值
LIGHT_ZONE_MIN = 170.0      # 亮块：平均亮度 ≥ 此值
FLAT_ZONE_MAX_STD = 6.0     # 平坦：块内标准差 ≤ 此值（挡住文字 / 图标密布的块）

# ── 被控端托盘日志里那两行诊断（`RemoteStreamHost` 每 5 秒一行）─────────
#    样例：
#      [Stream] 指针：画上 141 帧 / 失败 0 帧（最近一次：画上了）
#              ｜最近一次的入参：指针 1800,1100 形状 32×32 Color（热点 0,0，可见，形状字节 4096）
CURSOR_SUM_RE = re.compile(r"画上\s*(\d+)\s*帧\s*/\s*失败\s*(\d+)\s*帧")
SHAPE_RE = re.compile(
    r"指针\s*(-?\d+),(-?\d+)\s+形状\s*(\d+)[×x](\d+)\s*([A-Za-z_]+)"
    r"（热点\s*(-?\d+),(-?\d+)，[^）]*形状字节\s*(\d+)")

FAILURES = []
STEPS = []


def check(name, ok, detail=""):
    print(("  [PASS] " if ok else "  [FAIL] ") + name + ("   " + detail if detail else ""))
    STEPS.append((name, ok, detail))
    if not ok:
        FAILURES.append(name + " :: " + detail)
    return ok


def note(msg):
    print("  · " + msg)


def free_port(kind=socket.SOCK_STREAM):
    with socket.socket(socket.AF_INET, kind) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


# ═══════════════════════════════════════════════════════════════
# HTTP（带 cookie，登录后复用）
# ═══════════════════════════════════════════════════════════════

class Http:
    def __init__(self):
        self.jar = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def call(self, method, url, body=None, timeout=30):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, data=data, method=method)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with self.opener.open(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8", "replace")
                try:
                    return resp.status, json.loads(raw) if raw else None
                except json.JSONDecodeError:
                    return resp.status, raw
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", "replace")
            try:
                return e.code, json.loads(raw) if raw else None
            except json.JSONDecodeError:
                return e.code, raw
        except Exception as e:  # 连接被拒等
            return 0, str(e)


def wait_ready(http, base_url, proc, timeout=45.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        status, _ = http.call("GET", base_url + "/readyz", timeout=3)
        if status == 200:
            return True
        time.sleep(0.5)
    return False


# ═══════════════════════════════════════════════════════════════
# 🔴 前端产物新鲜度：服务端发的是不是**磁盘上刚构建的那一份**
# ═══════════════════════════════════════════════════════════════
#
# 这条判据治的是一次真实的误诊（2026-10-18）：
#   改了前端 → `vue-tsc` 过、`vite build` 成功、`wwwroot` 里确实是新包，
#   可冒烟跑出来**行为一点没变**（页面还在报同一个错）。
#   真因：进程的 ContentRootPath（= 本脚本的 cwd = 仓库根）下**没有 `wwwroot`**，
#   于是 `Program.cs` 回退到 `ManifestEmbeddedFileProvider` —— 发的是**编译期内嵌的旧包**。
#   ⇒ 构建、产物、源码三处全绿，被测的却是上一个版本。
#
# 判据很便宜：比 `index.html` 里引用的 `assets/index-*.js` 是不是同一个文件名。

def _asset_of(html):
    m = re.search(r"assets/(index-[A-Za-z0-9_\-]+\.js)", html or "")
    return m.group(1) if m else None


def verify_asset_freshness(http, base_url):
    disk_index = os.path.join(REPO, "src", "ControlServer.Server", "wwwroot", "index.html")

    if not os.path.isfile(disk_index):
        note("磁盘上没有 src/ControlServer.Server/wwwroot/index.html（前端还没构建过）—— 跳过新鲜度判据")
        return

    with open(disk_index, encoding="utf-8") as fh:
        disk_html = fh.read()

    status, body = http.call("GET", base_url + "/", timeout=10)
    served_asset = _asset_of(body) if (status == 200 and isinstance(body, str)) else None
    disk_asset = _asset_of(disk_html)

    check(
        "服务端发的前端产物 = 磁盘上刚构建的那一份",
        served_asset is not None and served_asset == disk_asset,
        f"服务端 {served_asset} vs 磁盘 {disk_asset} —— 不一致说明它发的是**内嵌的旧包**"
        "（cwd 下没有 wwwroot ⇒ ManifestEmbeddedFileProvider）。"
        "改完前端必须 `dotnet build src/ControlServer.Server` 重新嵌入，否则整轮验的是旧版本")


def agents_of(body):
    """从响应里取出机器清单，兼容两种形状。

    · `GET /api/agents`        → 直接就是 `{items, total, online, …}`
    · 写操作（改名/拉黑/移除…） → `{ok, status, message, list: {items, …}}`

    🔴 别写成 `isinstance(body, list)` —— 那会**静默**永远返回空，
       现场表现是"客户机不上线"（而它其实早就上线了）。
    """
    if not body:
        return []
    if isinstance(body, list):
        return body
    if isinstance(body, dict):
        items = body.get("items")
        if isinstance(items, list):
            return items
        nested = body.get("list")
        if isinstance(nested, dict) and isinstance(nested.get("items"), list):
            return nested["items"]
        if isinstance(nested, list):
            return nested
    return []


def wait_for(fn, predicate, timeout=30.0, interval=0.5):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = fn()
        if predicate(last):
            return True, last
        time.sleep(interval)
    return False, last


# ═══════════════════════════════════════════════════════════════

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-pointer", action="store_true", help="只验画面出得来，不动鼠标")
    ap.add_argument("--headful", action="store_true", help="显示浏览器窗口（人工复核）")
    ap.add_argument("--probe", action="store_true",
                    help="只跑 HTTP 侧建会话探针（不起浏览器）—— 用来把"
                         "「前端问题」与「被控端不推流」切开")
    ap.add_argument("--keep", action="store_true", help="收摊时不删临时目录")
    ap.add_argument("--backdrops", action="store_true",
                    help="追加深/浅底受控实验：在真实桌面上找一块**确凿很暗**的平坦区与"
                         "一块**确凿很亮**的平坦区，各判一次「指针进画面」。"
                         "🟢 **零副作用**（不改壁纸、不改任何系统设置）；"
                         "找不到那样的平坦块就**如实跳过**。")
    ap.add_argument("--shapes", action="store_true",
                    help="追加「真机拿到的是哪种指针形状」实验（**需 --headful**）："
                         "桌面角落 = 箭头、文本区 = I 型，两者在合成器里走不同分支。")
    ap.add_argument("--panels", action="store_true",
                    help="追加三个前端入口的浏览器实证：**抓哪块屏 / 质量档 / 剪贴板**（M5.5），"
                         "外加「网页全屏」入口（M7.9）。判据落在页面文字 + 后端返回值 + 宿主剪贴板"
                         "三处交叉，不看'有没有抛异常'（没抛只说明请求发出去了）。"
                         "⚠️ 会短暂改写**宿主剪贴板**（收摊时还原）。"
                         "⚠️ 无头浏览器可能拒绝元素全屏 —— 那一条会**如实跳过**，不判失败。")
    ap.add_argument("--metrics-seconds", type=float, default=8.0, metavar="SEC",
                    help="读数采样时长（秒）；0 = 跳过。默认 8。")
    ap.add_argument("--dwell", type=float, default=0.0, metavar="SEC",
                    help="建完会话后**多留一会儿**再收摊（秒），用来采被控端"
                         "「每 5 秒一行」的指针诊断。"
                         "⚠️ 实测：会话在探针看到 streaming 的那一刻就被 DELETE 了，"
                         "被控端推流循环只活 **0.6 秒** —— 所以不留一会儿，"
                         "永远只看得到开流那一行（必然是「0 帧」）。")
    ap.add_argument("--chunk-threshold", type=int, default=0, metavar="KB",
                    help="🔥 诊断模式：把被控端的**单片上限**降到这么多 KB（M5.4-c① 步 6）。"
                         "本机没有 8K 屏 ⇒ 分片那条路平时是**死代码**（帧都装得下）；"
                         "降到 64 就把 2160×1440 的普通帧切成 5 片，端到端才真的碰到它。"
                         "判据是中心 `stats.reassembledFrames` > 0（拼出来的帧数）。"
                         "⚠️ 下限 64 KB：再小理论上会让 64 MiB 的帧算出 >1024 片"
                         "（`Slice` 会抛），只是真帧到不了那个尺寸。0 = 不降（默认）。")
    ap.add_argument("--idle", type=float, default=0.0, metavar="SEC",
                    help="🔥 静止降帧的真机判据（M5.4-c② 步 8）：用一个置顶纯色全屏窗口"
                         "把桌面**真的盖住** SEC 秒，验「静止时长涨起来 / 退避 / 中心不判死 / "
                         "立刻恢复 / 画面又活了」五条。默认 0 = 跳过。"
                         "⚠️ 那段 SEC 秒里桌面被盖住（终端也看不见），属正常。"
                         "⚠️ 有头模式下盖布会连浏览器窗口一起盖掉 —— 读数照读，只是看不见。")
    args = ap.parse_args()

    if not os.path.isfile(SERVER_EXE):
        print(f"[X] 找不到被测服务端（源码态）：{SERVER_EXE}")
        print("    先跑：run.cmd（或 dotnet build src/ControlServer.Server）")
        return 2
    if not os.path.isfile(AGENT_EXE):
        print(f"[X] 找不到被测客户机（源码态）：{AGENT_EXE}")
        print("    先跑：dotnet build agents/ControlServer.Agent.Service")
        return 2
    if not os.path.isfile(TRAY_EXE):
        print(f"[X] 找不到客户机托盘（源码态）：{TRAY_EXE}")
        print("    先跑：dotnet build agents/ControlServer.Agent.Tray")
        print("    ⚠️ 没有托盘就没有画面 —— 抓屏与编码只在这个进程里做。")
        return 2

    os.makedirs(OUT_DIR, exist_ok=True)

    http_port = free_port()
    pairing_port = free_port(socket.SOCK_DGRAM)
    discovery_port = free_port(socket.SOCK_DGRAM)
    while discovery_port == pairing_port:
        discovery_port = free_port(socket.SOCK_DGRAM)

    base_url = f"http://127.0.0.1:{http_port}"

    tag = f"remote-e2e-{os.getpid()}"
    data_dir = os.path.join(tempfile.gettempdir(), f"cs-{tag}-data")
    state_dir = os.path.join(tempfile.gettempdir(), f"cs-{tag}-state")
    for d in (data_dir, state_dir):
        shutil.rmtree(d, ignore_errors=True)

    server_log_path = os.path.join(OUT_DIR, "server.log")
    agent_log_path = os.path.join(OUT_DIR, "agent.log")
    tray_log_path = os.path.join(OUT_DIR, "tray.log")

    print("══════════════════ 远程桌面端到端（M5.4-b 第 9 步）══════════════════")
    print(f"  服务端：{os.path.relpath(SERVER_EXE, REPO)}")
    print(f"  客户机：{os.path.relpath(AGENT_EXE, REPO)}")
    print(f"  地址：  {base_url}")
    print(f"  状态目录：{state_dir}")
    print(f"  产物：  {os.path.relpath(OUT_DIR, REPO)}")
    print()

    env = dict(os.environ)
    env.setdefault("DOTNET_ROOT", DOTNET_ROOT)

    # ── 🔥 诊断模式：把**客户机**的单片上限降下来（M5.4-c① 步 6）──
    #
    # 为什么需要它：本机（以及绝大多数客户机）没有 8K 屏，而分片那条路只在
    # 「一条消息装不下」时才会走 ⇒ 平时它**是一段死代码**。
    # 把阈值降到 64 KB，2160×1440 的普通帧会被切成 5 片 —— 这是端到端唯一
    # 能真的碰到分片的办法。
    #
    # ⚠️ **只给客户机**：服务端那一侧不切片（它只负责拼），把变量塞给它
    #    只会让「这个环境变量到底谁在读」变得含糊。
    agent_env = dict(env)

    if args.chunk_threshold < 0:
        print(f"[X] --chunk-threshold 不能是负数（实得 {args.chunk_threshold}）")
        return 2

    if args.chunk_threshold > 0:
        if args.chunk_threshold < 64:
            print(f"    [!] --chunk-threshold={args.chunk_threshold} KB 低于 64 KB ——"
                  " 理论上会让 64 MiB 的帧算出 >1024 片（`Slice` 会抛）；"
                  "真帧到不了那个尺寸，但这一轮请盯着日志里有没有「超过协议允许的…片上限」。")

        agent_env["CONTROLSERVER_CHUNK_THRESHOLD_BYTES"] = str(args.chunk_threshold * 1024)
        print(f"    🔧 诊断模式：客户机分片阈值 = {args.chunk_threshold} KB"
              f" ⇒ **普通帧也会被切成几片**（判据：中心 reassembledFrames > 0）")

    server_log = open(server_log_path, "wb")
    agent_log = open(agent_log_path, "wb")

    server_proc = subprocess.Popen([
        SERVER_EXE,
        f"--Server:Storage:DataDir={data_dir}",
        f"--Server:Http:Urls={base_url}",
        f"--Server:Listener:PairingPort={pairing_port}",
        f"--Server:Discovery:Port={discovery_port}",
        "--Server:Startup:AutoOpenBrowser=false",
        "--Server:Tray:Enabled=false",
        "--Server:Startup:SingleInstance=false",
    ], stdout=server_log, stderr=subprocess.STDOUT, cwd=REPO, env=env)

    agent_proc = None
    tray_proc = None
    tray_log = None
    try:
        # ── 1. 中心就绪 ──
        http = Http()
        if not check("中心在 45s 内就绪（/readyz）", wait_ready(http, base_url, server_proc),
                     f"日志：{server_log_path}"):
            return 1

        # ── 1.5 前端产物新鲜度（先判，否则后面所有页面判据都可能是在验旧包）──
        verify_asset_freshness(http, base_url)

        # ── 2. 登录 + 看授权档位 ──
        status, body = http.call("POST", base_url + "/api/auth/login",
                                 {"userName": ADMIN_USER, "password": ADMIN_PASS})
        check("管理员登录成功", status == 200,
              f"实得 {status} {body if not isinstance(body, dict) else body.get('role')}")
        if status != 200:
            return 1

        status, lic = http.call("GET", base_url + "/api/license")
        lic = lic if isinstance(lic, dict) else {}
        note(f"授权：status={lic.get('status')} isUsable={lic.get('isUsable')} "
             f"档位={lic.get('enforcement')}")
        # ⚠️ 不在这里断言 —— 我们验的是**编解码链路**，不是授权子系统
        #    （授权由 license-smoke.py 负责）。但档位若是 Enforce 且不可用，
        #    建会话会被 403 挡住，那时要把它说清楚，而不是报"画面出不来"。
        if lic.get("enforcement") == "Enforce" and lic.get("isUsable") is False:
            check("授权档位不挡建会话（Enforce 且不可用 ⇒ 会 403）", False,
                  "改用 --Server:License:Enforcement=Off 重跑（届时在文档里记一笔）")

        # ── 3. 客户机起来并上线 ──
        agent_proc = subprocess.Popen([
            AGENT_EXE,
            f"--Agent:ServerUrl={base_url}",
            f"--Agent:StateDirectory={state_dir}",
            "--Agent:ServiceName=ControlServerRemoteE2E",
            "--Agent:AllowBroadcastDiscovery=false",
            # 🔥 **这一行必须显式写 true，而且它是本脚本最容易踩的一个坑。**
            #
            #    键名叫 `ExecuteUdpCommands`，看起来只是"UDP 那条通道执不执行"，
            #    所以它常被顺手关掉（agent-smoke.py 就是关的，理由是"冒烟不允许
            #    把跑它的机器关掉"）。但 **2026-10-15 起它的语义已经扩大成
            #    "本机此刻敢不敢动手"的总闸**，管的不只是 UDP ——
            #    中心经 SignalR 下发的关机 / 重启 / 音量**以及远控建流**一律先看它。
            #
            #    ⇒ 关掉它的现场症状**极其难查**：中心侧 POST 返回 200、
            #      编码协商日志齐全、账本登记成功，状态停在 `requested`，
            #      20 秒后被巡检按"没有收到被控端的任何状态上报"清掉。
            #      而客户端日志里只有一行 Warning：
            #        「演练模式已开启（Agent:Tasks:ExecuteUdpCommands=false）—— 指令已识别但不执行。」
            #      光是中心侧看，它和"客户端版本过旧""托盘没在跑"长得一模一样。
            #
            #    ⚠️ 代价要说清楚：开了这道闸，本机 UDP 指令通道（默认 8090）会
            #      **真的执行**收到的电源指令。本脚本自己不发任何电源 / 音量指令，
            #      所以只要这台机器上没有别人往 8090 发报文，就没有额外风险。
            "--Agent:Tasks:ExecuteUdpCommands=true",
            # 🔥 关掉本机配置页：它默认绑 127.0.0.1:8091，而"生产那个值是几"与本脚本
            #    要验的东西无关 —— 绑固定端口只会让用例在"本机恰好装了客户端"时变红
            #    （铁律 12 的形状：红的是机器上装了什么，不是代码）。
            "--Agent:LocalUi:Enabled=false",
        ], stdout=agent_log, stderr=subprocess.STDOUT, cwd=REPO, env=agent_env)

        # ── 托盘：远控推流的**唯一**执行者（见 TRAY_EXE 的注释）──
        # ⚠️ `--state-dir` 必须与服务侧解析出**同一个值**，否则两边读的状态文件不是一份。
        #
        # 🔥 起之前先记下日志长度：诊断行要按"本次运行"归段读（托盘日志是**追加**的，
        #    机器上以前跑过的诊断还在同一个文件里）。
        tray_mark = tray_log_mark()
        tray_log = open(tray_log_path, "wb")
        tray_proc = subprocess.Popen([
            TRAY_EXE,
            "--service-name=ControlServerRemoteE2E",
            f"--state-dir={state_dir}",
        ], stdout=tray_log, stderr=subprocess.STDOUT, cwd=REPO, env=env)
        note("客户机托盘已起（抓屏 / 编码在这一侧）")

        def find_online():
            # 🔥 硬判据是 `connected`（长连接握上了），不是状态字符串 ——
            #    与 agent-smoke.py 同一口径：状态是"中心认为它怎样"，
            #    长连接是"它真的连着"。
            st, bd = http.call("GET", base_url + "/api/agents")
            if st != 200:
                return None
            for host in agents_of(bd):
                if host.get("connected") or host.get("state") == "online":
                    return host
            return None

        ok, host = wait_for(find_online, lambda h: h is not None, timeout=45.0, interval=1.5)
        check("客户机自动注册并上线（≤45s）", ok, f"日志：{agent_log_path}")
        if not ok:
            return 1
        agent_id = host.get("id")
        note(f"被控机：#{agent_id} {host.get('displayName')}")

        # 发现端口上的 UDP 上报晚于 HTTP 在线表，给一点点余量；
        # 紧接着等托盘的远控通道 —— 这两个都不是"断言"，是**前置条件**。
        time.sleep(1.0)
        check("托盘远控通道就绪（远控推流=可用）", wait_tray_ready(state_dir),
              f"托盘日志：{os.path.join(tempfile.gettempdir(), 'ControlServer.Agent.Tray.log')}")

        # ── 3.5 只跑 HTTP 探针时到此为止 ──
        if args.probe:
            # ⚠️ dwell 由 http_probe **内部**执行（见那里的注释）：本函数一看到 streaming
            #    就 DELETE 会话，在外面等只会等到一个已经收场的会话。
            return http_probe(base_url, http, agent_id, dwell=args.dwell)

        # ── 4. 浏览器：登录 → 远程桌面 → 建会话 → 看画面 ──
        note(f"进浏览器半边（probe={args.probe} pointer={not args.no_pointer}"
             f" chunk={args.chunk_threshold}KB idle={args.idle}s）")
        rc = run_browser(base_url, agent_id, headful=args.headful,
                         pointer=not args.no_pointer,
                         backdrops=args.backdrops, shapes=args.shapes,
                         metrics_seconds=args.metrics_seconds, tray_mark=tray_mark,
                         chunk_threshold_kb=args.chunk_threshold,
                         idle_seconds=args.idle, panels=args.panels)
        return rc
    finally:
        # ⚠️ 先收托盘：它持有抓屏与编码，也占着"通知区域里唯一一个图标"的单实例互斥。
        for p in (tray_proc, agent_proc, server_proc):
            if p is not None and p.poll() is None:
                try:
                    p.terminate()
                    p.wait(timeout=10)
                except Exception:
                    try:
                        p.kill()
                    except Exception:
                        pass
        server_log.close()
        agent_log.close()
        if tray_log is not None:
            tray_log.close()
        if not args.keep:
            for d in (data_dir, state_dir):
                shutil.rmtree(d, ignore_errors=True)
        print()
        total = len(STEPS)
        failed = len(FAILURES)
        print(f"  合计 {total} 项：通过 {total - failed}，失败 {failed}")

        # 🔴 **这一步不是锦上添花，它治的是一次真实的误诊**（M5.4-c① 步 6 实测）：
        #
        #    下面那句 `sys.exit` 在 `finally` 里 ⇒ 它<b>抛出的 SystemExit 会把
        #    正在传播的那个异常整个盖掉</b>（Python 不打印被 finally 覆盖的异常）。
        #    现场表现 = **"只跑了 4 项就正常退出 0，没有任何报错"** ——
        #    而真因（`ModuleNotFoundError: playwright` / Edge 起不来）一个字都看不见。
        #    当时是靠临时加 print 才把它逼出来的，代价是三轮。
        #
        #    ⇒ 判据是 `sys.exc_info()`：`finally` 执行时异常还没被处理，能拿到。
        exc = sys.exc_info()[1]
        if exc is not None:
            print()
            print("  [X] 脚本因异常中断（真因紧跟在下面，别被「合计 4 项」误导）：")
            # ⚠️ **必须显式 file=sys.stdout**：`traceback` 默认写 stderr，
            #    而重定向到文件时两个流各自缓冲 ⇒ traceback 会跑到**文件最前面**去，
            #    于是"下面才是真因"这句提示把人指到了空处（实测踩过）。
            traceback.print_exception(type(exc), exc, exc.__traceback__, file=sys.stdout)

        print(f"  服务端日志：{server_log_path}")
        print(f"  客户机日志：{agent_log_path}")
        print(f"  托盘日志：  {tray_log_path}")
        if FAILURES:
            print("  失败项：")
            for f in FAILURES:
                print("    - " + f)
        # ⚠️ 退出码只认失败数（"失败 0" 还不够 —— 见方案里那条挂账：
        #    集成程序集曾"中止但打印已通过"，所以真结论要看"总计 == 预期"）。
        # 🔥 异常也必须是失败：否则"崩溃"会被读成"全绿"。
        sys.exit(1 if (failed or exc is not None) else 0)


def _win32():
    import ctypes
    return ctypes


def get_cursor():
    ct = _win32()

    class POINT(ct.Structure):
        _fields_ = [("x", ct.c_long), ("y", ct.c_long)]

    p = POINT()
    ct.windll.user32.GetCursorPos(ct.byref(p))
    return (p.x, p.y)


def set_cursor(x, y):
    """移动真实光标（不是页面的虚拟鼠标），并**回读**确认。

    🔴 回读这一步不是多余：`SetCursorPos` 返回 true 也可能**根本没有移动**
       （被别的进程锁住、或坐标被夹到屏幕外）。只信 `GetCursorPos`。
       —— 与"构建报成功而产物是旧的"是同一个形状：回了成功 ≠ 真做成了。

    ⚠️ 为什么必须驱动**真实**光标而不是 `page.mouse`：
       `page.mouse` 动的是 headless 浏览器内部那个虚拟指针，被控端的抓屏
       **看不见它**。我们要验的恰恰是"被控端把真实光标合成进画面"。
    """
    ct = _win32()
    ct.windll.user32.SetCursorPos(int(x), int(y))
    time.sleep(0.25)
    return get_cursor()


# ═══════════════════════════════════════════════════════════════
# 宿主剪贴板（CF_UNICODETEXT）—— 验"把这段字发到被控机"时要能**从外面看**
# ═══════════════════════════════════════════════════════════════
#
# 🔴 为什么不用 PowerShell 的 `Get-Clipboard` / `Set-Clipboard`：
#    ① 它要过一次控制台码页，中文进来出去是两个方向各一次转码；
#    ② 子进程的 stdout 在 PS 5.1 里还会被折行；
#    ③ 本机那条"从 bash 里起 powershell"的路被安全策略挡着（实测被拦）。
#    直接用 Win32，与托盘那侧读剪贴板走的是同一套 API，也顺带避免
#    "测试用一个 API、被测代码用另一个 API"这种自欺。

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002


def _open_clipboard(timeout=5.0):
    """打开剪贴板。会被别的程序占着 ⇒ 必须重试，不能一次失败就判死。"""
    ct = _win32()
    deadline = time.time() + timeout
    while time.time() < deadline:
        if ct.windll.user32.OpenClipboard(None):
            return True
        time.sleep(0.05)
    return False


def host_clipboard_get(timeout=5.0):
    """读宿主剪贴板里的**文本**；没有文本（图片/空）时返回 `None`。

    ⚠️ `None` 与 `""` 是两件事，这里刻意不合并：被控端也是这么区分的
       （见面板上"没有文本"与"读失败"两个分支）。

    🔴 **`restype/argtypes` 一个都不能省**（2026-10-18 实测踩到）：
       `GetClipboardData` 与 `GlobalLock` 默认按 `c_int`（32 位）返回，
       而 64 位下它们是**指针**。句柄被截断后 `wstring_at` 读到的是野内存 ——
       症状极其误导：明明被控端日志写着「已写入剪贴板（20000 个字符）」，
       我们这边读出来却是 `''` 或 `None`，看着像"写入没生效"。
    """
    ct = _win32()
    ct.windll.user32.GetClipboardData.restype = ct.c_void_p
    ct.windll.user32.GetClipboardData.argtypes = [ct.c_uint]
    ct.windll.kernel32.GlobalLock.restype = ct.c_void_p
    ct.windll.kernel32.GlobalLock.argtypes = [ct.c_void_p]
    ct.windll.kernel32.GlobalUnlock.argtypes = [ct.c_void_p]

    if not _open_clipboard(timeout):
        return None
    try:
        handle = ct.windll.user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        ptr = ct.windll.kernel32.GlobalLock(handle)
        if not ptr:
            return None
        try:
            return ct.wstring_at(ptr)
        finally:
            ct.windll.kernel32.GlobalUnlock(handle)
    finally:
        ct.windll.user32.CloseClipboard()


def host_clipboard_set(text, timeout=5.0):
    """把一段文本写进宿主剪贴板。返回是否成功（写完**回读**核对过）。"""
    ct = _win32()
    ct.windll.kernel32.GlobalAlloc.restype = ct.c_void_p
    ct.windll.kernel32.GlobalAlloc.argtypes = [ct.c_uint, ct.c_size_t]
    ct.windll.kernel32.GlobalLock.restype = ct.c_void_p
    ct.windll.kernel32.GlobalLock.argtypes = [ct.c_void_p]
    ct.windll.kernel32.GlobalUnlock.argtypes = [ct.c_void_p]
    ct.windll.user32.SetClipboardData.argtypes = [ct.c_uint, ct.c_void_p]

    encoded = text.encode("utf-16-le") + b"\x00\x00"
    handle = ct.windll.kernel32.GlobalAlloc(GMEM_MOVEABLE, len(encoded))
    if not handle:
        return False
    ptr = ct.windll.kernel32.GlobalLock(handle)
    if not ptr:
        return False
    ct.memmove(ptr, encoded, len(encoded))
    ct.windll.kernel32.GlobalUnlock(handle)

    if not _open_clipboard(timeout):
        return False
    try:
        ct.windll.user32.EmptyClipboard()
        # ⚠️ 成功后所有权归系统，**不许** GlobalFree —— 提前释放会让别的程序粘贴到野指针。
        return bool(ct.windll.user32.SetClipboardData(CF_UNICODETEXT, handle))
    finally:
        ct.windll.user32.CloseClipboard()


# ═══════════════════════════════════════════════════════════════
# 被控端托盘日志：只读**本次运行**追加的那一段
# ═══════════════════════════════════════════════════════════════

def tray_log_file():
    return os.path.join(tempfile.gettempdir(), "ControlServer.Agent.Tray.log")


def tray_log_mark():
    """记下当前日志长度（**字节**），之后只看这之后追加的内容。

    🔥 为什么是字节而不是行数：日志是**追加**写的，按行数从头切会在
       "最后一行还没写完"这种边界上错位，而错位的那一行恰恰可能是我们要的诊断行。
       文件被外部重建（长度反而变短）时，`tray_log_since` 会回退成读全文。
    """
    try:
        return os.path.getsize(tray_log_file())
    except OSError:
        return 0


def tray_log_since(mark):
    try:
        with open(tray_log_file(), "rb") as fh:
            data = fh.read()
    except OSError:
        return []
    if mark and len(data) >= mark:
        data = data[mark:]
    return data.decode("utf-8", "replace").splitlines()


def tray_cursor_lines(mark):
    """本次运行里所有含「指针」的诊断行（原样字符串，含时间戳）。"""
    return [l for l in tray_log_since(mark) if "指针" in l]


# ═══════════════════════════════════════════════════════════════
# 画面 ↔ 屏幕坐标映射（"指哪打哪"的基础）
# ═══════════════════════════════════════════════════════════════

def fit_map(screen_w, screen_h, img_w, img_h):
    """画面是**等比缩放 + 居中**贴进 canvas 的（`object-fit: contain` 语义）。

    返回 `(vx, vy, view_w, view_h)` = 画面在截图里的位置与尺寸。

    🔴 这一步不是"为了精确"，而是**没有它就只能靠肉眼去找那一小块**：
       2160 宽的屏 → 约 850 px 的画，一个 12×20 的指指针缩完只剩 4~5 像素。
    """
    ar_screen = screen_w / screen_h
    ar_canvas = img_w / img_h
    if ar_canvas > ar_screen:          # 画布更"宽" ⇒ 左右留白
        view_h, view_w = img_h, int(img_h * ar_screen)
    else:                              # 画布更"高" ⇒ 上下留白
        view_w, view_h = img_w, int(img_w / ar_screen)
    return (img_w - view_w) // 2, (img_h - view_h) // 2, view_w, view_h


def img_to_screen(ix, iy, screen_w, screen_h, img_w, img_h):
    """截图坐标 → 屏幕坐标（把 `fit_map` 反过来）。"""
    vx, vy, vw, vh = fit_map(screen_w, screen_h, img_w, img_h)
    return (int((ix - vx) * screen_w / vw), int((iy - vy) * screen_h / vh))


def luma(path, box=None):
    """平均亮度（0..255），`box` = (l, t, r, b) 裁剪。"""
    from PIL import Image
    im = Image.open(path).convert("L")
    if box:
        im = im.crop(box)
    px = im.tobytes()
    return sum(px) / len(px) if px else 0.0


def region_diff(a_path, b_path, box, thr=25):
    """同一矩形内"超过阈值的像素数"。

    🔥 为什么必须**限定矩形**：整图差异会把"桌面上一扇窗口动了"算进来 ——
       而那种变化与指针毫无关系，却足以让判据通过（这就是一次假 PASS 的成因）。
    """
    from PIL import Image, ImageChops
    a = Image.open(a_path).convert("RGB").crop(box)
    b = Image.open(b_path).convert("RGB").crop(box)
    d = ImageChops.difference(a, b).convert("L").point(lambda v: 255 if v > thr else 0)
    return d.histogram()[255]


def region_signal_excl_noise(ref_path, noise_path, sig_path, box, thr=25):
    """**掩掉噪声**之后的信号像素数：`|ref − sig|` 里、且 `|ref − noise|` 没动过的那些。

    🔴 为什么非要这一步 —— 实测（本机）：
       · P1（屏幕正中心）那一框里，**静止不动时也有 123 px 在变**
         （编辑器里的光标闪烁 ＋ 文字区的高对比压缩噪声）；
       · 而"指针从那里消失"造成的信号是 198 px。
       两者**混在同一框里**，于是任何"信号 > 噪声×k"的判据都会误杀
       —— 这不是缺陷，是**判据的口径不对**（拿混合量去比单一量）。

    ⇒ 先算出"哪里在无端地动"，把这些像素从信号里**扣掉**，剩下的才是指针。
       同时把三个数都报出来，让人能自己判断扣得合不合理（透明优先于好看）。
    """
    from PIL import Image, ImageChops
    ia = Image.open(ref_path).convert("RGB").crop(box)
    inz = Image.open(noise_path).convert("RGB").crop(box)
    isg = Image.open(sig_path).convert("RGB").crop(box)
    noise = ImageChops.difference(ia, inz).convert("L").point(lambda v: 255 if v > thr else 0)
    sig = ImageChops.difference(ia, isg).convert("L")
    keep = noise.point(lambda v: 0 if v > thr else 255)          # 噪声处 0 ⇒ 扣掉
    masked = Image.composite(sig, Image.new("L", sig.size, 0), keep)
    return masked.point(lambda v: 255 if v > thr else 0).histogram()[255]


def zoom_pair_generic(a_path, b_path, box, out_path, scale=6, gap=10):
    """把同一个 box 从两张图里裁出来、并排放大落盘（人眼复核用）。"""
    from PIL import Image
    a = Image.open(a_path).convert("RGB").crop(box)
    b = Image.open(b_path).convert("RGB").crop(box)
    a = a.resize((a.width * scale, a.height * scale), Image.NEAREST)
    b = b.resize(a.size, Image.NEAREST)
    out = Image.new("RGB", (a.width * 2 + gap, a.height), (255, 0, 0))
    out.paste(a, (0, 0))
    out.paste(b, (a.width + gap, 0))
    out.save(out_path)
    return out_path


def diff_stats(a_path, b_path, thr=25):
    """两张图的差异（阈值化的像素数、包围盒、总像素）。"""
    from PIL import Image, ImageChops
    a = Image.open(a_path).convert("RGB")
    b = Image.open(b_path).convert("RGB")
    d = ImageChops.difference(a, b).convert("L")
    bw = d.point(lambda v: 255 if v > thr else 0)
    hist = bw.histogram()
    changed = hist[255] if len(hist) > 255 else 0
    return changed, bw.getbbox(), a.size[0] * a.size[1]


def diff_side_by_side(b_path, c_path, out_path, thr=25):
    """把 B、C 并排落盘，差异区域画红框 —— 这张图是给人复核用的。"""
    from PIL import Image, ImageChops, ImageDraw
    b = Image.open(b_path).convert("RGB")
    c = Image.open(c_path).convert("RGB")
    d = ImageChops.difference(b, c).convert("L").point(lambda v: 255 if v > thr else 0)
    box = d.getbbox()

    gap = 8
    out = Image.new("RGB", (b.size[0] * 2 + gap, b.size[1]), (30, 30, 30))
    out.paste(b, (0, 0))
    out.paste(c, (b.size[0] + gap, 0))
    if box:
        dr = ImageDraw.Draw(out)
        for ox in (0, b.size[0] + gap):
            dr.rectangle([box[0] + ox, box[1], box[2] + ox, box[3]], outline=(255, 0, 0), width=3)
    out.save(out_path)
    return box


def wait_tray_ready(state_dir, timeout=40.0):
    """等托盘把**会话侧指令通道**架起来（`远控推流=可用`）。

    🔴 **不等会得到一种非常像"客户端坏了"的失败**：Service 注册上线只要几百毫秒，
       而托盘的通道要一两秒。这中间建会话，指令发到一条**还没人接的管道**上 ——
       中心侧一切正常（200、协商齐全、登记成功），被控端一个字都不报，
       20 秒后被巡检按"没有任何状态上报"清掉。

    ⚠️ 判据取**本次那几行**（按 state_dir 定位），因为托盘日志是**追加**的：
       机器上以前跑过的记录也在同一个文件里。
    """
    path = os.path.join(tempfile.gettempdir(), "ControlServer.Agent.Tray.log")
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                txt = f.read()
        except OSError:
            txt = ""
        if state_dir in txt:
            tail = txt[txt.rindex(state_dir):]
            if "会话侧指令通道已启动" in tail and "远控推流=可用" in tail:
                return True
        time.sleep(0.5)
    return False


def dump_remote_logs(base_url, http, count=200):
    """把中心「系统日志」里 Remote 类的条目打出来。

    🔴 **这是排查会话生命周期的唯一入口**：`Close` 里那句
       `[Remote] 会话 {id} 已结束（{reason}）` 会带着**原因**进这里。
       页面上的 `state` 只会说"没有了"，日志才说"为什么没有"。
    """
    st, body = http.call("GET", f"{base_url}/api/logs/recent?count={count}")
    if st != 200:
        print(f"    （拉日志失败 {st}）")
        return
    rows = agents_of(body)
    hits = [r for r in rows if isinstance(r, dict)
            and ("Remote" in str(r.get("category", "")) or "Remote" in str(r.get("message", "")))]
    if not hits:
        print("    （系统日志里没有 Remote 类的条目）")
        return
    print(f"    ── 中心日志（Remote，{len(hits)} 条）──")
    for r in hits[-25:]:
        msg = " ".join(str(r.get("message", "")).split())
        print(f"      [{r.get('level')}] {msg[:300]}")


def http_probe(base_url, http, agent_id, dwell=0.0):
    """纯 HTTP 建会话 + 轮询状态。

    🔴 **它的用处是把范围切开**：画面出不来时，先要知道是"被控端压根没推流"
       还是"推了、浏览器没解出来"。浏览器那一侧一黑屏什么都看不出来，
       而这一侧的 `state` / `endedReason` / `stats` 会把话说明白。

    ⚠️ `viewerVideo` 必须传 —— 它是协商三项输入里的第二项。这里**手工**声明
       "我这边能显示 h264"，等于扮演一个"支持 H.264 的观看端"
       （浏览器探针在生产里会自动得出同样的结论，见 remoteCodecProbe.ts）。
    """
    print()
    print("── HTTP 侧建会话探针 ──")

    body = {
        "agentId": agent_id,
        "screenId": None,
        "displayMode": "fit",
        "maxFps": None,
        "viewerVideo": {"declared": True, "codecs": ["h264", "mjpeg"], "packaging": "inband"},
    }
    st, dto = http.call("POST", base_url + "/api/remote/sessions", body)
    print(f"  POST /api/remote/sessions → {st}")
    if not isinstance(dto, dict):
        print(f"    响应：{str(dto)[:300]}")
        return 1
    print(f"    sessionId={dto.get('sessionId')} state={dto.get('state')}")
    sid = dto.get("sessionId")

    last_state = None
    gone = False
    for _ in range(45):
        time.sleep(1.0)
        st, cur = http.call("GET", f"{base_url}/api/remote/sessions/{sid}")
        if st != 200 or not isinstance(cur, dict):
            if not gone:
                gone = True
                msg = cur.get("message") if isinstance(cur, dict) else cur
                print(f"  → 会话已从账本上消失（HTTP {st}）：{str(msg)[:200]}")
                dump_remote_logs(base_url, http)
            continue
        state = cur.get("state")
        if state != last_state:
            print(f"  → state = {state}"
                  f"  codec={cur.get('codec')}  note={cur.get('codecNote')}"
                  f"  endedReason={cur.get('endedReason')}")
            last_state = state
        if state in ("streaming", "failed", "ended", "stopped"):
            st2 = cur.get("stats") or {}
            ast = cur.get("agentStats") or {}
            print(f"    中心侧：frames={st2.get('framesReceived')} bytes={st2.get('bytesReceived')}"
                  f" fps={st2.get('fps')} dropped={st2.get('droppedFrames')}")
            print(f"    被控端：{json.dumps(ast, ensure_ascii=False)[:300]}")
            if state == "streaming":
                check("被控端开始推流（HTTP 侧建会话）", True, f"codec={cur.get('codec')}")

                # ── 额外停留（--dwell）──
                #
                # 🔥🔥 **它必须在这里、也就是 DELETE 之前**：本函数一看到 `streaming`
                #    就 DELETE ⇒ 被控端的推流循环立刻收场（实测**会话只活 0.6 秒**），
                #    而那行"每 5 秒一次"的指针诊断永远等不到第二个采样点。
                #    放在函数外面等 = 等一个已经结束的会话（第一次就是这么白等的）。
                #
                # ⚠️ 停留期间**持续轮询**：既能把帧数增长看在眼里，也顺带证伪
                #    "中心嫌没人看就把会话清了"那种猜测。
                if dwell > 0:
                    note(f"额外停留 {dwell:g} 秒（指针诊断每 5 秒一行；期间持续轮询）…")

                    deadline = time.monotonic()

                    # 🔥 停留期间**主动挪光标**：指针诊断那一行带着"这一次合成用的入参"
                    #    （指针位置 ＋ 算出的小矩形）。光标挪了而它没跟着变 ⇒ 输入侧就是坏的；
                    #    输入跟着变而画面里没有 ⇒ 问题在往输出上写那一刀。
                    #    ⇒ 这一步把"合成链"切成两段，不必再靠猜。
                    #
                    # ⚠️ 两个落点都在画面内且相隔很远（1366 的横向差距 ⇒ 小矩形不可能重合）；
                    #    判据一律以 `GetCursorPos` **回读**为准。
                    moves = [(2.0, 300, 300), (8.0, 1800, 1100)]
                    moved = set()

                    while True:
                        elapsed = time.monotonic() - deadline

                        if elapsed >= dwell:
                            break

                        time.sleep(1.0)

                        elapsed = time.monotonic() - deadline

                        for at, mx, my in moves:
                            if at <= elapsed and at not in moved:
                                moved.add(at)

                                before = get_cursor()
                                after = set_cursor(mx, my)

                                print(f"    · 挪光标 → ({mx},{my})  回读 {after}（挪之前 {before}）")

                        st3, cur3 = http.call("GET", f"{base_url}/api/remote/sessions/{sid}")

                        if st3 != 200:
                            print(f"    ⚠️ 停留期间会话从账本上消失（HTTP {st3}）—— 被控端会跟着收场")
                            break

                        s3 = cur3.get("stats") or {}

                        print(f"    · t={elapsed:4.1f}s"
                              f"  state={cur3.get('state')}"
                              f"  frames={s3.get('framesReceived')}"
                              f"  bytes={s3.get('bytesReceived')}"
                              f"  dropped={s3.get('droppedFrames')}")

                    st4, cur4 = http.call("GET", f"{base_url}/api/remote/sessions/{sid}")

                    if st4 == 200:
                        print("    被控端（停留后）："
                              f"{json.dumps(cur4.get('agentStats') or {}, ensure_ascii=False)[:300]}")

                http.call("DELETE", f"{base_url}/api/remote/sessions/{sid}")
                return 0
            check("被控端开始推流（HTTP 侧建会话）", False,
                  f"state={state} endedReason={cur.get('endedReason')}")
            return 1

    dump_remote_logs(base_url, http)
    check("被控端开始推流（HTTP 侧建会话）", False,
          f"45s 内状态没变（最后 {last_state}）—— 指令可能没到被控端")
    return 1


def verify_pointer(page, out_dir):
    """指针验证：三段对照（不动 / 不动 / 移动）＋ 深·浅底受控实验。

    自动判据部分：
      · 光标**不动**时画面几乎无变化 —— 否则"移动后变了"什么都证明不了
        （整屏都在刷新时，指针那点像素变化淹没在噪声里）；
      · 移动光标后出现**局部**变化 —— 证明有个小东西真的跟着动。

    ⚠️「那个小东西就是指针」由**人看落盘的图**确认（分工：脚本驱动、人复核）。
       红框标出了差异区，指针就在框里。
    """
    print()
    print("── 指针验证（脚本驱动光标 ＋ 落盘供人复核）──")

    ct = _win32()
    sw = ct.windll.user32.GetSystemMetrics(0)
    sh = ct.windll.user32.GetSystemMetrics(1)
    note(f"本机逻辑屏幕：{sw} × {sh}")

    original = get_cursor()
    canvas = page.locator("canvas").first
    marker_proc = None

    def shot(tag):
        path = os.path.join(out_dir, f"pointer-{tag}.png")
        canvas.screenshot(path=path)
        return path

    def goto(fx, fy):
        want = (int(sw * fx), int(sh * fy))
        return want, set_cursor(*want)

    try:
        # ── ① 三段对照 ──
        #
        # 🔥 光标放在**屏幕正中心**：等比缩放 ＋ 居中显示时，屏幕中心必定落在画面中心。
        #    这样即使不知道精确缩放比，也能**指哪打哪**地去放大那一小块 ——
        #    而"精确缩放比"恰恰是最难可靠拿到的（canvas 的 CSS 尺寸与画面尺寸是两回事）。
        p1, p2 = (0.5, 0.5), (0.5, 0.20)

        w1, g1 = goto(*p1)
        note(f"光标 → {w1}（回读 {g1}）")
        time.sleep(2.5)
        f_a = shot("a-p1")

        _, g2 = goto(*p1)
        note(f"光标原地不动（回读 {g2}）")
        time.sleep(1.5)
        f_b = shot("b-p1-still")

        w2, g3 = goto(*p2)
        note(f"光标 → {w2}（回读 {g3}）")
        time.sleep(2.5)
        f_c = shot("c-p2")

        still_px, still_box, total = diff_stats(f_a, f_b)
        move_px, move_box, _ = diff_stats(f_b, f_c)
        note(f"不动时（A vs B）：{still_px} px（{still_px / total * 100:.3f}%）bbox={still_box}")
        note(f"移动后（B vs C）：{move_px} px（{move_px / total * 100:.3f}%）bbox={move_box}")

        note("⚠️「移动后全图变化 > 静止时全图变化 ×3」这条**不拿来当判据**："
             f"实测本机静止时也有 {still_px} px 在动（编辑器里的光标在闪 ＋ 文字区的"
             "压缩噪声），而它与指针的信号**混在同一小块里** ⇒ 全图尺度上根本分不开。"
             "硬判据在下面的「定点小框 ＋ 扣噪声」里。")
        check("画面是稳的（光标不动 ⇒ 全图变化 < 2%）",
              still_px < total * 0.02, f"{still_px}/{total} px")
        check("变化是局部的（不是整屏刷新）", move_px < total * 0.25, f"{move_px / total * 100:.2f}%")

        diff_side_by_side(f_b, f_c, os.path.join(out_dir, "pointer-diff.png"))

        # ── ② 定点放大对照：这张才是"指针到底有没有被合成"的证据 ──
        #
        #    画面是等比缩小过的（2160 宽的屏 → 约 850 px 的画），一个 12×20 的指针
        #    缩完只剩 4~5 像素，H.264 再糊一层，**在整张图上根本看不见**。
        #    所以必须裁一小块、放大若干倍再看。
        from PIL import Image
        iw, ih = Image.open(f_a).size
        ar_screen = sw / sh
        ar_canvas = iw / ih
        if ar_canvas > ar_screen:      # 画布更"宽" ⇒ 左右留白
            view_h, view_w = ih, int(ih * ar_screen)
        else:                          # 画布更"高" ⇒ 上下留白
            view_w, view_h = iw, int(iw / ar_screen)
        vx, vy = (iw - view_w) // 2, (ih - view_h) // 2
        note(f"截图 {iw}×{ih}，推断画面区 {view_w}×{view_h} @ ({vx},{vy})"
             "（fit 等比缩放 + 居中）")

        def zoom_pair(cx, cy, out_name, half=70, scale=6):
            ca = Image.open(f_a).convert("RGB")
            cc = Image.open(f_c).convert("RGB")
            box = (max(0, cx - half), max(0, cy - half),
                   min(iw, cx + half), min(ih, cy + half))
            za = ca.crop(box).resize(((box[2] - box[0]) * scale, (box[3] - box[1]) * scale),
                                     Image.NEAREST)
            zc = cc.crop(box).resize(za.size, Image.NEAREST)
            out = Image.new("RGB", (za.size[0] * 2 + 10, za.size[1]), (255, 0, 0))
            out.paste(za, (0, 0))
            out.paste(zc, (za.size[0] + 10, 0))
            path = os.path.join(out_dir, out_name)
            out.save(path)
            return path

        c1x, c1y = vx + int(0.5 * view_w), vy + int(0.5 * view_h)
        c2x, c2y = vx + int(0.5 * view_w), vy + int(0.2 * view_h)
        z1 = zoom_pair(c1x, c1y, "pointer-zoom-at-p1.png")
        z2 = zoom_pair(c2x, c2y, "pointer-zoom-at-p2.png")
        note(f"放大对照（左=光标在 P1 / 右=光标在 P2）："
             f"{os.path.basename(z1)}（P1 处）、{os.path.basename(z2)}（P2 处）")

        # P1 与 P2 两小块里的差异 —— 比全图判据硬得多：
        # 窗口变化不在这一小块里，所以这里"有变化"只可能是指针。
        #
        # 🔴 但**光有小框还不够**：实测 P1（屏幕正中心）那一框里，**静止不动时也有
        #    123 px 在变**（编辑器里的光标闪烁 ＋ 文字区的压缩噪声），而"指针从那里消失"
        #    造成的信号是 198 px —— 两者**混在同一框里**。所以先把"无端在动"的像素扣掉
        #    （`region_signal_excl_noise`），剩下的才是指针。三个数（原始 / 噪声 / 扣后）
        #    全部报出来，让人能自己判断扣得合不合理。
        def box_of(cx, cy, half=45):
            return (max(0, cx - half), max(0, cy - half),
                    min(iw, cx + half), min(ih, cy + half))

        p1_box, p2_box = box_of(c1x, c1y), box_of(c2x, c2y)
        sigs = {}
        for label, box in (("P1", p1_box), ("P2", p2_box)):
            raw = region_diff(f_a, f_c, box)
            nz = region_diff(f_a, f_b, box)
            sig = region_signal_excl_noise(f_a, f_b, f_c, box)
            sigs[label] = sig
            note(f"{label} 处：原始差异 {raw} px / 同点静止噪声 {nz} px ⇒ 扣噪声后 {sig} px")

        check("光标离开 P1 ⇒ 该点扣掉噪声后仍有信号（指针从那里消失）",
              sigs["P1"] >= 12, f"{sigs['P1']} px")
        check("光标到达 P2 ⇒ 该点扣掉噪声后仍有信号（指针出现在那里）",
              sigs["P2"] >= 12, f"{sigs['P2']} px")
    finally:
        try:
            set_cursor(*original)   # 把人家的光标放回原处
        except Exception:
            pass


# ═══════════════════════════════════════════════════════════════
# ⑥ 读数：帧率 / 码率 / 丢帧 / 帧时延
# ═══════════════════════════════════════════════════════════════

STAT_LABELS = ("画面分辨率", "中心实测帧率", "被控端实测帧率", "桌面静止", "本机已画",
               "单帧大小", "累计码流", "被丢弃的帧", "帧时延")

# 「桌面静止」那一格的标签（`RemoteDesktopPanel.vue`）—— 只在这里写一次字面量，
# 免得判据里到处散落中文。
IDLE_CARD = "桌面静止"


def read_cards(page, labels=STAT_LABELS):
    """把观看页上那排 `.rd-stat` 读成 `{标签: 值}`。

    🔥 必须**按标签找**，不能按下标取：页面上有**两组**同构卡片（画面 / 音频），
       而音频那组是"接上了才显示" —— 按下标取会在音频出现的那一刻整体错位，
       于是"帧率"读成"码率"，而读数本身看起来完全正常。
    """
    return page.evaluate("""(labels) => {
      const out = {};
      for (const e of document.querySelectorAll('.rd-stat')) {
        const l = e.querySelector('span');
        const v = e.querySelector('strong');
        if (l && v && labels.includes(l.textContent.trim())) {
          out[l.textContent.trim()] = v.textContent.trim();
        }
      }
      return out;
    }""", list(labels))


def parse_num(text):
    """从 `12.3 fps` / `205.3 KB` / `1.2 MB` / `1,234 帧` / `—` 里取数；取不到 ⇒ None。"""
    if not text or text.strip() in ("—", "-"):
        return None
    m = re.search(r"(-?[\d.]+)", text.replace(",", ""))
    if not m:
        return None
    v = float(m.group(1))
    t = text.upper()
    if "KB" in t:
        v *= 1024
    elif "MB" in t:
        v *= 1024 * 1024
    elif "GB" in t:
        v *= 1024 ** 3
    return v


def read_session_via_page(page):
    """经**页面自己**的 `fetch` 读会话快照（顺带证明"页面手里的凭据够用"）。

    ⚠️ `/api/remote/sessions` 返回 `{items, total, online}` 而**不是数组** ——
       直接当数组遍历会**静默**拿到空列表（这是本脚本踩过的第一个坑）。
    """
    return page.evaluate("""async () => {
      try {
        const r = await fetch('/api/remote/sessions', { credentials: 'include' });
        if (!r.ok) return { error: 'HTTP ' + r.status };
        const j = await r.json();
        const items = j.items ?? (j.list && j.list.items) ?? (Array.isArray(j) ? j : []);
        const s = items.find(x => x.state === 'streaming') || items[0] || null;
        if (!s) return { error: '会话列表为空' };
        const st = s.stats || {};
        const ag = s.agentStats || {};
        return {
          sessionId: s.sessionId, state: s.state, codec: s.codec,
          framesReceived: st.framesReceived, bytesReceived: st.bytesReceived,
          fps: st.fps, lastFrameBytes: st.lastFrameBytes, droppedFrames: st.droppedFrames,
          // 🔥 分片那两个数（M5.4-c① 步 6）：`reassembledFrames` 是"拼出来的帧数"，
          //    它与 `framesReceived` 并排才能回答"分片到底有没有在工作"。
          reassembledFrames: st.reassembledFrames,
          reassemblyDroppedFrames: st.reassemblyDroppedFrames,
          agentFramesSent: ag.framesSent, agentBytesSent: ag.bytesSent, agentFps: ag.fps,
        };
      } catch (e) { return { error: String(e) }; }
    }""")


def collect_metrics(page, seconds=8.0, interval=1.0, out_dir=OUT_DIR):
    """⑥ 帧率 / 码率 / 丢帧 / 帧时延 —— **页面上显示的读数**，并与中心接口交叉核对。

    🔥 为什么两个来源都要：页面那排卡片是"给人看的数"，中心接口是"数据本身"。
       只看一边时，永远分不清"画面卡"是"真的没帧"还是"页面没刷新"。
    ⚠️ 口径**照抄页面**（页面上已经有一个「帧时延」）—— 不另造第二个定义，
       否则同一个量在页面上会有两个不一样的值。
    """
    print()
    print("── 读数（帧率 / 码率 / 丢帧 / 帧时延）──")

    samples = []
    t0 = time.time()
    while time.time() - t0 < seconds:
        cards = read_cards(page)
        sess = read_session_via_page(page)
        samples.append((time.time() - t0, cards, sess))
        t, c = samples[-1][0], samples[-1][1]
        print(f"    · t={t:4.1f}s  中心 {c.get('中心实测帧率')} / 被控端 {c.get('被控端实测帧率')}"
              f"｜单帧 {c.get('单帧大小')}｜丢帧 {c.get('被丢弃的帧')}"
              f"｜帧时延 {c.get('帧时延')}")
        time.sleep(interval)

    if not samples:
        check("采到读数样本", False, "一个都没采到")
        return

    def col(name):
        """某张卡片的数值序列。

        ⚠️ 取不到的点**直接丢掉**，不补 0 —— 补 0 会把均值拉低，看起来像"帧率不稳"。
        """
        vs = [parse_num(c.get(name)) for _, c, _ in samples]
        return [v for v in vs if v is not None]

    def rng(vs):
        return (min(vs), sum(vs) / len(vs), max(vs)) if vs else None

    first, last = samples[0], samples[-1]
    dt = last[0] - first[0]
    adv, agv = rng(col("中心实测帧率")), rng(col("被控端实测帧率"))
    fr, ar = rng(col("帧时延")), rng(col("单帧大小"))

    if adv:
        note(f"中心实测帧率：min {adv[0]:.1f} / avg {adv[1]:.1f} / max {adv[2]:.1f} fps")
    if agv:
        note(f"被控端实测帧率：min {agv[0]:.1f} / avg {agv[1]:.1f} / max {agv[2]:.1f} fps")
    if ar:
        note(f"单帧大小：min {ar[0] / 1024:.1f} / avg {ar[1] / 1024:.1f} / max {ar[2] / 1024:.1f} KB")
    if fr:
        note(f"帧时延（页面上那个）：min {fr[0]:.0f} / avg {fr[1]:.0f} / max {fr[2]:.0f} ms")

    # ── 交叉核对：中心那边两次数出来的"实际速率" vs 卡片上写的 fps ──
    ns = [s.get("framesReceived") for _, _, s in samples
          if isinstance(s, dict) and isinstance(s.get("framesReceived"), (int, float))]
    rate = None
    if len(ns) >= 2 and dt > 0:
        rate = (ns[-1] - ns[0]) / dt
        note(f"中心侧实测：{ns[0]} → {ns[-1]} 帧 / {dt:.1f}s ⇒ 实际 {rate:.1f} fps"
             f"（卡片上写 {last[1].get('中心实测帧率')}）")

    check("页面读数里「画面分辨率」不是空的",
          bool(last[1].get("画面分辨率")) and last[1].get("画面分辨率") != "—",
          f"实得 {last[1].get('画面分辨率')}")
    both_fps = bool(adv) and bool(agv) and adv[1] > 0 and agv[1] > 0

    # 🔥 这条以前是"只记录、不当判据"（那是产品缺陷在时**刻意**的做法：
    #    假 FAIL 比不做断言更坏）。现在缺陷修了 —— 被控端在**推流期间会每秒上报一次计数**
    #    （`RemoteSessionCoordinator.PumpAsync` 里那条周期上报）⇒ **升回硬判据**。
    #
    #    它守的是什么：那一格与"中心收了多少帧"是**并排看**才有意义的一对 ——
    #    被控端发 500 / 中心收 12 ⇒ 链路上在掉；被控端发 3 / 中心收 3 ⇒ 是被控端编不动。
    #    只给一个数时这两种故障长得一模一样，而处置相反（治网络 / 治编码）。
    check("两路帧率都 > 0（中心侧 / 被控端侧各有产出）", both_fps,
          f"中心 {adv[1] if adv else '—'} / 被控端 {agv[1] if agv else '—'} fps"
          "（被控端那一路靠推流期间的**周期上报**，每秒一条）")
    check("中心侧帧数单调不倒退（会话没被重建过）",
          len(ns) >= 2 and all(ns[i] <= ns[i + 1] for i in range(len(ns) - 1)),
          f"{ns[0] if ns else '?'} → {ns[-1] if ns else '?'}")
    if rate is not None and adv:
        dev = abs(rate - adv[1]) / max(adv[1], 1e-6)
        check("「实际速率」与卡片上写的 fps 自洽（差 < 60%）", dev < 0.6,
              f"实际 {rate:.1f} vs 卡片 {adv[1]:.1f}（差 {dev * 100:.0f}%）")
    # ── 被控端那一路的计数 ──
    #    🔥 它是**独立的一条通道**：中心收到的 ≠ 被控端发出的。
    #       只有一边有数 = 异常，而"画面卡"与"声音断了"在只给一个总数时长得一模一样。
    ag = [s for _, _, s in samples if isinstance(s, dict) and not s.get("error")]
    if ag:
        la = ag[-1]
        note(f"中心接口里的被控端数据：framesSent={la.get('agentFramesSent')}"
             f" / bytesSent={la.get('agentBytesSent')} / fps={la.get('agentFps')}"
             f"（同一次快照里 中心侧 framesReceived={la.get('framesReceived')}）")
    else:
        note("⚠️ 一次都没读到中心接口的会话快照 —— 页面上的卡片值可能全是旧值")
    note(f"页面卡片「被控端实测帧率」显示的是：{last[1].get('被控端实测帧率')}")

    # ── 丢帧 ──
    # ⚠️ **不拿「== 0」当判据**：建流头一两帧排队溢出是链路的正常代价
    #    （本步实测就是"第 1 秒丢 1 帧、之后一直是 1"）。真正该盯的是
    #    "它还在不在涨" —— 一直涨才说明链路跟不上。
    dr_seq = [v for v in (parse_num(c.get("被丢弃的帧")) for _, c, _ in samples)
              if v is not None]
    dr_last = dr_seq[-1] if dr_seq else None
    # ⚠️ 判**后半段**有没有涨，而不是"首末比较"：本步实测序列是 `0,1,1,1,1,1,1,1`
    #    —— 那是**建流初期的一次性排队溢出**，不是"链路持续跟不上"。
    #    拿首末比会把它误判成"还在涨"（第一次采样恰好取在溢出之前）。
    half = len(dr_seq) // 2
    dr_grew_late = len(dr_seq) >= 4 and dr_seq[-1] > dr_seq[half]
    check("被丢弃的帧在采样窗口的**后半段不再增长**", not dr_grew_late, f"序列 {dr_seq}")
    if dr_last:
        note(f"被丢弃的帧最终是 {dr_last}（非 0 ⇒ 如实记下：建流初期有过排队溢出）")
        if ns:
            ratio = dr_last / max(ns[-1], 1)
            check("丢帧占比 < 10%", ratio < 0.10,
                  f"{dr_last} / {ns[-1]} 帧 = {ratio * 100:.1f}%")
    else:
        note("被丢弃的帧最终是 0（理想）")
    if fr:
        check("帧时延可用且落在合理区间（>0 且 <2000ms）",
              fr[0] > 0 and fr[2] < 2000, f"{fr[0]:.0f}~{fr[2]:.0f} ms")
    else:
        check("帧时延可用（不是「时钟未同步」）", False, f"实得 {last[1].get('帧时延')}")

    path = os.path.join(out_dir, "metrics.csv")
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write("t_s," + ",".join(STAT_LABELS)
                 + ",api_frames_received,api_bytes_received,api_fps\n")
        for t, c, s in samples:
            api = s if isinstance(s, dict) else {}
            fh.write(f"{t:.2f},"
                     + ",".join(f'"{str(c.get(k, ""))}"' for k in STAT_LABELS)
                     + f",{api.get('framesReceived', '')},{api.get('bytesReceived', '')},"
                       f"{api.get('fps', '')}\n")
    note(f"采样落盘：{os.path.relpath(path, REPO)}")

    if any(isinstance(s, dict) and s.get("error") for _, _, s in samples):
        note("⚠️ 有采样点读不到会话接口（页面 fetch 失败）—— 上面那几条「自洽」判据要打折看")

    # ⚠️ 诚实边界：本机是**单机自环** ⇒ 两侧时钟是同一台机器，
    #    所以「帧时延」这条**不能**用来证明"跨机时钟同步可用"（那是另一条挂账）。
    note("⚠️ 单机自环：两侧时钟同源，页面的「帧时延」在此**不能**证明跨机时钟同步")


# ═══════════════════════════════════════════════════════════════
# ⑦ 分片（`--chunk-threshold`）—— M5.4-c① 步 6
# ═══════════════════════════════════════════════════════════════

def verify_chunking(page, threshold_kb):
    """🔥 **诊断模式下"分片这条路真的走通了"的硬判据。**

    🔴 为什么这条判据必须存在（而不是"跑了一遍没报错就算过"）：
       阈值降下来之后，如果那个旋钮**没有接线**（或者环境变量名字打错一个字），
       整轮冒烟会安安静静地跑**老路**，画面一切正常，然后报绿 ——
       那是一次**假 PASS**，而它会让人以为分片在真机上验证过了。

    判据（三个数必须同时成立）：

      ① `reassembledFrames > 0` —— 中心**真的把多片拼成了帧**（这是唯一一条
         能证明"走了分片"的证据；本机没有 8K 屏，正常帧永远走老路）；
      ② `framesReceived > 0` —— 拼出来的帧**真的进了转发链**（不是拼完就丢了）；
      ③ `reassemblyDroppedFrames` 不涨 —— 拼的过程中没有因为缺片而丢帧。

    ⚠️ 第 ③ 条与「画面不花」是同一件事的两面：丢一帧 = 参考链上戳个洞，
       而解码器不会报错（只是从那一刻起每帧都花）。
    """
    print()
    print(f"── 分片（诊断模式：阈值已降到 {threshold_kb} KB）──")

    sess = read_session_via_page(page)

    if not isinstance(sess, dict) or sess.get("error"):
        check("读得到会话统计（分片判据的前提）", False, f"实得 {sess}")
        return

    assembled = sess.get("reassembledFrames")
    received = sess.get("framesReceived")
    dropped = sess.get("reassemblyDroppedFrames")

    note(f"中心：收到帧 {received}｜**拼出来的帧** {assembled}｜重组丢弃 {dropped}")

    check(
        f"中心真的把多片拼成了帧（reassembledFrames > 0，阈值 {threshold_kb} KB）",
        isinstance(assembled, (int, float)) and assembled > 0,
        f"实得 {assembled} —— 若为 0 或 None，说明这一轮**根本没走分片**"
        f"（阈值没接线 / 环境变量没传进去 / 帧比阈值还小），"
        f"而画面照样正常 ⇒ 这就是一次假 PASS")

    check(
        "拼出来的帧进了转发链（framesReceived > 0）",
        isinstance(received, (int, float)) and received > 0,
        f"实得 {received}")

    check(
        "重组没有因为缺片丢帧（reassemblyDroppedFrames == 0）",
        not dropped,
        f"实得 {dropped} —— 每丢一帧，观看端都会从那一刻花到下一个关键帧，而解码器不报错")


# ═══════════════════════════════════════════════════════════════
# ⑥ 三个前端入口（`--panels`）：抓哪块屏 / 质量档 / 剪贴板
# ═══════════════════════════════════════════════════════════════
#
# 🔴 这一节治的是「代码写了、类型过了、产物里有标签，但**浏览器里从没跑过**」。
#    这三个入口的判据全在**渲染与交互**上：vitest 只覆盖纯函数
#    （`remotePlayback` / `webCodecsDecoder` / `tabState`…），一行模板都没跑到。
#    而它们恰恰有三个"画错了也不报错"的典型：
#      · 屏下拉默认项写成主屏 ⇒ 用户以为"多屏拼接坏了"；
#      · 质量档的 badge 把 `null` 画成「自动」⇒ 把"读不到"写成"它是自动"；
#      · 剪贴板把"没有文本"与"读失败"画成同一句 ⇒ 让人去查一个没坏的东西。
#
# ⚠️ 判据全部落在**页面上的文字 + 后端返回值 + 宿主剪贴板**三处交叉，
#    不看"有没有抛异常" —— 没抛异常只说明请求发出去了。

QUALITY_LABELS = {"auto": "自动", "low": "低", "balanced": "均衡", "high": "高"}

_SESSION_STATE_JS = """async () => {
  const r = await fetch('/api/remote/sessions');
  if (!r.ok) return { ok: false, status: r.status };
  const list = await r.json();
  const live = (list || []).filter(s => !s.endedAt);
  if (!live.length) return { ok: false, status: 'no-live-session' };
  const s = live[0];
  return { ok: true, sessionId: s.sessionId, state: s.state, quality: s.quality ?? null };
}"""

def _text_of(locator):
    """取元素文本；元素不在就返回 None（**不要**抛 —— 断言里要能说出"没这个元素"）。"""
    try:
        if locator.count() == 0:
            return None
        return locator.first.inner_text().strip()
    except Exception:
        return None


def verify_panels(page, out_dir):
    """M5.5 三个前端入口的浏览器实证。返回失败数（与其它 verify_* 一致，直接改全局）。"""
    print()
    print("── 三个前端入口（多屏 / 质量档 / 剪贴板）：浏览器实证 ──")

    # ══ ① 抓哪块屏 ══
    sel = page.locator('label:has-text("抓哪块屏") select')
    has_sel = sel.count() == 1
    check("有「抓哪块屏」下拉", has_sel)
    if has_sel:
        opts = []
        try:
            opts = sel.evaluate(
                "el => [...el.options].map(o => ({ v: o.value, t: o.textContent.trim() }))")
            idx = sel.evaluate("el => el.selectedIndex")
        except Exception as ex:
            idx = -1
            note(f"读下拉失败：{type(ex).__name__}: {ex}")

        first = opts[0]["t"] if opts else ""
        check("下拉第一项是「自动」（默认项不许写成主屏）", "自动" in first, f"实得「{first}」")
        check("默认选中的就是「自动」", idx == 0, f"selectedIndex={idx}")
        check("屏清单有内容（自动 ＋ ≥1 块屏）", len(opts) >= 2,
              f"实得 {len(opts)} 项：" + " / ".join(o["t"] for o in opts))

        missing = [o["t"] for o in opts[1:] if not re.search(r"\d+\s*[×x]\s*\d+", o["t"])]
        check("每块屏都带分辨率（不给分辨率，用户无从判断抓的是哪块）", not missing,
              f"缺少分辨率的项：{missing}")

        if len(opts) >= 3:
            note(f"这台机器有 {len(opts) - 1} 块屏 —— 多屏选项真的被渲染出来了")
        else:
            note("这台机器只有 1 块屏 ⇒ 只能验证「自动」与单屏两项；"
                 "多屏拼接本身的正确性要在多屏机器上验（如实记录，不算失败）")

        before = len(opts)
        page.click('button:has-text("重读屏")')
        ok_reload, disabled = wait_for(
            lambda: sel.evaluate("el => el.disabled"), lambda d: d is False,
            timeout=25.0, interval=0.25)
        opts2 = []
        try:
            opts2 = sel.evaluate("el => [...el.options].map(o => o.textContent.trim())")
        except Exception:
            pass
        check("「重读屏」点完回到可点状态（不是永远「读取中…」）", ok_reload,
              "25s 内下拉一直是 disabled")
        check("重读屏后清单没有变空/变形", len(opts2) == before,
              f"重读前 {before} 项 → 重读后 {len(opts2)} 项")

    # ══ ② 质量档 ══
    qbox = page.locator(".rd-quality")
    check("有「质量档」区块", qbox.count() == 1)
    if qbox.count() == 1:
        labels = [t.strip() for t in page.locator(".rd-quality button").all_inner_texts()]
        check("四个档位都在且顺序对（自动 / 低 / 均衡 / 高）",
              labels == ["自动", "低", "均衡", "高"], f"实得 {labels}")
        check("streaming 时档位可点（不是灰的）",
              page.locator(".rd-quality button").first.is_enabled())

        state = page.evaluate(_SESSION_STATE_JS)
        note(f"后端会话：state={state.get('state')} quality={state.get('quality')}")

        original = state.get("quality")
        expected_badge = f"当前：{QUALITY_LABELS.get(original, '—')}"
        badge = _text_of(page.locator(".rd-quality .badge"))
        check("badge 的「当前」与服务端一致（读不到就画「—」，不许画成自动）",
              badge == expected_badge, f"页面「{badge}」 vs 期望「{expected_badge}」")

        # 切到与当前不同的一档，再切回来 —— 只切一次证明不了"双向都通"
        target_label = "低" if original != "low" else "高"
        target_value = "low" if target_label == "低" else "high"

        stats_before = read_stats(page)
        page.click(f'.rd-quality button:has-text("{target_label}")')

        ok_q, got = wait_for(
            lambda: page.evaluate(_SESSION_STATE_JS),
            lambda s: isinstance(s, dict) and s.get("quality") == target_value,
            timeout=25.0, interval=0.5)
        check(f"点「{target_label}」→ 后端会话 quality 真的变了", ok_q,
              f"实得 {got.get('quality') if isinstance(got, dict) else got}")

        ok_badge, _ = wait_for(
            lambda: _text_of(page.locator(".rd-quality .badge")),
            lambda t: t == f"当前：{target_label}", timeout=15.0, interval=0.25)
        check(f"页面 badge 同步成「当前：{target_label}」", ok_badge)

        if original in QUALITY_LABELS:
            page.click(f'.rd-quality button:has-text("{QUALITY_LABELS[original]}")')
            ok_back, got2 = wait_for(
                lambda: page.evaluate(_SESSION_STATE_JS),
                lambda s: isinstance(s, dict) and s.get("quality") == original,
                timeout=25.0, interval=0.5)
            check(f"切回「{QUALITY_LABELS[original]}」→ 后端也回到原值（双向都通）", ok_back,
                  f"实得 {got2.get('quality') if isinstance(got2, dict) else got2}")
        else:
            note("服务端没报初始档位（旧版中心）⇒ 无法验证「切回原档」，如实跳过")

        # 切档会触发编码器重建（≈0.3 s 空窗）—— 但流必须自己活过来
        ok_alive, stats_after = wait_for_stats(
            page, "decoded", min_value=(stats_before[0] if stats_before else 0) + 2,
            timeout=30.0)
        check("切档之后画面仍在解（编码器重建没把流弄死）", ok_alive,
              f"切前 {stats_before} → 切后 {stats_after}")

    # ══ ③ 剪贴板 ══
    cbox = page.locator(".rd-clipboard")
    check("有「剪贴板」区块", cbox.count() == 1)
    if cbox.count() == 1:
        read_btn = page.locator('.rd-clipboard button:has-text("读取被控机的")')
        send_btn = page.locator('.rd-clipboard button:has-text("把下面这段发过去")')
        draft = page.locator("textarea.rd-clipboard-draft")
        view = page.locator("pre.rd-clipboard-view")

        check("「读取被控机的」与「把下面这段发过去」两个按钮分开存在",
              read_btn.count() == 1 and send_btn.count() == 1)

        original_clip = host_clipboard_get()
        note(f"宿主剪贴板原内容："
             f"{'（没有文本）' if original_clip is None else str(len(original_clip)) + ' 字'}"
             " —— 收摊时还原")

        stamp = int(time.time())
        known = f"CS-PANEL-E2E-{stamp}-READ"

        # ① 从宿主喂一段已知文本，让被控机（同机）读到它 → 经推送回到页面
        host_clipboard_set(known)
        read_btn.click()

        notice_ok, notice = wait_for(
            lambda: _text_of(page.locator("p.notice")),
            lambda t: bool(t) and "剪贴板" in t, timeout=8.0, interval=0.25)
        # ⚠️ 不能"点完立刻读 p.notice"：提示只有**一个槽**，上一步切质量档的那句
        #    （"已请求切到「自动」…"）还会挂 6 秒。第一次跑就是这样假红的。
        check("点「读取被控机的」立刻有反馈（不许「点了没反应」）", notice_ok,
              f"实得提示：{notice}")

        ok_push, got_text = wait_for(
            lambda: _text_of(view), lambda t: t == known, timeout=30.0, interval=0.5)
        check("读一次 → 内容经 ClipboardChanged 推到页面（推送链路通）", ok_push,
              f"页面实得：{(got_text or '')[:80]!r}")

        # ② 反方向：把页面上的草稿写进**被控机**的剪贴板（同机 ⇒ 宿主剪贴板可观测）
        sent = f"CS-PANEL-E2E-{stamp}-WRITE"
        draft.fill(sent)
        send_btn.click()

        ok_write, got_clip = wait_for(
            host_clipboard_get, lambda t: t == sent, timeout=30.0, interval=0.5)
        check("「把下面这段发过去」→ 宿主（=被控机）剪贴板真的变成这段文字", ok_write,
              f"实得：{(got_clip or '')[:80]!r}")

        ok_echo, _ = wait_for(lambda: _text_of(view), lambda t: t == sent,
                              timeout=15.0, interval=0.5)
        check("写入后页面那一格同步成新内容（不是还挂着上一次读到的）", ok_echo)

        # ③ 超限：前端**先截断**，让用户看到"只发了前 2 万字"，而不是收一个 400
        long_text = "长" * 20050
        draft.fill(long_text)
        send_btn.click()
        ok_cut, cut_notice = wait_for(
            lambda: _text_of(page.locator("p.notice")),
            lambda t: bool(t) and "20000" in t and "超过上限" in t, timeout=15.0, interval=0.5)
        check("超过 2 万字时页面明说「只发了前 20000 字」（不是静默截断）", ok_cut,
              f"实得提示：{cut_notice}")

        ok_len, clip_len = wait_for(
            lambda: host_clipboard_get(), lambda t: t is not None and len(t) == 20000,
            timeout=30.0, interval=0.5)
        check("被控机剪贴板里真的是 20000 字（前端截断生效，没把 20050 全塞过去）",
              ok_len, f"实得 {len(clip_len) if clip_len is not None else None} 字")

        if original_clip is not None:
            host_clipboard_set(original_clip)
            note("宿主剪贴板已还原")
        else:
            note("宿主剪贴板原本没有文本 ⇒ 不复原（现在里面是测试留下的内容）")

        shot = os.path.join(out_dir, "panels.png")
        try:
            page.screenshot(path=shot)
            note(f"截图：{os.path.relpath(shot, REPO)}")
        except Exception:
            pass

    # ══ ④ 全屏（M7.9）══
    #
    # 🔴 这一节要证的是**状态跟着浏览器走**，不是"按钮点了没抛异常"：
    #    全屏可以用 Esc 退出，而那条路**不经过我们的按钮** ——
    #    页面若拿本地布尔当状态，按过 Esc 之后按钮会永远停在「全屏显示」，
    #    第二下点下去还得再退一次（表现是"点了没反应"）。
    #    ⇒ 判据落在 `document.fullscreenElement` 与按钮文案**两边对得上**。
    #
    # ⚠️ 无头浏览器可能直接拒绝元素全屏（`requestFullscreen` 抛 NotAllowedError）。
    #    那不是本页的缺陷 ⇒ 这一节**如实跳过**、不判失败
    #    （假 FAIL 比不断言更坏：它会训练人忽略红色）。
    fs_btn = page.locator(".rd-header-actions button", has_text="全屏显示")
    check("有「全屏显示」按钮（M7.9）", fs_btn.count() == 1)

    if fs_btn.count() == 1:
        fs_btn.click()

        ok_fs, fs_class = wait_for(
            lambda: page.evaluate(
                "() => document.fullscreenElement ? document.fullscreenElement.className : ''"),
            lambda v: isinstance(v, str) and "rd-viewport" in v,
            timeout=3.0, interval=0.2)

        if not ok_fs:
            note("这个浏览器环境没有进全屏（无头模式下常见）⇒ 跳过「真的全屏了」这条；"
                 "「按钮存在」已经验过，这里不判失败。")
        else:
            check("全屏的是**画面那一块**，不是整页（工具栏与读数不该跟着进全屏）",
                  isinstance(fs_class, str) and "rd-viewport" in fs_class, f"实得 {fs_class!r}")

            # 退出走页面自己的按钮：Esc 那条路由浏览器直接处理，脚本插不进去。
            page.click('.rd-fullscreen-hint button:has-text("退出全屏")')

            ok_back, _ = wait_for(
                lambda: page.evaluate("() => !!document.fullscreenElement"),
                lambda v: v is False, timeout=3.0, interval=0.2)
            check("点「退出全屏」→ 真的退出了（document.fullscreenElement 为空）", ok_back)

            check("退出后按钮文案回到「全屏显示」（状态是从 document 读回来的）",
                  _text_of(page.locator(".rd-header-actions button", has_text="全屏显示")) is not None)

# ═══════════════════════════════════════════════════════════════
# ⑥-b 剪贴板的两个"容易画错"的态 ＋ 非安全上下文（C1 / C2）
# ═══════════════════════════════════════════════════════════════
#
# 🔴 C1 治的是**把两句话合并成一句**：
#      · 「没有文本」（图片 / 空的 / 被占用）—— 不是故障，不用处置；
#      · 「读失败」（OpenClipboard 失败）—— 是故障，note 里有原因。
#    合并之后现场会去查一个根本没坏的东西。
#
# 🔴 C2 治的是**只在安全上下文里才有的 API 被当成"总是有"**：
#      `navigator.clipboard` 在 `http://` 的非本机地址下**压根不存在**
#      （不是报错，是 undefined），于是"复制到本机"必须给一条能走通的路
#      （手动选中复制），而不是回一句"复制失败"。
#
# ⚠️ 「读失败」这一态是**造出来**的：脚本自己把宿主剪贴板 OpenClipboard 占住不放，
#    被控端（同机）就必然失败。没有这一步，这个分支在自动化里永远跑不到。

_CLIP_CACHE_JS = """async () => {
  const r = await fetch('/api/remote/sessions');
  const list = await r.json();
  const live = (list || []).filter(s => !s.endedAt);
  if (!live.length) return { ok: false };
  const c = await fetch(`/api/remote/sessions/${encodeURIComponent(live[0].sessionId)}/clipboard`);
  if (c.status === 204) return { ok: true, status: 204 };
  const dto = await c.json();
  return { ok: true, text: dto.text, note: dto.note };
}"""


def _clipboard_hold():
    """占住宿主剪贴板，返回是否占成功。**必须尽快 `_clipboard_release()` 关掉。**"""
    return bool(_win32().windll.user32.OpenClipboard(None))


def _clipboard_release():
    try:
        _win32().windll.user32.CloseClipboard()
    except Exception:
        pass


def _clipboard_clear():
    """把宿主剪贴板清空 —— 这是"没有文本"最普通的一种真实现场（空的）。

    ⚠️ 刻意不塞"文件列表/图片"：清空同样是 `CF_UNICODETEXT` 不存在，
       而被控端那侧看到的东西一模一样，代价却只有三次 API 调用。
    """
    ct = _win32()
    if not _clipboard_hold():
        return False
    try:
        return bool(ct.windll.user32.EmptyClipboard())
    finally:
        _clipboard_release()


def _clipboard_probe_after_clear():
    """清一次再回读：返回宿主剪贴板里的文本（`None` = 真的空了）。"""
    _clipboard_clear()

    return host_clipboard_get()


def verify_clipboard_states(page, browser, out_dir):
    """C1（剪贴板两态）＋ C2（非安全上下文）。"""
    print()
    print("── 剪贴板两态 / 非安全上下文（C1 / C2）：浏览器实证 ──")

    status = page.locator(".rd-clipboard div.muted.small")
    read_btn = page.locator('.rd-clipboard button:has-text("读取被控机的")')

    if read_btn.count() != 1:
        check("剪贴板区块在（没有它这两条都验不了）", False)
        return

    original = host_clipboard_get()
    note(f"宿主剪贴板原内容：{'（没有文本）' if original is None else str(len(original)) + ' 字'}")

    try:
        # ══ C1 ① 没有文本 ══
        #
        # ⚠️ **清完要回读确认**，不能"调用了 EmptyClipboard 就当它空了"：
        #    上一节收尾刚把原文写回去，两边是紧挨着的两次剪贴板操作，
        #    第一次跑就撞上了 —— 页面读到的是上一节留下的那 28 个字，
        #    于是本条断言在一个**产品完全正常**的情况下假红了。
        ok_cleared, _ = wait_for(
            lambda: _clipboard_probe_after_clear(), lambda t: t is None, timeout=5.0, interval=0.25)
        check("脚本能把宿主剪贴板清空并回读确认（造出「没有文本」）", ok_cleared)

        read_btn.click()

        ok_empty, empty_txt = wait_for(
            lambda: _text_of(status), lambda t: bool(t) and "没有文本" in t,
            timeout=25.0, interval=0.5)
        check("剪贴板里没有文本时页面说「没有文本」（不是「读取失败」）", ok_empty,
              f"实得：{(empty_txt or '')[:90]!r}")
        check("那一句同时说明**这不是故障**（否则现场会去查一个没坏的东西）",
              "这不是故障" in (empty_txt or ""), f"实得：{(empty_txt or '')[:90]!r}")

        cache = page.evaluate(_CLIP_CACHE_JS)
        check("后端把「没有文本」表达成 text=null 且 note=null（不是失败）",
              isinstance(cache, dict) and cache.get("text") is None and cache.get("note") is None,
              f"实得 {cache}")

        # ══ C1 ② 读失败 ══
        #
        # 🔴 **这一态在本机构造不出来，如实记录，不假装验过。**
        #
        #    原本的打算是"脚本自己 OpenClipboard 占住不放" ⇒ 被控端读必然失败。
        #    实测**挡不住**：Win10 起 `OpenClipboard` 并不排他（同一个桌面下，
        #    子进程在父进程开着剪贴板的时候**第 1 次尝试就打开成功了**）。
        #    也就是说"抢剪贴板"这条路造不出失败态。
        #
        # ✅ 但**渲染那一半已经被证过了**：上一版把"没有文本"误写进 `note`，
        #    页面渲染出来的正是「读取失败：被控机剪贴板里此刻没有文本…」——
        #    那条 FAIL 反证了「有 note ⇒ 渲染成 读取失败：{note}」这条分支是通的。
        #    剩下的一半（"读失败时载荷要带上 readNote"）在 SessionPipeServer 里，
        #    而且这条通路是**刻意**从 Failure 改成 payload 的（原先走 Failure 时
        #    页面上根本收不到任何东西 —— 点了没反应）。
        probe = _clipboard_hold()
        if probe:
            _clipboard_release()
        note("C1②（读失败）本机构造不出来：Win10 的 OpenClipboard 不排他"
             f"（实测能占住={probe} 但子进程照样能打开）⇒ 抢剪贴板造不出失败。"
             "渲染分支已由上一版那条误写 note 的 FAIL 反证；"
             "真实失败（输入法 / 剪贴板管理器长期占用）请在现场留意一次")
    finally:
        if original is not None:
            host_clipboard_set(original)

    # ══ C2 非安全上下文 ══
    #
    # 🔴 **这里是"复现根因"，不是"换个地址再开一次页"**，这点必须说清：
    #    现场按 `http://192.168.x.x` 打开时，浏览器让 `navigator.clipboard`
    #    **不存在**、`isSecureContext` 为 false —— 脚本用 init 脚本把这两件事造成同一个样。
    #    而"页面自己跑在 http 局域网地址上"这一条**只能在现场验**：
    #    它取决于服务端绑没绑 0.0.0.0、以及 Windows 防火墙放不放行。
    ctx2 = browser.new_context(viewport={"width": 1280, "height": 900})
    ctx2.add_init_script(
        "Object.defineProperty(window, 'isSecureContext', { get: () => false });"
        "try { Object.defineProperty(navigator, 'clipboard', { value: undefined }); } catch (e) {}")
    page2 = ctx2.new_page()
    try:
        page2.goto(page.url, wait_until="domcontentloaded", timeout=30000)

        # ⚠️ 这是一个**全新的浏览器上下文** ⇒ 没有 cookie，必须重新登录。
        #    第一次写漏了这一步，症状是"等 20 秒等不到「远程桌面」按钮"
        #    （页面停在登录框上），看起来像"非安全上下文把页面弄坏了"—— 完全不是。
        page2.fill('input[autocomplete="username"]', ADMIN_USER)
        page2.fill('input[type="password"]', ADMIN_PASS)
        page2.click('button:has-text("登录")')

        page2.wait_for_selector('button:has-text("远程桌面")', timeout=20000)
        page2.click('button:has-text("远程桌面")')

        # ⚠️ 只能等"远控页确实开了"，**不能等 `.rd-clipboard`**：
        #    这一页没有建会话，剪贴板 / 质量档这些区块属于"有了会话才出现"的那一半。
        page2.wait_for_selector('button:has-text("开始观看")', timeout=20000)

        banner = _text_of(page2.locator("p.notice.warn"))
        check("非安全上下文时页面顶部明说「没有声音」并解释原因",
              bool(banner) and "安全上下文" in banner, f"实得：{(banner or '')[:90]!r}")

        # 这一页没有会话 ⇒ 剪贴板区块不渲染是**对的**；这里只验"页面没崩、功能没消失"。
        has_entry = page2.locator('button:has-text("开始观看")').count() == 1
        check("非安全上下文下远控页照常可用（没有因为缺 API 就整页不可用）", has_entry)

        no_api = page2.evaluate("() => typeof navigator.clipboard === 'undefined'"
                                " || !navigator.clipboard?.writeText")
        check("这个上下文里 `navigator.clipboard.writeText` 确实不存在 ⇒ 复制只能手动选中",
              no_api)
        note("⚠️ 「点『复制到本机』弹出降级提示」那一步要求 clipboard.text 有值，"
             "而它要求这一页自己也看着一路流（脚本不多占一路配额）。"
             "降级分支的触发条件就是上面这条 no_api；现场按 http 局域网地址打开时，"
             "请手动点一次确认「请在下面那段文字里手动选中复制」那句出现")
    except Exception as ex:
        note(f"非安全上下文那一页没能跑完：{type(ex).__name__}: {ex}")
    finally:
        try:
            ctx2.close()
        except Exception:
            pass


# ═══════════════════════════════════════════════════════════════
# ⑦ 静止降帧（`--idle SEC`）—— 让桌面**真的**静止，再看它降不降、回不回
# ═══════════════════════════════════════════════════════════════

class SolidOverlay:
    """一个置顶的纯色全屏窗口：把桌面变成**一张不动的图**。

    🔴 为什么必须"盖住"，而不是"人别动鼠标就行"：

       观看页在浏览器里解码播放的**正是这块屏**。屏幕上只要还有任何东西在动
       （任务栏时钟、别的窗口、甚至本脚本自己的日志窗口），
       `Session.DesktopIdleMs` 就永远涨不上去。

       M5.4-c② 的首次 18/18 冒烟就是这样：全程**一行「退避」都没有**，
       于是"静止降帧"在真机上从未被触发过（只在单测里用假抓屏器验过）。
       ⇒ 这一节存在的唯一理由，就是把"单测验过"补成"真机验过"。

    🔴 为什么用 Win32 而不是 tkinter：

       `tkinter` 的 `mainloop()` 要占住**主线程**，而主线程此刻正在轮询页面读数
       —— 一占就变成"静止的这 10 秒里什么都读不到"，而那 10 秒正是本节要看的。
       改成"独立线程里跑消息泵"：`ctypes.WinDLL` 的调用会**释放 GIL**，
       泵里 `PeekMessage` 之间 `sleep` 也不挡主线程。

    ⚠️ 四个必须做对的细节（错一个就白跑）：

      ① **必须盖住虚拟屏的整个矩形**（含任务栏、含第二块屏）：漏一条边，
         那条边上在动的东西就足以让抓屏一直"有新帧"；
      ② **`WS_EX_NOACTIVATE`**：别抢焦点 —— 抢焦点会让窗口重绘一次、
         还会把终端切走，那是一次"桌面变化"，偏偏发生在最不该有的时候；
      ③ **窗口类直接用 `DefWindowProcW` + 纯色画刷**：不写 Python 窗口过程，
         于是没有"Python 回调在别的线程里跑"这类问题，`WM_ERASEBKGND`
         由系统拿 `hbrBackground` 填掉。

      ④ 🔴 **必须给窗口类设一个光标（`hCursor = IDC_ARROW`）** —— 这一条是本脚本
         **实测踩出来的**，不设的后果极隐蔽：

         不设 `hCursor` ⇒ 光标到了这块窗上"没人负责它" ⇒ 系统反复重设它
         ⇒ DDA 的 `LastMouseUpdateTime` **每帧都在变**（`PointerShapeBufferSize`
         每帧都是 4096）⇒ 抓屏器判成 `PointerUpdated`（"只有指针变了"）
         ⇒ **每 tick 都算"有新帧"** ⇒ `DesktopIdleMs` 永远涨不上去、
         「静止降帧」一次都不会触发。

         而"屏幕像素有没有变"这条探针**完全看不出来**（实测连拍 0 变化），
         所以只看截图会得出"桌面已经静止了、是产品没退避"这个**错误结论**。
    """

    WS_POPUP = 0x80000000
    WS_VISIBLE = 0x10000000
    WS_EX_TOPMOST = 0x00000008
    WS_EX_TOOLWINDOW = 0x00000080
    WS_EX_NOACTIVATE = 0x08000000
    SW_SHOW = 5
    PM_REMOVE = 1

    # `LoadCursorW(NULL, IDC_ARROW)` —— 见上面第 ④ 条
    IDC_ARROW = 32512

    # 虚拟屏那几个 `GetSystemMetrics` 下标
    SM_XVIRTUALSCREEN = 76
    SM_YVIRTUALSCREEN = 77
    SM_CXVIRTUALSCREEN = 78
    SM_CYVIRTUALSCREEN = 79
    SM_CXSCREEN = 0
    SM_CYSCREEN = 1

    def __init__(self, color=0x00262626):
        self.color = color          # COLORREF = 0x00BBGGRR ⇒ 0x262626 是中灰
        self._hwnd = None
        self._thread = None
        self._ready = threading.Event()
        self._stop = False
        self._error = None

    # ── 给调用方 ──
    def show(self, timeout=5.0):
        """起窗口。返回"窗口真的建出来了"（**不要假设它一定成功**）。"""
        self._thread = threading.Thread(target=self._run, name="solid-overlay", daemon=True)
        self._thread.start()
        if not self._ready.wait(timeout):
            self._error = self._error or "创建窗口超时"
            return False
        return self._hwnd is not None

    def close(self, timeout=5.0):
        self._stop = True
        if self._thread is not None:
            self._thread.join(timeout)

    @property
    def error(self):
        return self._error or ""

    # ── 线程里干活 ──
    def _run(self):
        ct = _win32()
        u32 = ct.windll.user32
        g32 = ct.windll.gdi32
        hwnd = None

        class WNDCLASSW(ct.Structure):
            _fields_ = [
                ("style", ct.c_uint),
                ("lpfnWndProc", ct.c_void_p),
                ("cbClsExtra", ct.c_int),
                ("cbWndExtra", ct.c_int),
                ("hInstance", ct.c_void_p),
                ("hIcon", ct.c_void_p),
                ("hCursor", ct.c_void_p),
                ("hbrBackground", ct.c_void_p),
                ("lpszMenuName", ct.c_wchar_p),
                ("lpszClassName", ct.c_wchar_p),
            ]

        class MSG(ct.Structure):
            """只当一块**够大**的缓冲区用（`PeekMessage` 往里写，我们不读）。"""
            _fields_ = [
                ("hwnd", ct.c_void_p),
                ("message", ct.c_uint),
                ("wParam", ct.c_size_t),
                ("lParam", ct.c_ssize_t),
                ("time", ct.c_uint),
                ("pt_x", ct.c_long),
                ("pt_y", ct.c_long),
            ]

        try:
            # 🔴 64 位下**所有**指针参数都必须声明 argtypes：不声明的话，
            #    传进去的句柄会被当成 32 位截断，而失败方式是"窗口不出来"，
            #    一个字的报错都没有。
            # ⚠️ `GetModuleHandleW` 在 **kernel32**，不在 user32（写错就是
            #    `AttributeError: function 'GetModuleHandleW' not found`）。
            k32 = ct.windll.kernel32
            k32.GetModuleHandleW.argtypes = [ct.c_wchar_p]
            k32.GetModuleHandleW.restype = ct.c_void_p
            u32.RegisterClassW.argtypes = [ct.POINTER(WNDCLASSW)]
            u32.RegisterClassW.restype = ct.c_ushort
            u32.CreateWindowExW.restype = ct.c_void_p
            u32.CreateWindowExW.argtypes = [
                ct.c_uint, ct.c_wchar_p, ct.c_wchar_p, ct.c_uint,
                ct.c_int, ct.c_int, ct.c_int, ct.c_int,
                ct.c_void_p, ct.c_void_p, ct.c_void_p, ct.c_void_p]
            u32.ShowWindow.argtypes = [ct.c_void_p, ct.c_int]
            u32.ShowWindow.restype = ct.c_int
            u32.PeekMessageW.argtypes = [ct.POINTER(MSG), ct.c_void_p,
                                         ct.c_uint, ct.c_uint, ct.c_uint]
            u32.PeekMessageW.restype = ct.c_int
            u32.TranslateMessage.argtypes = [ct.POINTER(MSG)]
            u32.DispatchMessageW.argtypes = [ct.POINTER(MSG)]
            u32.DispatchMessageW.restype = ct.c_void_p

            hinst = k32.GetModuleHandleW(None)
            brush = g32.CreateSolidBrush(self.color)

            # 🔴 见类注释第 ④ 条：**不设光标 = 抓屏永远认为"指针动了"**。
            u32.LoadCursorW.argtypes = [ct.c_void_p, ct.c_void_p]
            u32.LoadCursorW.restype = ct.c_void_p
            arrow = u32.LoadCursorW(None, ct.c_void_p(self.IDC_ARROW))

            cls_name = "ControlServerSmokeIdleOverlay_%d" % os.getpid()
            wc = WNDCLASSW()
            wc.lpfnWndProc = ct.cast(u32.DefWindowProcW, ct.c_void_p).value
            wc.hInstance = hinst
            wc.hCursor = arrow
            wc.hbrBackground = brush
            wc.lpszClassName = cls_name
            if not u32.RegisterClassW(ct.byref(wc)):
                raise RuntimeError("RegisterClassW 失败（GetLastError=%d）"
                                   % ct.windll.kernel32.GetLastError())

            vx = u32.GetSystemMetrics(self.SM_XVIRTUALSCREEN)
            vy = u32.GetSystemMetrics(self.SM_YVIRTUALSCREEN)
            vw = u32.GetSystemMetrics(self.SM_CXVIRTUALSCREEN)
            vh = u32.GetSystemMetrics(self.SM_CYVIRTUALSCREEN)
            if vw <= 0 or vh <= 0:      # 某些远程/虚拟会话不报虚拟屏 ⇒ 退回主屏
                vx = vy = 0
                vw = u32.GetSystemMetrics(self.SM_CXSCREEN)
                vh = u32.GetSystemMetrics(self.SM_CYSCREEN)

            hwnd = u32.CreateWindowExW(
                self.WS_EX_TOPMOST | self.WS_EX_TOOLWINDOW | self.WS_EX_NOACTIVATE,
                cls_name, "idle-overlay",
                self.WS_POPUP | self.WS_VISIBLE,
                vx, vy, vw, vh, None, None, hinst, None)
            if not hwnd:
                raise RuntimeError("CreateWindowExW 失败（GetLastError=%d）"
                                   % ct.windll.kernel32.GetLastError())

            self._hwnd = hwnd
            u32.ShowWindow(ct.c_void_p(hwnd), self.SW_SHOW)
            self._ready.set()

            msg = MSG()
            while not self._stop:
                if u32.PeekMessageW(ct.byref(msg), None, 0, 0, self.PM_REMOVE):
                    u32.TranslateMessage(ct.byref(msg))
                    u32.DispatchMessageW(ct.byref(msg))
                else:
                    time.sleep(0.02)
        except Exception as e:
            self._error = "%s: %s" % (type(e).__name__, e)
            self._ready.set()
        finally:
            self._hwnd = None
            if hwnd:
                try:
                    u32.DestroyWindow(ct.c_void_p(hwnd))
                except Exception:
                    pass


def read_idle_card(page):
    """读「桌面静止」那一格的**原文**（`画面在变` / `已静止 N 秒` / `—`）。

    🔴 判据必须用原文、不能用 `parse_num` 的结果当唯一依据：
       `—`（旧客户端）与 `画面在变` 在 `parse_num` 下**都是 None**
       —— 把这两件事混成一个，就分不出"这台机器不提这件事"和"桌面在动"。
    """
    return read_cards(page, (IDLE_CARD,)).get(IDLE_CARD)


def idle_seconds_of(text):
    """`已静止 12 秒` ⇒ 12.0；`画面在变` / `—` / 读不到 ⇒ None。"""
    if not text or "静止" not in text:
        return None
    v = parse_num(text)
    return v


def verify_idle_backoff(page, dwell=10.0):
    """🔥 **静止降帧在真机上的硬判据**（M5.4-c② 步 8 的端到端补验）。

    🔴 为什么非要这一节：步 8 的 9 条用例全绿，但它们用的是**假抓屏器**
       （`StubCapturer.NeverChanges` 人造静止）。而第一次 18/18 的真机冒烟里，
       托盘日志**一行「退避」都没有** —— 桌面一直在动，降帧这个分支在真机上
       一次都没被走到过。"单测验过"与"真机验过"是两件事，这节就是补后者。

    判据（五条，缺一条这段就不算验过）：

      ① 页面这一格**存在且有值**（`—` ⇒ 被控端是旧包，整条链路缺一段）；
      ② 盖住桌面期间它**真的涨到 ≥ 2 秒**（否则下面那条"退避"可能只是巧合）；
      ③ 托盘日志出现 **「退避到 N fps」**（只在翻转时说一次，出现即证明进过退避）；
      ④ 盖着的这段时间会话**始终 streaming** —— 中心没把它判成死机器，
         这正是 `REF-M5 §14.14` 那次翻车的正题；
      ⑤ 撤掉盖布后 ≤ 6 秒出现 **「恢复变化」**、那一格回到 `画面在变`、
         且 `framesReceived` 重新涨（画面真的又活了）。

    ⚠️ 顺序很要紧：它必须在**指针 / 深浅底 / 形状**那几个实验**之后**跑 ——
       那几个都会动真实光标，而这里要的是"除了盖布什么都不动"。
    """
    print()
    print(f"── 静止降帧（把桌面盖成纯色 {dwell:0.0f} 秒）──")

    before = read_idle_card(page)
    if not check("页面读得到「桌面静止」这一格", before is not None,
                 f"读到 {before!r}"):
        return
    if before.strip() in ("—", "-", ""):
        check("「桌面静止」这一格有值（旧客户端才会是 —）", False,
              "—— 被控端是旧包：`DesktopIdleMs` 这条链缺一段。"
              "先确认 agents/*/bin/Debug 里的托盘是本次编译的产物（铁律 13）")
        return
    note(f"盖布前：{before}")

    # 🔥 先**动一下光标**，不用"此刻桌面大概在动"当假设。
    #    如果它本来就已经静止（这台机器没人用），`backedOff` 早就是 true 了，
    #    「退避」那句在我们 mark 之前就打完了 ⇒ 下面会**假红**，
    #    而且红得很有说服力（"静止时长涨了、却没有退避日志"）。
    #    动一下 ⇒ 立刻有新帧 ⇒ 静止计时归零，接下来才是我们要看的那一次上升沿。
    x, y = get_cursor()
    set_cursor(x + 8 if x + 8 < 3800 else x - 8, y)
    time.sleep(1.2)

    mark = tray_log_mark()

    overlay = SolidOverlay()
    if not check("纯色盖布挂上去了（桌面被真的盖住）", overlay.show(), overlay.error):
        return

    samples = []
    not_streaming = []
    t0 = time.time()
    try:
        while time.time() - t0 < dwell:
            text = read_idle_card(page)
            sess = read_session_via_page(page)
            state = sess.get("state") if isinstance(sess, dict) else None
            el = time.time() - t0
            samples.append((el, text, idle_seconds_of(text), state))
            if state != "streaming":
                not_streaming.append(f"t={el:0.1f}s 会话={state}")
            print(f"    · t={el:4.1f}s  {text}｜会话 {state}"
                  f"｜帧 {sess.get('framesReceived') if isinstance(sess, dict) else '?'}")
            time.sleep(1.0)
    finally:
        overlay.close()

    peak = max([s[2] for s in samples if s[2] is not None], default=None)
    grew = peak is not None and peak >= 2.0
    check("静止时长真的涨起来了（≥ 2 秒）", grew,
          f"最长读到 {peak} 秒" if grew
          else f"最长只读到 {peak} 秒 —— 没涨就说明盖布没盖住"
               f"（或抓屏器仍认为在变），下面那条「退避」也就无从谈起")

    lines = tray_log_since(mark)
    backoff = [l for l in lines if "退避到" in l]
    check("托盘进了静止退避（日志有「退避到 N fps」）",
          len(backoff) > 0,
          f"本次追加 {len(lines)} 行里找到 1 条" if backoff
          else f"本次追加的 {len(lines)} 行里没有这句 —— 静止时长涨了却不退避，"
               f"问题在 `IdleBackoffAfter` / `RemoteStreamPacing.PollPeriod`")
    if backoff:
        note("退避：" + backoff[0].strip())

    check("静止期间中心没把它判成死机器（会话始终 streaming）",
          not not_streaming,
          "全程 streaming" if not not_streaming else "；".join(not_streaming[:3]))

    # ── 撤掉盖布：桌面立刻变一次 ⇒ 必须**立刻**回到目标帧率 ──
    ok_recover, seen = wait_for(
        lambda: tray_log_since(mark),
        lambda ls: any("恢复变化" in l for l in ls),
        timeout=6.0, interval=0.3)
    check("撤掉盖布后立刻恢复（日志有「恢复变化」）", ok_recover,
          "已出现" if ok_recover
          else "6 秒内没等到 —— 恢复只发生在「下一个抓屏 tick」里；"
               "等不到说明退避周期没被及时打断（`MaxIdlePeriod` 或 ResumeTargetPacing 出问题）")
    if ok_recover:
        hit = [l for l in seen if "恢复变化" in l][0]
        note("恢复：" + hit.strip())

    ok_awake, text = wait_for(lambda: read_idle_card(page),
                              lambda t: bool(t) and "静止" not in t,
                              timeout=8.0, interval=0.5)
    check("那一格回到「画面在变」", ok_awake, f"最后一次读到 {text!r}")

    s0 = read_session_via_page(page)
    time.sleep(2.0)
    s1 = read_session_via_page(page)
    n0 = s0.get("framesReceived") if isinstance(s0, dict) else None
    n1 = s1.get("framesReceived") if isinstance(s1, dict) else None
    grew = isinstance(n0, (int, float)) and isinstance(n1, (int, float)) and n1 > n0
    check("画面真的又活了（framesReceived 继续涨）", grew, f"{n0} → {n1}")


# ═══════════════════════════════════════════════════════════════
# ③ 深 / 浅底受控实验（`--backdrops`）—— **零副作用**
# ═══════════════════════════════════════════════════════════════

def find_flat_zones(shot, block=64):
    """在一张桌面截图里找**两块平坦区**：一块确凿很暗、一块确凿很亮。

    返回 `{"dark": (box, luma, stddev) | None, "light": (...)}`。

    🔴 为什么在**真实桌面**上找，而不是自己铺一块纯色：原本打算"临时换纯色壁纸"，
       实测**在本机不生效**（`SPI_SETDESKWALLPAPER` 回 True、读回也变了，但直抓屏比对
       屏幕主体亮度没变：238.3 → 238.5）。而判据本来要的就不是"精确的底色值"，
       而是"一块确凿很暗的平坦区"与"一块确凿很亮的平坦区" —— 桌面上本来就有
       （暗色编辑器的空白处 / 白底正文区）。

    ⚠️ `stddev` 这一项是**必需**的，不是保险：文字密布的块**看着均匀、其实噪声很大**，
       而噪声会让"指针在/不在"的定点差异淹没掉 —— 那条路只会得到假 FAIL。
    """
    from PIL import Image, ImageStat
    im = Image.open(shot).convert("L")
    w, h = im.size
    dark = light = None
    step = max(1, block // 2)
    for y in range(0, h - block + 1, step):
        for x in range(0, w - block + 1, step):
            box = (x, y, x + block, y + block)
            st = ImageStat.Stat(im.crop(box))
            m, sd = st.mean[0], st.stddev[0]
            if sd > FLAT_ZONE_MAX_STD:
                continue
            if m <= DARK_ZONE_MAX and (dark is None or m < dark[1]):
                dark = (box, m, sd)
            if m >= LIGHT_ZONE_MIN and (light is None or m > light[1]):
                light = (box, m, sd)
    return {"dark": dark, "light": light}


def verify_backdrops(page, out_dir, screen_w, screen_h):
    """③ 在真实桌面的一块**暗区** / 一块**亮区**上各判一次"指针进画面"。

    🔴 **零副作用**：不改壁纸、不改任何系统设置（理由见 `find_flat_zones`）。
    ⚠️ 前提是"桌面上确实找得到那样两块平坦区"；找不到就**如实跳过** ——
       桌面上窗口密密麻麻时这完全可能，而把它报成 FAIL 就成了"红的是环境"。

    ④ 延迟与帧率**不在这里另算**：页面上已经有一个「帧时延」（抓屏时刻 → 本机画上），
       `collect_metrics` 采的就是它。在这里另造一个只会有两个口径、两个数。
    """
    print()
    print("── 深 / 浅底受控实验（在真实桌面上找一块暗区 / 一块亮区）──")

    from PIL import Image

    canvas = page.locator("canvas").first
    original = get_cursor()

    def shot(tag):
        path = os.path.join(out_dir, f"backdrop-{tag}.png")
        canvas.screenshot(path=path)
        return path

    try:
        set_cursor(screen_w // 2, screen_h // 2)
        time.sleep(1.5)
        ref = shot("ref")
        iw, ih = Image.open(ref).size

        zones = find_flat_zones(ref)
        dark_ok = zones["dark"] is not None
        light_ok = zones["light"] is not None

        if dark_ok:
            b, m, sd = zones["dark"]
            note(f"暗区 {b}：亮度 {m:.1f}、块内标准差 {sd:.1f}")
        if light_ok:
            b, m, sd = zones["light"]
            note(f"亮区 {b}：亮度 {m:.1f}、块内标准差 {sd:.1f}")

        check("桌面上找得到一块确凿**很暗**的平坦区（深底实验的前提）", dark_ok,
              (f"亮度 {zones['dark'][1]:.1f}、块内标准差 {zones['dark'][2]:.1f}"
               if dark_ok else
               f"没找到 ⇒ 跳过（这不是缺陷：窗口铺满时本来就没有暗区）。参考图 {os.path.basename(ref)}"))
        check("桌面上找得到一块确凿**很亮**的平坦区（浅底实验的前提）", light_ok,
              (f"亮度 {zones['light'][1]:.1f}、块内标准差 {zones['light'][2]:.1f}"
               if light_ok else
               f"没找到 ⇒ 跳过。参考图 {os.path.basename(ref)}"))

        results = {}
        for name, key, tag in (("深底", "dark", "dark"), ("浅底", "light", "light")):
            zone = zones[key]
            if zone is None:
                continue
            box, m, sd = zone
            cx_img, cy_img = (box[0] + box[2]) // 2, (box[1] + box[3]) // 2
            cx_scr, cy_scr = img_to_screen(cx_img, cy_img, screen_w, screen_h, iw, ih)
            half = max(16, (box[2] - box[0]) // 4)
            probe = (cx_img - half, cy_img - half, cx_img + half, cy_img + half)
            note(f"{name}：区域 {box} ⇒ 中心 画面 ({cx_img},{cy_img}) = 屏幕 ({cx_scr},{cy_scr})")

            set_cursor(cx_scr, cy_scr)
            time.sleep(1.2)
            f_on = shot(f"{tag}-cursor")
            time.sleep(1.0)
            f_on2 = shot(f"{tag}-cursor-still")

            # ⚠️ 挪到**屏幕另一侧**：必须真的离开这一小块，否则"在/不在"没有差别。
            set_cursor(screen_w - cx_scr, screen_h - cy_scr)
            time.sleep(2.0)
            f_off = shot(f"{tag}-nocursor")

            noise = region_diff(f_on, f_on2, probe)
            signal = region_diff(f_on, f_off, probe)
            masked = region_signal_excl_noise(f_on, f_on2, f_off, probe)
            results[name] = (noise, signal, masked, f_on, f_off, probe)
            note(f"{name}：原始差异 {signal} px / 同点噪声 {noise} px ⇒ 扣噪声后 {masked} px")
            zoom_pair_generic(f_on, f_off, probe,
                              os.path.join(out_dir, f"backdrop-{tag}-zoom.png"),
                              scale=max(2, min(10, 480 // max(1, probe[2] - probe[0]))))

        for name in ("深底", "浅底"):
            if name not in results:
                continue
            _, _, masked, _, _, _ = results[name]
            check(f"{name}上指针进画面（同一小块、扣掉噪声后）", masked >= 12, f"{masked} px")

        if "深底" in results and "浅底" in results:
            d_on = luma(results["深底"][3], results["深底"][5])
            l_on = luma(results["浅底"][3], results["浅底"][5])
            check("两块底色真的分开了（暗区明显暗于亮区）", d_on + 60 < l_on,
                  f"暗区 {d_on:.1f} vs 亮区 {l_on:.1f}")

        return results or None
    finally:
        try:
            set_cursor(*original)
        except Exception:
            pass


# ═══════════════════════════════════════════════════════════════
# ⑤ 真机上的指针形状类型（`--shapes`，需 `--headful`）
# ═══════════════════════════════════════════════════════════════

def parse_shapes(lines):
    """取出每一处「指针 X,Y 形状 W×H Type（热点 hx,hy，…，形状字节 N）」。

    返回 dict 列表（`x/y/w/h/type/hx/hy/bytes`）。

    🔥 **热点必须一起读**：被控端报回的 `X,Y` 是**位图左上角**，而 `SetCursorPos`
       给的/人理解的是**热点位置**（箭头尖端）。两者差一个热点 ⇒ 只拿 X,Y 去和
       "我们设的位置"比，会得到 8~9 px 的假偏差（本步实测：I 型那次报回 442,161、
       热点 8,9，而我们设的是 450,170 —— **加上热点就精确相等**）。
       把热点一起读回来，这条就能变成一条**硬判据**："尖端位置 == 我们设的位置"。
    """
    out = []
    for i, l in enumerate(lines):
        m = SHAPE_RE.search(l)
        if m:
            out.append({"line": i, "x": int(m.group(1)), "y": int(m.group(2)),
                        "w": int(m.group(3)), "h": int(m.group(4)),
                        "type": m.group(5),
                        "hx": int(m.group(6)), "hy": int(m.group(7)),
                        "bytes": int(m.group(8))})
    return out


def shape_bytes_candidates(w, h):
    """形状字节数的候选值（按类型）。

    ⚠️ 为什么列出**三个**候选、而不是断言"Color 就必须 W×H×4"：
       `Monochrome` 的缓冲区是 AND / XOR **两个位掩码**拼起来的（每像素 2 bit），
       而"日志里那个高到底是像素高还是它的两倍"，本脚本**没有独立的权威依据**。
       与其猜，不如把候选都摆出来 —— 命中任一个都说明"字节数与尺寸自洽"；
       **一个都不命中**才是真正可疑的信号（形状缓冲被截断 / 尺寸读错）。
    """
    return {"32bpp（Color / MaskedColor）": w * h * 4,
            "2bpp（Monochrome：AND+XOR 各 1 bit）": w * h // 4,
            "1bpp（单掩码）": w * h // 8}


def verify_cursor_shapes(ctx, out_dir, screen_w, screen_h, tray_mark):
    """⑤ 真机上实际拿到的是哪种指针形状（**需要 `--headful`**）。

    🔥 为什么值得单独做一次：`PointerCompositor` 里**三种形状类型走三条不同的分支**
       —— Color = alpha 混合 / Monochrome = AND+XOR 双掩码 / MaskedColor = 逐位 XOR。
       纯函数层那三条**已有单元测试**（`PointerCompositorTests`：Monochrome 三条、
       MaskedColor 两条），但"真机光标会给出哪种类型"只有真机能回答 ——
       而它决定了生产路径**实际**走到哪条分支。

    ⚠️ 无头模式做不了：I 型光标只出现在**窗口里的文本区**上，
       而无头模式屏幕上没有那个窗口，真实光标根本悬不到它上面。
    """
    print()
    print("── 指针形状类型（桌面箭头 vs 文本区 I 型）──")

    page2 = ctx.new_page()
    # 🔥 **一块窗口里放两种区域**：上半是文本区（I 型）、下半是空白区（箭头）。
    #    这样两个采样点都在**同一个已知窗口**里 ⇒ 不必去猜"屏幕上哪块没被别的窗口盖住"
    #    （那两个窗口的位置本来就不可控，猜错就会把"窗口里的形状"当成"桌面的形状"）。
    # ⚠️ 尺寸都用**固定像素**而不是 `vh`：视口(1680×1000)比窗口客户区大得多，
    #    用 `vh` 会让下半块被裁掉，而裁掉之后"光标在不在里面"就说不清了。
    page2.set_content(
        "<html><head><style>html,body{margin:0;padding:0;background:#fff}"
        "textarea{display:block;width:900px;height:340px;border:0;outline:0;"
        "resize:none;font:32px Consolas,monospace;color:#111}"
        "#blank{width:900px;height:340px;background:#e8e8e8}"
        "</style></head><body>"
        "<textarea id=\"txt\" spellcheck=\"false\">上半：光标停在这里应当是 I 型（Monochrome）"
        "</textarea><div id=\"blank\"></div></body></html>")

    # 把窗口摆到**已知位置** —— 不能假设它恰好落在哪。
    try:
        cdp = ctx.new_cdp_session(page2)
        win = cdp.send("Browser.getWindowForTarget")
        cdp.send("Browser.setWindowBounds", {
            "windowId": win["windowId"],
            "bounds": {"left": 0, "top": 0, "width": 1000, "height": 760,
                       "windowState": "normal"},
        })
        note(f"实验窗口已摆到屏幕左上 (0,0) 1000×760（windowId={win.get('windowId')}）")
    except Exception as e:
        note(f"⚠️ 摆窗口失败（{type(e).__name__}: {e}）—— 改用它当前的位置算坐标")
    page2.bring_to_front()
    time.sleep(1.5)

    # 由**页面自己**报出几何，再换算成屏幕坐标 —— 比按"标题栏约 31 px"去估准得多。
    geo = page2.evaluate("""() => {
      const r = el => { const b = el.getBoundingClientRect(); return [b.x, b.y, b.width, b.height]; };
      return { sx: window.screenX, sy: window.screenY,
               ow: window.outerWidth, iw: window.innerWidth,
               oh: window.outerHeight, ih: window.innerHeight,
               txt: r(document.getElementById('txt')), blank: r(document.getElementById('blank')) };
    }""")
    # 客户区在屏幕上的起点：外框宽 - 视口宽 = 左右边框（各一半）；
    # 外框高 - 视口高 = 标题栏 + 下边框（几乎全在顶部）。
    dx = max(0.0, (geo["ow"] - geo["iw"]) / 2)
    dy = max(0.0, (geo["oh"] - geo["ih"]) - dx)
    ox, oy = geo["sx"] + dx, geo["sy"] + dy

    def center_of(rect):
        return (int(ox + rect[0] + rect[2] / 2), int(oy + rect[1] + rect[3] / 2))

    inbox = center_of(geo["txt"])        # 文本区 ⇒ 期望 I 型（Monochrome）
    notbox = center_of(geo["blank"])     # 空白区 ⇒ 期望箭头（Color）
    note(f"实验窗口客户区起点推算为 ({ox:.0f},{oy:.0f})；"
         f"文本区中心 {inbox}、空白区中心 {notbox}")

    set_cursor(*notbox)
    # ⚠️ 诊断**每 5 秒一行** ⇒ 必须等一轮多一点，否则读到的还是上一个位置的入参。
    time.sleep(6.5)
    seg_arrow = tray_cursor_lines(tray_mark)
    a = parse_shapes(seg_arrow)

    set_cursor(*inbox)
    time.sleep(6.5)
    seg_all = tray_cursor_lines(tray_mark)
    b = parse_shapes(seg_all[len(seg_arrow):])

    arrow = a[-1] if a else None
    ibeam = b[-1] if b else None
    note(f"光标 → 空白区 {notbox} ⇒ {arrow}")
    note(f"光标 → 文本区 {inbox} ⇒ {ibeam}")

    # ⚠️ 自检：两次采到的**坐标**必须真的分开了，否则"类型不同"全都无从谈起
    #    （而这一条恰恰最容易被忽略：两次都读到同一行入参时，类型当然一样）。
    if arrow and ibeam:
        gap = abs(arrow["y"] - ibeam["y"])
        check("两次采样确实落在两个不同的位置（按被控端报回的坐标）", gap > 80,
              f"y {arrow['y']} vs {ibeam['y']}（差 {gap}）")
    else:
        check("空白区与文本区都采到了指针入参", False,
              "有一段没采到 ⇒ 下面关于类型的结论不成立")

    for label, rec, want in (("空白区（期望箭头）", arrow, notbox),
                             ("文本区（期望 I 型）", ibeam, inbox)):
        if rec is None:
            check(f"{label}拿到了形状（形状字节 > 0）", False, "这一次一段形状都没读到")
            continue
        cands = shape_bytes_candidates(rec["w"], rec["h"])
        hits = [k for k, v in cands.items() if v == rec["bytes"]]
        check(f"{label}的形状字节数与尺寸自洽", bool(hits) and rec["bytes"] > 0,
              f"{rec['type']} {rec['w']}×{rec['h']} {rec['bytes']} 字节（位置 {rec['x']},{rec['y']}）⇒ "
              + (", ".join(hits) if hits else f"一个候选都不匹配：{cands}"))

        # 🔥 **坐标自洽**：位图左上角 + 热点 == 我们设的那个点。
        #    这一条证明的是"整条链路上的指针坐标是对的（含热点语义）"，
        #    而不只是"有个东西在动"。没有它，上一步那个 8~9 px 会被当成错位。
        tip = (rec["x"] + rec["hx"], rec["y"] + rec["hy"])
        check(f"{label}：被控端报回的位置 + 热点 == 脚本设的位置", 
              abs(tip[0] - want[0]) <= 2 and abs(tip[1] - want[1]) <= 2,
              f"报回 {rec['x']},{rec['y']} + 热点 {rec['hx']},{rec['hy']} = {tip}，目标 {want}")

    if arrow and ibeam:
        note(f"两次的类型：空白区={arrow['type']} / 文本区={ibeam['type']}"
             + ("（**不同** ⇒ 合成器里两条分支各被真机走到一次）" if arrow["type"] != ibeam["type"]
                else "（相同 ⇒ 本机环境没给出第二种类型）"))
        if arrow["type"] == ibeam["type"]:
            # 🔴 这**不是**缺陷：给什么形状由光标主题决定。如实记录、写进挂账，
            #    绝不用一条 FAIL 把它说成"代码坏了"（假 FAIL 比不做断言更坏）。
            note("⚠️ 只有一种类型出现 ⇒ 生产路径上另一条分支**未被真机覆盖**（挂账）；"
                 "算法本身由 PointerCompositorTests 兜着")

    page2.close()


def run_browser(base_url, agent_id, headful, pointer, backdrops=False, shapes=False,
                metrics_seconds=8.0, tray_mark=0, chunk_threshold_kb=0, idle_seconds=0.0,
                panels=False):
    """浏览器半边。返回退出码。"""
    from playwright.sync_api import sync_playwright

    print()
    print("── 浏览器半边（系统 Edge + WebCodecs）──")

    _ct = _win32()
    screen_w = _ct.windll.user32.GetSystemMetrics(0)
    screen_h = _ct.windll.user32.GetSystemMetrics(1)

    with sync_playwright() as p:
        browser = p.chromium.launch(
            channel=EDGE_CHANNEL,
            headless=not headful,
            args=["--disable-gpu", "--no-sandbox"],
        )
        note(f"内核：{browser.version}（channel={EDGE_CHANNEL}）")
        ctx = browser.new_context(viewport={"width": 1680, "height": 1000})
        page = ctx.new_page()

        errors = []
        page.on("pageerror", lambda e: errors.append("pageerror: " + str(e)))
        page.on("console", lambda m: errors.append("console." + m.type + ": " + m.text)
                if m.type in ("error",) else None)

        try:
            page.goto(base_url, wait_until="domcontentloaded", timeout=30000)
            page.fill('input[autocomplete="username"]', ADMIN_USER)
            page.fill('input[type="password"]', ADMIN_PASS)
            page.click('button:has-text("登录")')

            # 远程桌面入口只对管理员可见
            page.wait_for_selector('button:has-text("远程桌面")', timeout=20000)
            page.click('button:has-text("远程桌面")')

            # 设备下拉里的在线机器
            page.wait_for_selector('select', timeout=20000)
            page.wait_for_function(
                "() => [...document.querySelectorAll('select option')]"
                ".some(o => o.textContent.includes('在线'))", timeout=20000)
            page.select_option('select', index=1)

            page.click('button:has-text("开始观看")')
            note("已点「开始观看」，等画面 …")

            got_frame, stats = wait_for_stats(page, "decoded", min_value=1, timeout=90.0)
            check("解码器有产出（画面真的解出来了）", got_frame,
                  f"实得 {stats}")
            if not got_frame:
                dump_diagnostics(page, errors)
                return 1

            # 解不出 == 0、重建不涨 是"画面不花"的判据
            decoded, dec_err, resets = stats
            check("解不出 == 0", dec_err == 0, f"实得 {dec_err}")
            check("解码器重建 == 0（解码链没断过）", resets == 0, f"实得 {resets}")

            time.sleep(3.0)
            stats2 = read_stats(page)
            note(f"3s 后统计：{stats2}")

            shot = os.path.join(OUT_DIR, "viewer-h264.png")
            page.screenshot(path=shot)
            note(f"截图：{os.path.relpath(shot, REPO)}")

            # ── 读数：**先采**，因为此时桌面还是"自然状态" ──
            #    （后面的指针 / 壁纸实验都会动桌面，读数放在它们之前才代表常态）
            if metrics_seconds > 0:
                collect_metrics(page, seconds=metrics_seconds)

            # ── 🔥 分片判据（诊断模式才有意义）──
            #    ⚠️ 位置很要紧：必须在**收摊之前**（会话还在、stats 还读得到），
            #       而且要在 `collect_metrics` **之后**（那时已经跑了足够多帧，
            #       "拼出来的帧数 > 0" 才有意义 —— 刚建完会话时它一定是 0）。
            if chunk_threshold_kb > 0:
                verify_chunking(page, chunk_threshold_kb)

            # ── 🔥 三个前端入口（多屏 / 质量档 / 剪贴板）──
            #    ⚠️ 位置：必须在 collect_metrics **之后**（切档会让编码器重建，
            #       放前面会把"重建"记进基线读数）、在 pointer **之前**
            #       （指针实验会挪真实光标，和这里的判据无关）。
            if panels:
                verify_panels(page, OUT_DIR)

                # 🔥 紧跟着跑：它要复用同一路会话（不另开一路），
                #    而且会把宿主剪贴板清一次 → 必须在 `verify_panels` 的
                #    "读取/写入"判据**之后**（那些判据自己对剪贴板有期望）。
                verify_clipboard_states(page, browser, OUT_DIR)

            if pointer:
                verify_pointer(page, OUT_DIR)

            # ── 深/浅底（零副作用：只是在真实桌面上找暗区 / 亮区）──
            #    ⚠️ 有头模式也能跑：此时"暗区 / 亮区"可能落在浏览器窗口自身上，
            #       但判据（指针在/不在同一小块）照样成立。
            if backdrops:
                verify_backdrops(page, OUT_DIR, screen_w, screen_h)

            # ── 形状类型（I 型光标只在窗口里的文本区上 ⇒ 必须 headful）──
            if shapes:
                if not headful:
                    note("⚠️ --shapes 需要 --headful（I 型光标只出现在窗口里的文本区上）—— 已跳过")
                else:
                    verify_cursor_shapes(ctx, OUT_DIR, screen_w, screen_h, tray_mark)

            # ── 静止降帧（必须**最后**做：它要盖住整块屏，别人动光标就白测了）──
            if idle_seconds > 0:
                verify_idle_backoff(page, dwell=idle_seconds)

            if errors:
                note(f"页面错误 {len(errors)} 条（前 5）：")
                for e in errors[:5]:
                    print("      " + e[:200])
            return 0 if not FAILURES else 1
        except Exception as e:
            check("浏览器流程走完", False, f"{type(e).__name__}: {e}")
            try:
                dump_diagnostics(page, errors)
            except Exception:
                pass
            return 1
        finally:
            ctx.close()
            browser.close()


def read_stats(page):
    """读「解码器（产出 / 解不出 / 重建）」→ (decoded, decodeErrors, resets)。"""
    txt = page.evaluate("""() => {
      const els = [...document.querySelectorAll('.rd-stat')];
      const el = els.find(e => e.textContent.includes('解码器'));
      if (!el) return null;
      const s = el.querySelector('strong');
      return s ? s.textContent.trim() : null;
    }""")
    if not txt:
        return None
    parts = [p.strip() for p in txt.split("/")]
    try:
        return tuple(int(p) for p in parts[:3])
    except ValueError:
        return None


def wait_for_stats(page, field, min_value, timeout):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = read_stats(page)
        if last:
            decoded = last[0]
            if decoded >= min_value:
                return True, last
        time.sleep(0.5)
    return False, last


def dump_diagnostics(page, errors):
    """画面没出来时把能捞的都捞出来（否则只剩一句"超时"）。"""
    print("  ── 诊断 ──")
    try:
        cards = page.evaluate("""() => {
          return [...document.querySelectorAll('.rd-stat')].map(e => e.textContent.trim());
        }""")
        for c in cards:
            print("      " + " ".join(c.split()))
    except Exception as e:
        print(f"      读统计失败：{e}")
    try:
        body = page.inner_text("body")
        for line in body.splitlines():
            s = line.strip()
            if s and ("失败" in s or "错误" in s or "无法" in s or "收场" in s or "未" in s):
                print("      » " + s[:160])
    except Exception:
        pass
    if errors:
        print(f"      页面错误 {len(errors)} 条：")
        for e in errors[:8]:
            print("      " + e[:200])


if __name__ == "__main__":
    sys.exit(main())
