#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · 设备授权冒烟（M3-S6）

对着一个**全新实例**（`Server:License:Enforcement=Enforce` + `AutoTrial=false`）跑，
验证授权闸门在真服务上的实际行为 —— 而不是只看单测的矩阵。

覆盖：
  A. 未授权 + Enforce   · 价值点全线 403 license-required
                        · 登录 / 只读 / 授权导入 / 用户管理 放行
                        · 对接码监听停摆（**真 UDP 打进去，确认一条都不出网**）
                        · /readyz 仍报 ready，但把 licenseBlocked 如实标出来
  B. 验签真的在工作      · 用**另一把私钥**签出的授权必须被拒（signatureinvalid）
                          这一条是"能解码就算过"与"真的验签"的分水岭
  C. 导入真授权          · 放行；监听**自动重启**（真 UDP 恢复出网）；/readyz 归位
  D. 移除授权            · 重新停摆（真 UDP 再次收不到）

为什么必须一条条发真 UDP，而不只看 /readyz 的 listener 字段：
    `listener=false` 只是"监听服务自称没跑"。真正要证明的是
    **"绕开 Web 界面直接往 UDP 端口发对接码，也触发不了动作"** ——
    这正是当初坚持要在监听层再设一道闸门的理由（HTTP 闸门管不着 UDP）。

⚠️ 密钥：签发工具已**内嵌**厂商私钥（见 tools/LicenseToolKey.cs），
    因此 C 段直接调 `gen`、**不传 --key** —— 这本身就是对新模型的一条验证
    （"工具不依赖任何密钥文件也能签出服务端收得下的授权"）。
    B 段要一把"不配对的"私钥，用 new-key 现造即可。
    只有当签发工具是"未内嵌密钥"的构建时（构建时缺
    `.tools/license-keys/vendor-private.pem`），才需要显式指定私钥 ——
    见 `--key` / 环境变量 `CONTROL_SERVER_VENDOR_KEY`。

用法：
    python deploy/smoke/license-smoke.py <baseUrl> [<pairingPort>] [<udpSinkPort>]
    python deploy/smoke/license-smoke.py --tool <签发工具或.dll> --key <私钥.pem> ...

    baseUrl      默认 http://127.0.0.1:18110
    pairingPort  实例的对接码监听端口（默认 18098）
    udpSinkPort  本脚本自己 bind 的收包端口（默认 18097）

约定：
  · 所有 HTTP 调用绕过本机代理（Git Bash 常配代理，访问 127.0.0.1 会 502）。
  · 脚本会**改该实例的配置与授权**，请只对临时实例跑。
  · 退出码：0 全通过；1 有用例失败；2 前置条件不满足（找不到签发工具 / 私钥 / 服务未就绪）。
"""

import http.cookiejar
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))


# ═══════════════════════════════════════════════════════════════
# 命令行
# ═══════════════════════════════════════════════════════════════

argv = sys.argv[1:]


def take_option(name):
    """摘出 `--name value`；不存在返回 None。"""
    if name not in argv:
        return None
    i = argv.index(name)
    if i + 1 >= len(argv):
        print(f"[X] {name} 后面缺一个值")
        sys.exit(2)
    value = argv[i + 1]
    del argv[i:i + 2]
    return value


TOOL_OVERRIDE = take_option("--tool")
KEY_OVERRIDE = take_option("--key")

BASE = (argv[0] if len(argv) > 0 else "http://127.0.0.1:18110").rstrip("/")
PAIRING_PORT = int(argv[1]) if len(argv) > 1 else 18098
SINK_PORT = int(argv[2]) if len(argv) > 2 else 18097

SEED_ADMIN = "admin"
SEED_PASSWORD = "admin123"


# ═══════════════════════════════════════════════════════════════
# 签发工具 / 密钥 探测
# ═══════════════════════════════════════════════════════════════

def to_native_path(path):
    """
    把 Git Bash 风格路径转成 Windows 路径。
    `export DOTNET_ROOT=/c/Users/...` 这类值在 Windows 上的 Python 里认不出来
    （`/c/...` 不是合法盘符路径），会让"dotnet 明明装了却探测不到"。
    """
    if os.name == "nt" and len(path) >= 3 and path[0] == "/" and path[2] == "/":
        return path[1].upper() + ":" + path[2:]
    return path


def detect_dotnet():
    """找一个能跑 framework-dependent dll 的 dotnet。"""
    root = os.environ.get("DOTNET_ROOT")
    if root:
        candidate = os.path.join(to_native_path(root), "dotnet.exe")
        if os.path.isfile(candidate):
            return candidate

    which = shutil.which("dotnet")
    if which:
        return which

    # 本机开发环境的约定路径（见 .workbuddy/memory/MEMORY.md §4）：
    # 系统 dotnet 缺 SDK，SDK 装在 .workbuddy/binaries/dotnet8 下。
    home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
    candidate = os.path.join(home, ".workbuddy", "binaries", "dotnet8", "dotnet.exe")
    if os.path.isfile(candidate):
        return candidate

    return None


def detect_tool():
    """返回 (argv 前缀, 描述) 或 (None, 原因)。"""
    candidates = []
    if TOOL_OVERRIDE:
        candidates.append(os.path.abspath(TOOL_OVERRIDE))
    env = os.environ.get("CONTROL_SERVER_LICENSE_TOOL")
    if env:
        candidates.append(env)
    candidates.append(os.path.join(REPO, "dist", "win-x64-license-tool", "ControlServer.LicenseTool.exe"))
    for config in ("Debug", "Release"):
        candidates.append(os.path.join(
            REPO, "tools", "ControlServer.LicenseTool", "bin", config,
            "net8.0", "ControlServer.LicenseTool.dll"))

    for path in candidates:
        if not path or not os.path.isfile(path):
            continue
        if path.lower().endswith(".dll"):
            dotnet = detect_dotnet()
            if not dotnet:
                return None, f"找到 {path} 但找不到 dotnet 来运行它（可设 DOTNET_ROOT）"
            return [dotnet, path], path
        return [path], path

    return None, (
        "找不到签发工具。先跑 "
        "`powershell -NoProfile -ExecutionPolicy Bypass -File deploy\\windows\\build-license-tool.ps1`，"
        "或用 --tool 指定 ControlServer.LicenseTool.exe / .dll")


def resolve_key():
    key = KEY_OVERRIDE or os.environ.get("CONTROL_SERVER_VENDOR_KEY")
    if key:
        return os.path.abspath(key)
    return os.path.join(REPO, ".tools", "license-keys", "vendor-private.pem")


TOOL, TOOL_DESC = detect_tool()
KEY_PATH = resolve_key()

print("══════════════════ 授权冒烟 ══════════════════")
print(f"  服务：{BASE}    对接码端口：{PAIRING_PORT}    sink：{SINK_PORT}")

if TOOL is None:
    print(f"[X] {TOOL_DESC}")
    sys.exit(2)

if not os.path.isfile(KEY_PATH):
    print(f"[!] 未找到 *.pem 私钥：{KEY_PATH}")
    print("    这不影响本脚本：签发工具已内嵌厂商私钥，C 段直接用它签发。")
    print("    只有当工具是「未内嵌密钥」的构建时才需要它 ——")
    print("    可用 --key / 环境变量 CONTROL_SERVER_VENDOR_KEY 指定。")
else:
    print(f"  参考私钥：{KEY_PATH}   （C 段不传 --key，走工具内嵌密钥）")

print(f"  签发工具：{TOOL_DESC}")


def run_tool(args, timeout=120):
    """跑签发工具，返回 (returncode, 合并输出)。"""
    proc = subprocess.run(
        TOOL + args,
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        cwd=REPO, timeout=timeout,
    )
    return proc.returncode, ((proc.stdout or "") + (proc.stderr or ""))


# ═══════════════════════════════════════════════════════════════
# HTTP
# ═══════════════════════════════════════════════════════════════

class Session:
    """一个独立会话（独立 Cookie jar）。"""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(self.jar),
        )

    def call(self, method, path, body=None, timeout=60):
        data, headers = None, {}
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=timeout) as resp:
                status, content = resp.status, resp.read()
        except urllib.error.HTTPError as e:
            status, content = e.code, e.read()

        try:
            return status, json.loads(content.decode("utf-8"))
        except Exception:
            return status, content.decode("utf-8", errors="replace")


def wait_for(call_fn, predicate, timeout=15.0, interval=0.3):
    """轮询直到 predicate(body) 为真。返回 (是否达成, 最后一次 body)。"""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        status, body = call_fn()
        last = body
        if status == 200 and isinstance(body, dict) and predicate(body):
            return True, body
        time.sleep(interval)
    return False, last


# ═══════════════════════════════════════════════════════════════
# 真 UDP
# ═══════════════════════════════════════════════════════════════

def probe_udp(payload, wait=2.5, idle=1.0):
    """
    往对接码端口发一包，收集 sink 上收到的所有数据报。
    返回 list[bytes]。期望"不该有"时 wait 就是它要等的静默期。
    """
    sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sink.bind(("127.0.0.1", SINK_PORT))
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    try:
        sink.settimeout(wait)
        sender.sendto(payload, ("127.0.0.1", PAIRING_PORT))

        got = []
        try:
            while True:
                data, _ = sink.recvfrom(65535)
                got.append(data)
                sink.settimeout(idle)
        except socket.timeout:
            pass
        return got
    finally:
        sink.close()
        sender.close()


def recv_udp(wait=3.0, idle=0.8):
    """只收（不发）—— 用在服务端自己往外发的场景（/api/test/udp）。"""
    sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sink.bind(("127.0.0.1", SINK_PORT))
    try:
        sink.settimeout(wait)
        got = []
        try:
            while True:
                data, _ = sink.recvfrom(65535)
                got.append(data)
                sink.settimeout(idle)
        except socket.timeout:
            pass
        return got
    finally:
        sink.close()


# ═══════════════════════════════════════════════════════════════
# 准备：登录 + 预置一份最小配置（未授权时也能写，闸门放行）
# ═══════════════════════════════════════════════════════════════

admin = Session()

status, body = admin.call("POST", "/api/auth/login", {"userName": SEED_ADMIN, "password": SEED_PASSWORD})
if status != 200:
    print(f"[X] 登录失败（{status}）：{body}")
    print("    本脚本要求实例处于**出厂态**（admin/admin123 且未被锁定）。")
    sys.exit(2)

print()

# 预置配置：一个带对接码的按钮，动作指向 sink。
# ⚠️ 这一步本身就在断言"未授权时配置写入放行" —— 后面 A 段会把它作为 UDP 停摆的**前提**：
#    没有配置时"发对接码没反应"是必然的，证明不了任何事。
PING_CODE = "LIC-CODE"
PING_PAYLOAD = "LIC-OK"

status, cfg = admin.call("GET", "/api/config")
settings = dict(cfg.get("settings") or {}) if isinstance(cfg, dict) else {}
version = cfg.get("version") if isinstance(cfg, dict) else None

settings["buttons"] = [{
    "id": "lic-smoke-button",
    "order": 0,
    "label": "授权冒烟按钮",
    "buttonColor": "#512DA8",
    "textColor": "#FFFFFF",
    "nameFontSize": 13,
    "buttonImage": "",
    "width": 140,
    "height": 80,
    "layoutX": 0,
    "layoutY": 0,
    "timerEnabled": False,
    "timerMode": "Daily",
    "timerTime": "08:00",
    "timerTimes": ["08:00"],
    "timerDays": [1, 2, 3, 4, 5],
    "monthlyPlans": [],
    "pairingCode": PING_CODE,
    "pairingCodeType": "UTF-8",
    "actions": [{
        "id": "lic-smoke-action",
        "name": "回声",
        "targetIp": "127.0.0.1",
        "targetPort": SINK_PORT,
        "protocol": "UDP",
        "dataType": "UTF-8",
        "dataContent": PING_PAYLOAD,
        "delayMs": 0,
        "pairingCode": "",
        "pairingCodeType": "String",
    }],
}]
settings["subButtonSets"] = []

status, saved = admin.call("PUT", "/api/config", {
    "settings": settings,
    "expectedVersion": version,
    "updatedBy": "license-smoke",
})
print(f"[准备] 预置配置（对接码 {PING_CODE!r} → UDP 127.0.0.1:{SINK_PORT}）：PUT /api/config → {status}")
if status != 200:
    print(f"[X] 预置配置失败：{saved}")
    sys.exit(2)
print()


# ═══════════════════════════════════════════════════════════════
# A · 未授权 + Enforce
# ═══════════════════════════════════════════════════════════════

print("[A] 未授权 + Enforce：该拦的拦、该放的放")

status, lic = admin.call("GET", "/api/license")
check("GET /api/license → 200", status == 200, f"实得 {status}")
lic = lic if isinstance(lic, dict) else {}

check("状态为「未授权」", lic.get("status") == "NotAuthorized", f"实得 {lic.get('status')}")
check("IsUsable = false", lic.get("isUsable") is False, f"实得 {lic.get('isUsable')}")
check("配置档位 = Enforce", lic.get("configuredEnforcement") == "Enforce",
      f"实得 {lic.get('configuredEnforcement')}")
check("生效档位 = Enforce", lic.get("enforcement") == "Enforce",
      f"实得 {lic.get('enforcement')}")
check("未授权时不带授权文件信息", lic.get("document") is None, f"实得 {lic.get('document')}")

MACHINE = lic.get("machineCode") or ""
check("返回本机机器码（后面据此签发）", len(MACHINE.replace("-", "")) == 16, f"实得 {MACHINE!r}")

status, req = admin.call("GET", "/api/license/request")
check("授权申请文本可读（现场自救通道）→ 200", status == 200, f"实得 {status}")
check("申请文本里的机器码与状态一致",
      isinstance(req, dict) and req.get("machineCode") == MACHINE,
      f"实得 {req.get('machineCode') if isinstance(req, dict) else req}")

# ── 探活：停摆是策略决定，不是故障 ──
status, rz = admin.call("GET", "/readyz")
rz = rz if isinstance(rz, dict) else {}
check("/readyz 在停摆时仍报 ready（否则编排器会陷入重启死循环）",
      status == 200 and rz.get("status") == "ready", f"实得 {status} {rz.get('status')}")
check("/readyz 如实标出 licenseBlocked = true", rz.get("licenseBlocked") is True,
      f"实得 {rz.get('licenseBlocked')}")
check("对接码监听已停摆（listener = false）", rz.get("listener") is False,
      f"实得 {rz.get('listener')}")

# ── 价值点：四个端点全部 403 ──
GATED_ENDPOINTS = [
    ("POST", "/api/actions/execute", {"buttonId": "lic-smoke-button"}, "执行动作"),
    ("POST", "/api/timers/lic-smoke-button/fire", None, "手动触发定时点"),
    ("POST", "/api/test/udp", {"host": "127.0.0.1", "port": SINK_PORT, "payload": "X"}, "直发 UDP"),
    ("POST", "/api/test/loopback", None, "自环测试"),
]

for method, path, payload, label in GATED_ENDPOINTS:
    status, body = admin.call(method, path, payload)
    code = body.get("code") if isinstance(body, dict) else None
    check(f"未授权时「{label}」被拦 → 403 license-required",
          status == 403 and code == "license-required",
          f"实得 {status} code={code!r}")

# ── 放行对照：只读 / 纯计算 / 管理面 ──
ALLOWED_ENDPOINTS = [
    ("GET", "/api/timers", None, "定时总览（只读）"),
    ("GET", "/api/config", None, "配置（只读）"),
    ("GET", "/api/users", None, "用户管理"),
    ("GET", "/api/system/info", None, "系统信息"),
    ("GET", "/api/logs/recent", None, "最近日志"),
    ("POST", "/api/actions/preview", {"dataType": "UTF-8", "dataContent": "AB"}, "编码预览（纯计算）"),
]

for method, path, payload, label in ALLOWED_ENDPOINTS:
    status, _ = admin.call(method, path, payload)
    check(f"未授权时「{label}」放行 → 200", status == 200, f"实得 {status}")

# ── 真 UDP：停摆必须是"绕开界面也打不动"，而不只是"接口返回 403" ──
got = probe_udp(PING_CODE.encode("utf-8"), wait=2.5)
check("未授权时对接码打进去也不产生任何出网（监听确实停摆）",
      len(got) == 0, f"实得 {len(got)} 条：{got}")


# ═══════════════════════════════════════════════════════════════
# B · 验签真的在工作（另一把私钥签出来的授权必须被拒）
# ═══════════════════════════════════════════════════════════════

print("\n[B] 验签真的在工作：另一把私钥签出的授权必须被拒")

WORK = tempfile.mkdtemp(prefix="cs-license-smoke-")

try:
    other_key = os.path.join(WORK, "other-private.pem")
    rc, out = run_tool(["new-key", "--out", other_key])
    check("生成第二把（不配对的）密钥", rc == 0 and os.path.isfile(other_key),
          f"退出码 {rc}；{out.strip().splitlines()[-1] if out.strip() else ''}")

    foreign = os.path.join(WORK, "foreign.lic")
    rc, out = run_tool([
        "gen", "--type", "full", "--bind", MACHINE, "--expire", "2027-10-08",
        "--customer", "冒烟-未授权密钥", "--key", other_key,
        "--offline", "--out", foreign,
    ])
    check("用另一把私钥成功签出一份「格式完全正确」的授权", rc == 0, f"退出码 {rc}；{out.strip()[-200:]}")

    if rc == 0 and os.path.isfile(foreign):
        with open(foreign, "r", encoding="utf-8") as f:
            foreign_text = f.read()

        status, body = admin.call("POST", "/api/license/import", {"licenseText": foreign_text})
        code = (body.get("status") if isinstance(body, dict) else "") or ""
        check("导入被拒 → 400", status == 400, f"实得 {status}")
        check("拒绝原因明确是「签名不合法」（而不是「格式解析不了」）",
              code.lower() == "signatureinvalid", f"实得 status={code!r}")

        status, lic2 = admin.call("GET", "/api/license")
        check("被拒之后状态仍是「未授权」（没有被偷偷接受）",
              isinstance(lic2, dict) and lic2.get("isUsable") is False,
              f"实得 {lic2.get('status') if isinstance(lic2, dict) else lic2}")
    else:
        check("导入被拒 → 400", False, "上一步未签出授权，跳过")
        check("拒绝原因明确是「签名不合法」", False, "上一步未签出授权，跳过")
        check("被拒之后状态仍是「未授权」", False, "上一步未签出授权，跳过")

    # ═══════════════════════════════════════════════════════════
    # C · 导入真授权 → 放行 + 监听恢复
    # ═══════════════════════════════════════════════════════════

    print("\n[C] 导入真授权：放行 + 监听自动重启（不必重启服务）")

    good = os.path.join(WORK, "good.lic")
    rc, out = run_tool([
        "gen", "--type", "full", "--bind", MACHINE, "--expire", "2027-10-08",
        "--customer", "冒烟", "--sn", "SMOKE-001", "--vendor", "VENDOR-SMOKE",
        "--offline", "--out", good,
    ])
    check("用「工具内嵌密钥」签出一份服务端会接受的授权（不传 --key）",
          rc == 0 and "内置厂商密钥" in out, f"退出码 {rc}；{out.strip()[-200:]}")

    with open(good, "r", encoding="utf-8") as f:
        good_text = f.read()

    status, body = admin.call("POST", "/api/license/import", {"licenseText": good_text})
    check("导入授权 → 200 + ok", status == 200 and isinstance(body, dict) and body.get("ok") is True,
          f"实得 {status} {body if not isinstance(body, dict) else body.get('status')}")

    status, lic3 = admin.call("GET", "/api/license")
    lic3 = lic3 if isinstance(lic3, dict) else {}
    check("状态变为「已授权」", lic3.get("status") == "Authorized", f"实得 {lic3.get('status')}")
    check("IsUsable = true", lic3.get("isUsable") is True, f"实得 {lic3.get('isUsable')}")
    check("生效档位仍是 Enforce（授权后不降档）", lic3.get("enforcement") == "Enforce",
          f"实得 {lic3.get('enforcement')}")
    check("授权明细里的机器码与申请一致",
          (lic3.get("document") or {}).get("boundMachineCode") == MACHINE,
          f"实得 {(lic3.get('document') or {}).get('boundMachineCode')}")

    # 监听重启是**闸门事件回调**触发的（不是重启进程），所以给它一点点时间
    ok, rz2 = wait_for(
        lambda: admin.call("GET", "/readyz"),
        lambda b: b.get("listener") is True and b.get("licenseBlocked") is False,
        timeout=15.0)
    check("授权后监听自动重启（/readyz listener = true 且 licenseBlocked = false）",
          ok, f"实得 {rz2}")

    # ── 价值点恢复 ──
    status, body = admin.call("POST", "/api/actions/execute", {"buttonId": "lic-smoke-button"})
    check("授权后「执行动作」放行 → 200 ok", status == 200 and
          isinstance(body, dict) and body.get("ok") is True,
          f"实得 {status} {body if not isinstance(body, dict) else body.get('summary')}")

    status, _ = admin.call("POST", "/api/timers/no-such-ref/fire")
    check("授权后不存在的定时点报 404（不再是 403 —— 闸门确实开了）",
          status == 404, f"实得 {status}")

    # ── 真 UDP 恢复：这才是"功能真的回来了" ──
    got = probe_udp(PING_CODE.encode("utf-8"), wait=6.0)
    check("授权后对接码能触发出网（监听 → 匹配 → 动作）",
          len(got) == 1 and got[0] == PING_PAYLOAD.encode("utf-8"),
          f"实得 {len(got)} 条：{got}")

    # ── 服务端自己发（/api/test/udp 真出网）──
    listen = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    listen.bind(("127.0.0.1", SINK_PORT))
    listen.settimeout(5.0)
    try:
        status, body = admin.call(
            "POST", "/api/test/udp",
            {"host": "127.0.0.1", "port": SINK_PORT, "payload": "DIRECT-OK"})
        check("授权后 /api/test/udp 可调用 → 200 ok",
              status == 200 and isinstance(body, dict) and body.get("ok") is True,
              f"实得 {status}")
        try:
            data, _ = listen.recvfrom(65535)
        except socket.timeout:
            data = None
        check("该报文真的到达了目标端口（不是只返回 ok）",
              data == b"DIRECT-OK", f"实得 {data!r}")
    finally:
        listen.close()

    # ═══════════════════════════════════════════════════════════
    # D · 移除授权 → 重新停摆
    # ═══════════════════════════════════════════════════════════

    print("\n[D] 移除授权：重新停摆")

    status, body = admin.call("DELETE", "/api/license")
    check("移除授权 → 200 ok", status == 200 and isinstance(body, dict) and body.get("ok") is True,
          f"实得 {status}")

    status, lic4 = admin.call("GET", "/api/license")
    check("状态回到「未授权」",
          isinstance(lic4, dict) and lic4.get("isUsable") is False,
          f"实得 {lic4.get('status') if isinstance(lic4, dict) else lic4}")

    ok, rz3 = wait_for(
        lambda: admin.call("GET", "/readyz"),
        lambda b: b.get("listener") is False and b.get("licenseBlocked") is True,
        timeout=15.0)
    check("移除授权后监听重新停摆", ok, f"实得 {rz3}")

    status, body = admin.call("POST", "/api/actions/execute", {"buttonId": "lic-smoke-button"})
    code = body.get("code") if isinstance(body, dict) else None
    check("移除授权后「执行动作」又被拦 → 403 license-required",
          status == 403 and code == "license-required", f"实得 {status} code={code!r}")

    got = probe_udp(PING_CODE.encode("utf-8"), wait=2.5)
    check("移除授权后对接码再次打不动（监听已停）", len(got) == 0, f"实得 {len(got)} 条：{got}")

finally:
    shutil.rmtree(WORK, ignore_errors=True)


# ═══════════════════════════════════════════════════════════════

print()
print(f"结果：{len(PASS)}/{len(PASS) + len(FAIL)} 通过")
if FAIL:
    print("未通过：")
    for name in FAIL:
        print(f"  - {name}")
sys.exit(0 if not FAIL else 1)
