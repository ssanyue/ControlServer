#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · 客户端Server 冒烟（M4.1）

覆盖这些线：
  A. 安装包           信息端点 → 放包 → 尺寸/版本 → 真下载（含 Range 断点续传）
  B. 准入与在线表      免凭据注册 → 列表/改名/拉黑（2026-10-15 取代「吊销」）/移除
  C. 发现协议         真发一条 CS-DISCOVER 到发现端口，核对收到的 CS-HERE
  D. 真客户端端到端    启动 ControlServer.Agent.Service.exe（真的那个 exe）→
                      它自己发现/注册/连上 → 服务端看到"在线" → 下发重启指令 →
                      杀掉进程 → 服务端在阈值内变"离线"
  E. 权限矩阵与移除    Operator 能读不能写 → 移除记录后列表条数正好减 1
  F. 安装脚本静态检查  温度开关的**默认值方向**（需求①的落点：.iss 向导复选框 +
                      install-agent.ps1 的默认值）—— 不需要跑服务，读文件即可
  G. 离线客户端独立可用  起一台**找不到中心**的客户机：崭新机器默认就监听 8090 →
                      显式关掉 UDP + 换端口后**仍然监听** → 真发一条明文 DOWN 必须被认出来
                      （演练模式，绝不真关机）→ 本机配置页的「服务端」卡片如实说"还不知道"
                      （2026-10-14，需求①）；明细**默认收起**、收起时只留"已连接中心 + 地址"
                      （2026-10-15，按结构断言 —— 见 PageStructure）
  H. 移除之后加得回来    起一台真客户机 → 管理员「移除」它（硬删记录）→
                      它必须**自己重新注册**回来（新 Id + 日志里第二次「注册成功」）
                      （2026-10-14，修"移除后无法再次添加"）

关于 D 段的时间参数
    run-all 给本场景传了**调快过**的心跳与阈值（5s / 15s / 30s），
    否则"关机 → 90 秒后变离线"这条链路会让冒烟多等一分半。
    这里验证的是**链路**（心跳能落成在线、断开能落成离线）；
    **默认阈值本身**的正确性由 Core 单测
    （`AgentProtocolAndPresenceTests.The_default_thresholds_satisfy_the_ninety_second_offline_acceptance`）钉住。

D 段是可降级的：找不到 Agent exe（例如只发布了服务端）时打印 SKIP 并继续，
不当成失败 —— 否则"跑一次冒烟需要先编客户端"会挡住绝大多数只改服务端的改动。

用法：
    python deploy/smoke/agent-smoke.py <baseUrl> <discoveryPort> <dataDir>

    # 想让 D 段去跑**打包产物**而不是开发产物：
    CS_AGENT_EXE=dist/agent/ControlServer.Agent.Service.exe python deploy/smoke/agent-smoke.py ...

约定：所有 HTTP 调用绕过本机代理（Git Bash 常配代理，访问 127.0.0.1 会 502）。
"""

import http.cookiejar
import html.parser
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18100").rstrip("/")
DISCOVERY_PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 9998
DATA_DIR = sys.argv[3] if len(sys.argv) > 3 else ""

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

SEED_ADMIN = "admin"
SEED_PASSWORD = "admin123"

INSTALLER_DIR_NAME = "downloads"
INSTALLER_FILE_NAME = "ControlServer-Agent-Setup-0.1.0.exe"

PASS, FAIL = [], []

# Agent 服务端 exe。默认用开发产物；找不到就跳过 D 段。
# ⚠️ 可用环境变量 CS_AGENT_EXE 指向**打包产物**（绿色版 / 安装目录里的那个），
#    用来验证"真正发出去的那个 exe"与开发产物行为一致 —— 打包会改
#    DebugType / 单文件 / 裁剪，这些差异**只在打包产物上才暴露**。
AGENT_EXE = os.environ.get("CS_AGENT_EXE") or os.path.join(
    REPO, "agents", "ControlServer.Agent.Service", "bin", "Debug", "net8.0",
    "ControlServer.Agent.Service.exe")


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))


def skip(name, why):
    print(f"  [SKIP] {name} —— {why}")


class Session:
    """一个独立的浏览器会话（独立 Cookie jar）。"""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),                # 绕过本机代理
            urllib.request.HTTPCookieProcessor(self.jar),   # 记住会话 Cookie
        )

    def call(self, method, path, body=None):
        data, headers = None, {}
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=60) as resp:
                return resp.status, _decode(resp.read()), dict(resp.headers)
        except urllib.error.HTTPError as e:
            return e.code, _decode(e.read()), dict(e.headers)

    def raw(self, method, path, headers=None):
        """原样返回字节流（下载 / Range 用）。"""
        req = urllib.request.Request(BASE + path, headers=headers or {}, method=method)
        try:
            with self.opener.open(req, timeout=60) as resp:
                return resp.status, resp.read(), dict(resp.headers)
        except urllib.error.HTTPError as e:
            return e.code, e.read(), dict(e.headers)


def _decode(content):
    try:
        return json.loads(content.decode("utf-8"))
    except Exception:
        return content.decode("utf-8", errors="replace")


# ═══════════════════════════════════════════════════════════════
# UDP 指令通道（M4.5）用的三个小工具
#
# 🔥 2026-10-13 起线路协议是**纯明文**（DOWN / RESTART / + / - / 0 / 1），
#    不再有签名密钥 —— 于是冒烟可以直接发一条合法报文，正向路径也在这里验。
#    "真执行关机/音量"仍由 Agent 单测覆盖（它用桩电源控制器，绝不真动手）。
# ═══════════════════════════════════════════════════════════════


def free_udp_port():
    """要一个此刻空闲的 UDP 端口（绑 0 让系统挑，随即释放）。"""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def udp_listening(port, pid):
    """
    该端口是否正被 **pid 这个进程** 监听。

    <p>只看端口号不够：本机上可能有别的程序也占着同一个端口 ——
    那会让"客户机绑上了"这条断言变成假阳性。所以必须连进程号一起比。</p>
    """
    try:
        raw = subprocess.run(["netstat", "-ano"], capture_output=True, timeout=20).stdout
    except Exception:
        return False

    for line in raw.decode("utf-8", errors="replace").splitlines():
        parts = line.split()
        # UDP 行形如：UDP    0.0.0.0:15000    *:*    1234
        if len(parts) < 4 or parts[0].upper() != "UDP":
            continue
        if parts[1].endswith(f":{port}") and parts[-1] == str(pid):
            return True

    return False


def udp_port_owner(port):
    """
    谁在监听这个 UDP 端口（返回 pid 字符串），没人听返回 None。

    <p>⚠️ 它存在的理由：G 段要断言"是**我们这台**客户机把 8090 绑起来了"。
    开发机上很可能本来就装着一个客户端服务（默认也听 8090）——
    只看端口号会把它的监听算成我们的，那是一条**假 PASS**；
    而我们的绑定真失败时，那条红又看不出原因。所以先问一句"开工前是谁占着"，
    占着就 SKIP 而不是硬判。</p>
    """
    try:
        raw = subprocess.run(["netstat", "-ano"], capture_output=True, timeout=20).stdout
    except Exception:
        return None

    for line in raw.decode("utf-8", errors="replace").splitlines():
        parts = line.split()
        if len(parts) < 4 or parts[0].upper() != "UDP":
            continue
        if parts[1].endswith(f":{port}"):
            return parts[-1]

    return None


def send_udp(port, text):
    """朝 127.0.0.1:port 发一条 UDP 报文。发不出去也不算错（端口没人听时本就如此）。"""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            sender.sendto(text.encode("utf-8"), ("127.0.0.1", port))
        return True
    except OSError:
        return False


def free_tcp_port():
    """要一个此刻空闲的 TCP 端口（绑 0 让系统挑，随即释放）。"""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def raw_get(url, timeout=5):
    """
    不带登录会话的裸 GET —— 专门给**客户机的本机配置页**用。

    <p>那个页面只绑 <c>127.0.0.1</c>、也不认中心的会话 Cookie，用管理中心那套
    <c>Session</c> 去请求它会把两件事搅在一起（而且失败时会分不清是页面没起来还是登录过期）。</p>
    <p>⚠️ 一样要绕过本机代理：Git Bash 常常配着 http_proxy，访问 127.0.0.1 会 502。</p>
    """
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(url, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:  # noqa: BLE001
        return 0, str(e)


def raw_put(url, body, headers=None, timeout=5):
    """裸 PUT（本机配置页保存用）。写操作要带 X-CS-Local-Token，见调用处。"""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    req = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="PUT")
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with opener.open(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:  # noqa: BLE001
        return 0, str(e)


class PageStructure(html.parser.HTMLParser):
    """
    只为一个断言服务：**「服务端」明细默认收起、收起时该露的仍露着**（2026-10-15）。

    <p>为什么要真解析：<c>"serverDetail" in page</c> 这种子串检查对两类回归全都点头 ——
    「把明细挪到卡片外面」和「hidden 属性被顺手删掉」。
    而这两件事恰恰就是这条需求的内容（收起的边界在哪、谁被收进去了）。</p>

    <p>产出：<c>attrs[id]</c> = 该元素的属性字典；<c>parents[id]</c> = 它的**祖先 id 列表**
    （只记有 id 的祖先，顺序由外到内）。</p>
    """

    VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.attrs = {}
        self.parents = {}
        self._tags = []
        self._ids = []

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        me = a.get("id")
        if me:
            self.attrs[me] = a
            self.parents[me] = [i for i in self._ids if i]

        if tag in self.VOID:
            return

        self._tags.append(tag)
        self._ids.append(me)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in self.VOID:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        # 容错：往回找最近的同名开标签。页面里没有跨层错配，但别让解析器自己抛出去
        # —— 冒烟脚本崩掉比断言 FAIL 更难查。
        for i in range(len(self._tags) - 1, -1, -1):
            if self._tags[i] == tag:
                del self._tags[i:]
                del self._ids[i:]
                return


def parse_page(page_html):
    """把本机配置页解析成 PageStructure（见上）。"""
    idx = PageStructure()
    idx.feed(page_html)
    return idx


def client_messages(state_dir):
    """
    读客户机**自己的** NDJSON 日志（Serilog 文件 sink），返回解析后的 Message 列表。

    <p>⚠️ 必须在客户端**还活着**的时候读，不要等进程被杀之后：<br>
    客户端是被 <c>terminate()</c> 杀的（Windows 上是 TerminateProcess，没有优雅停机），
    日志 sink 的尾巴有可能留在缓冲区里再也落不了盘。<br>
    第一版就是"发完报文 → 杀进程 → 再读日志"，结果第二条报文对应的那行
    根本没写进文件 —— 看起来像"这道关卡没生效"，其实只是读得太晚。</p>

    <p>⚠️ 断言必须基于这里**解析后**的 Message，不能拿中文去 grep 原始文件：
    非 ASCII 可能被转义成 <c>\\uXXXX</c>，直接 grep 会永远不命中。</p>
    """
    messages = []
    logs_dir = os.path.join(state_dir, "logs")

    if not os.path.isdir(logs_dir):
        return messages

    for name in sorted(os.listdir(logs_dir)):
        if not name.endswith(".ndjson"):
            continue
        try:
            with open(os.path.join(logs_dir, name), "rb") as fh:
                text = fh.read().decode("utf-8", errors="replace")
        except OSError:
            continue

        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except ValueError:
                continue
            messages.append(str(rec.get("Message") or ""))

    return messages


def login_admin(session):
    status, body, _ = session.call(
        "POST", "/api/auth/login",
        {"userName": SEED_ADMIN, "password": SEED_PASSWORD})
    return status == 200, body


def agents_of(body):
    """从响应里取出机器清单，兼容两种形状。

    · `GET /api/agents`          → 直接就是 `{items, total, online, …}`
    · 写操作（改名/拉黑/移除…）   → `{ok, status, message, list: {items, …}}`

    ⚠️ 写操作**刻意**把列表嵌在 `list` 里而不是摊平 —— 这样"操作结果"与
    "全量列表"各自是一个完整对象，前端一次 `result.list` 就刷新到位。
    曾因为这里只认 `items` 而把嵌入形状当成"列表空了"，误报 3 处失败。
    """
    if not body:
        return []

    if isinstance(body.get("items"), list):
        return body["items"]

    inner = body.get("list")
    if isinstance(inner, dict) and isinstance(inner.get("items"), list):
        return inner["items"]

    return []


def total_of(body):
    """取出清单条数，兼容上面两种形状（写操作时要下钻到 `list.total`）。"""
    if not body:
        return None

    if "total" in body:
        return body.get("total")

    inner = body.get("list")
    return inner.get("total") if isinstance(inner, dict) else None


def find_agent(session, agent_id):
    status, body, _ = session.call("GET", f"/api/agents/{agent_id}")
    return body if status == 200 else None


def agent_live_udp(session, agent_id):
    """
    读中心**在线表**里那台机器的 UDP 通道**实际**状态（客户机自己上报的那份）。

    <p>⚠️ 它读的是 <c>GET /api/agents/{id}</c> 的 <c>host.udpChannel</c>，
    而**不是**配置里的 <c>udp.enabled</c>。这两者的差别正是
    "中心说已发出、机器毫无反应"的根源 —— 一个说"我要它怎样"，一个说"它现在怎样"。</p>

    <p>返回 None = 还没上报过（"不知道"）。它与"它说没在听"是两件必须分开的事。</p>
    """
    detail = find_agent(session, agent_id)
    if not detail:
        return None
    return (detail.get("host") or {}).get("udpChannel")


def wait_for(predicate, timeout, interval=1.0):
    """轮询直到 predicate 返回真值；返回 (是否成功, 最后一次的值)。"""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = predicate()
        if last:
            return True, last
        time.sleep(interval)
    return False, last


# ═══════════════════════════════════════════════════════════════
# A. 安装包
# ═══════════════════════════════════════════════════════════════


def section_a(session):
    print()
    print("A. 客户端安装包")

    status, body, _ = session.call("GET", "/api/downloads/agent-installer/info")
    check("安装包信息端点可读", status == 200, f"HTTP {status}")

    if status == 200:
        check("  尚未放包时 available=false", body.get("available") is False)
        check("  且给出「该放到哪」的指引",
              INSTALLER_DIR_NAME in (body.get("hint") or ""),
              body.get("hint") or "(无 hint)")
        check("  服务端版本随响应下发", bool(body.get("serverVersion")),
              str(body.get("serverVersion")))

    # 没有包时下载必须 404 且带可读原因（前端把它显示在按钮 tooltip 上）。
    status, body, _ = session.call("GET", "/api/downloads/agent-installer")
    check("尚无安装包时下载返回 404", status == 404, f"HTTP {status}")
    check("  且 404 体里带可读原因",
          isinstance(body, dict) and bool(body.get("message")),
          str(body)[:80])

    # 放一个"安装包"进去。
    target_dir = os.path.join(DATA_DIR, INSTALLER_DIR_NAME)
    os.makedirs(target_dir, exist_ok=True)
    payload = bytes(range(256)) * 4100          # 1 049 600 字节，约 1 MB
    target = os.path.join(target_dir, INSTALLER_FILE_NAME)
    with open(target, "wb") as fh:
        fh.write(payload)

    status, body, _ = session.call("GET", "/api/downloads/agent-installer/info")
    check("放包后 available=true", status == 200 and body.get("available") is True)
    check("  文件名正确", body.get("fileName") == INSTALLER_FILE_NAME, str(body.get("fileName")))
    check("  尺寸正确", body.get("sizeBytes") == len(payload),
          f"{body.get('sizeBytes')} vs {len(payload)}")
    check("  从文件名解析出版本 0.1.0", body.get("version") == "0.1.0", str(body.get("version")))
    check("  版本与服务端一致 ⇒ 不给不一致提示", body.get("versionMismatch") is False)

    # 真下载。
    status, data, headers = session.raw("GET", "/api/downloads/agent-installer")
    check("下载返回 200", status == 200, f"HTTP {status}")
    check("  内容是完整字节流", data == payload, f"{len(data)} vs {len(payload)}")
    check("  带 Content-Disposition（浏览器据此落成 .exe）",
          "attachment" in (headers.get("Content-Disposition") or "").lower(),
          headers.get("Content-Disposition") or "(无)")
    check("  声明支持 Range（断点续传的前提）",
          "bytes" in (headers.get("Accept-Ranges") or "").lower(),
          headers.get("Accept-Ranges") or "(无)")

    # 真 Range 请求：几十 MB 的安装包"网断了一下不用重来"就靠它。
    status, chunk, headers = session.raw(
        "GET", "/api/downloads/agent-installer", {"Range": "bytes=0-9"})
    check("Range 请求返回 206", status == 206, f"HTTP {status}")
    check("  只回请求的那 10 个字节", chunk == payload[:10], f"{len(chunk)} 字节")
    check("  带 Content-Range",
          (headers.get("Content-Range") or "").startswith("bytes 0-9/"),
          headers.get("Content-Range") or "(无)")

    # 版本不一致只提示、不阻断 —— 现场常有"客户端落后一版"的过渡期。
    mismatched = os.path.join(target_dir, "ControlServer-Agent-Setup-9.9.9.exe")
    with open(mismatched, "wb") as fh:
        fh.write(b"x")

    status, body, _ = session.call("GET", "/api/downloads/agent-installer/info")
    check("存在多个包时优先取「按约定命名且最新」的那个",
          status == 200 and body.get("fileName") == mismatched.split(os.sep)[-1],
          str(body.get("fileName")))
    check("  版本不一致时 versionMismatch=true", body.get("versionMismatch") is True)
    check("  且**不禁用**下载（只是提示）", body.get("available") is True)

    os.remove(mismatched)


# ═══════════════════════════════════════════════════════════════
# B. 准入与在线表
# ═══════════════════════════════════════════════════════════════


def section_b(session):
    print()
    print("B. 免凭据准入与在线表")

    status, body, _ = session.call("GET", "/api/agents")
    check("在线表可读且为空", status == 200 and body.get("total") == 0,
          f"HTTP {status} total={(body or {}).get('total')}")

    # 未登录也能读？不行 —— 在线表是设备资产台账。
    anon = Session()
    status, _, _ = anon.call("GET", "/api/agents")
    check("匿名访问在线表被拒（401）", status == 401, f"HTTP {status}")

    # ── 免凭据注册（匿名端点，请求体里只有主机信息）──
    #    2026-10-13 起没有任何令牌 / 加入码门槛：装好客户端、能发现到服务端即上线。
    machine_key = "smoke-machine-0001"
    register_body = {
        "hostInfo": {
            "machineKey": machine_key,
            "hostName": "SMOKE-PC",
            "osDescription": "Windows 11 (smoke)",
            "cpuName": "Smoke CPU @ 1.00GHz",
            "logicalProcessors": 8,
            "totalMemoryMb": 16384,
            "agentVersion": "0.1.0",
            "userName": "smoke",
            "interfaces": [
                {"name": "以太网", "address": "192.168.1.50", "mac": "AA:BB:CC:DD:EE:FF", "isUp": True}
            ],
        },
    }

    anonymous = Session()
    status, result, _ = anonymous.call("POST", "/api/agents/register", register_body)
    check("免凭据注册成功（不需要令牌 / 加入码）",
          status == 200 and result.get("ok") is True,
          f"HTTP {status} {result.get('message')}")

    agent_key = result.get("agentKey") or ""
    agent_id = result.get("agentId")
    check("  注册应答里带 agentKey（**仅此一次**）", bool(agent_key))
    check("  agentKey 是 Base64Url（可直接进命令行 / URL）",
          len(agent_key) == 43 and not any(c in agent_key for c in "+/="),
          f"len={len(agent_key)}")
    check("  注册应答里带 agentId", isinstance(agent_id, int), str(agent_id))
    check("  注册应答里带心跳间隔（服务端下发，客户端据此定时）",
          isinstance(result.get("heartbeatSeconds"), int),
          str(result.get("heartbeatSeconds")))

    if not isinstance(agent_id, int):
        return None

    # ── 同一机器码重复注册 = "这台机器重装了"，复用同一条记录 ──
    status, again, _ = anonymous.call("POST", "/api/agents/register", register_body)
    check("同一机器码重复注册成功（没有「用一次就作废」这回事）",
          status == 200 and again.get("ok") is True, f"HTTP {status}")
    check("  复用同一条记录（agentId 不变）", again.get("agentId") == agent_id,
          f"{again.get('agentId')} vs {agent_id}")
    check("  重装后签发新的 agentKey（旧的那份作废）",
          bool(again.get("agentKey")) and again.get("agentKey") != agent_key)

    # ── 缺机器标识必须 400（免凭据后唯一剩下的内容校验）──
    status, _, _ = anonymous.call("POST", "/api/agents/register", {"hostInfo": None})
    check("缺机器标识注册被拒（400）", status == 400, f"HTTP {status}")

    # ── 在线表 ──
    status, body, _ = session.call("GET", "/api/agents")
    items = agents_of(body)
    check("注册后出现在在线表里，且重复注册被复用（仍只有一台）",
          len(items) == 1, f"{len(items)} 台")

    if items:
        host = items[0]
        check("  状态是「等待连接」（注册过、但还没建立过长连接）",
              host.get("state") == "never",
              f"{host.get('state')} / {host.get('stateDisplay')}")
        check("  机器码正确", host.get("machineKey") == machine_key)
        check("  主机信息（CPU / 内存）已带上",
              host.get("cpuName") == "Smoke CPU @ 1.00GHz"
              and host.get("totalMemoryMb") == 16384,
              f"{host.get('cpuName')} / {host.get('totalMemoryMb')}")

    # ── 详情 ──
    detail = find_agent(session, agent_id)
    check("详情端点可读", detail is not None and detail.get("host", {}).get("id") == agent_id)
    if detail:
        check("  详情里带网卡清单",
              len((detail.get("info") or {}).get("interfaces") or []) == 1)
        check("  此刻未持有长连接", detail.get("connected") is False)

    # ── 离线时下发重启必须 409（请求合法但"现在做不到"）──
    status, body, _ = session.call("POST", f"/api/agents/{agent_id}/reboot")
    check("客户端离线时下发重启返回 409", status == 409, f"HTTP {status}")

    # ── 改名 ──
    status, body, _ = session.call("PUT", f"/api/agents/{agent_id}", {
        "displayName": "冒烟改名机", "group": "冒烟组", "note": "自动测试产生"})
    check("改名成功", status == 200 and body.get("ok") is True, f"HTTP {status}")
    renamed = next((h for h in agents_of(body) if h.get("id") == agent_id), None)
    check("  改名后列表里立刻可见（写操作会立刻刷快照）",
          renamed is not None and renamed.get("displayName") == "冒烟改名机",
          str(renamed and renamed.get("displayName")))

    # ── 拉黑 / 取消拉黑（2026-10-15 取代原「吊销」）──
    #
    # 🔥 与旧「吊销」的关键差别在这一段之外（H 段与服务端用例里）：拉黑**连注册都拒绝**，
    #    所以客户机没法靠重新注册把自己救回来。这里只验服务端这一侧的可见状态。
    status, body, _ = session.call("POST", f"/api/agents/{agent_id}/block")
    check("拉黑成功", status == 200 and body.get("ok") is True, f"HTTP {status}")
    blocked = next((h for h in agents_of(body) if h.get("id") == agent_id), None)
    check("  拉黑后状态为 blocked（压过一切时间判据）",
          blocked is not None and blocked.get("state") == "blocked"
          and blocked.get("isBlocked") is True,
          str(blocked and blocked.get("state")))

    status, body, _ = session.call("POST", f"/api/agents/{agent_id}/unblock")
    check("取消拉黑成功", status == 200 and body.get("ok") is True, f"HTTP {status}")
    check("  取消后不再是 blocked 状态",
          all(h.get("state") != "blocked" for h in agents_of(body)))

    # ── 「记为此机默认目录」（2026-10-14，需求⑥）──
    #
    # 🔥 它补的是"**挑对了目录、但这次先不建单**"那个缺口：在此之前
    #    `LastTargetDir` / `LastSourceDir` **只由建单流程回写**
    #    （`DistributionsController` 里那次 `SetLastPathAsync`），
    #    于是"在资源管理器里看图挑目录"这个动作本身什么都没留下 ——
    #    下次打开还是得从盘符列表一层层点进去，而那正是这套界面最烦的一步。
    #
    # ⚠️ 语义三分必须逐条钉住（库里用的是 `CASE WHEN @p IS NULL THEN 旧值 ELSE @p END`）：
    #    传 null = **这一项不动**；传空串 = 退回"用那台机器的默认目录"。
    #    把两者混成一条，"清空输入框"就会变成"忘掉记住的路径"，而用户的本意恰恰相反。
    def _remembered_paths(host_id):
        _st, _bd, _ = session.call("GET", "/api/agents")
        _host = next((h for h in agents_of(_bd) if h.get("id") == host_id), None) or {}
        return _host.get("lastTargetDir"), _host.get("lastSourceDir")

    status, body, _ = session.call(
        "PUT", f"/api/agents/{agent_id}/path-memory", {"targetDir": r"D:\展厅\素材"})
    check("记住「下发落脚目录」成功", status == 200 and body.get("ok") is True, f"HTTP {status}")

    tgt, src = _remembered_paths(agent_id)
    check("  下发目录已落库，且列表里立刻可见（不等下一次快照刷新）",
          tgt == r"D:\展厅\素材", str(tgt))
    check("  没传的那一项**保持不动**（null 是「不动」，不是「清空」）", src is None, str(src))

    status, _, _ = session.call(
        "PUT", f"/api/agents/{agent_id}/path-memory", {"sourceDir": r"D:\现场日志"})
    tgt, src = _remembered_paths(agent_id)
    check("记住「回传读取目录」成功", status == 200 and src == r"D:\现场日志", f"HTTP {status}")
    check("  这次没传下发目录 ⇒ 上一项原样留着（没被 null 冲掉）",
          tgt == r"D:\展厅\素材", str(tgt))

    status, body, _ = session.call("PUT", f"/api/agents/{agent_id}/path-memory", {})
    check("两项都不给被拒（400）—— 空请求不算「清空两项」",
          status == 400, f"HTTP {status} {str(body.get('message'))[:80]}")

    status, _, _ = anon.call(
        "PUT", f"/api/agents/{agent_id}/path-memory", {"targetDir": r"D:\x"})
    check("匿名记住路径被拒（401）", status == 401, f"HTTP {status}")

    # 还原：别把测试路径留在库里，后面的场景会读它当预填值。
    session.call("PUT", f"/api/agents/{agent_id}/path-memory",
                 {"targetDir": "", "sourceDir": ""})

    # ── 已移除的令牌端点必须真的没了（否则"全拆"只拆了一半）──
    #    ⚠️ **不能按状态码判**：未匹配的路径会被 SPA 回退（Program.cs 的
    #    MapFallbackToFile("index.html")）用 index.html 以 200 应答，
    #    所以 404 永远不会出现。判据改成"返回的不再是令牌清单"
    #    —— SPA 回退给的是 HTML 字符串，而令牌清单是带 tokens 的 JSON 对象。
    status, body, _ = anon.call("GET", "/api/agents/enrollment")
    check("令牌端点已整体移除（不再是令牌清单）",
          not (status == 200 and isinstance(body, dict) and "tokens" in body),
          f"HTTP {status} body={str(body)[:60]}")

    return agent_id


# ═══════════════════════════════════════════════════════════════
# C. 发现协议（真 UDP）
# ═══════════════════════════════════════════════════════════════


def section_c():
    print()
    print("C. UDP 发现协议")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(5)

    try:
        sock.sendto(b"CS-DISCOVER smoke-machine 0.1.0", ("127.0.0.1", DISCOVERY_PORT))

        try:
            payload, _ = sock.recvfrom(2048)
        except socket.timeout:
            check("服务端应答 CS-DISCOVER", False, f"{DISCOVERY_PORT} 上 5 秒内无应答")
            return

        text = payload.decode("utf-8", errors="replace")
        parts = text.split(" ")

        check("服务端应答 CS-DISCOVER", True, text)
        check("  首字段是 CS-HERE", parts[0] == "CS-HERE", parts[0])
        check("  字段数为 7（客户端按固定下标取值）", len(parts) == 7, f"{len(parts)} 个")
        check("  产品标识正确", len(parts) > 1 and parts[1] == "ControlServer", parts[1])

        http_port = BASE.rsplit(":", 1)[-1]

        if len(parts) == 7:
            check("  回传的 HTTP 端口与本次实例一致", parts[3] == http_port,
                  f"{parts[3]} vs {http_port}")
            check("  回传的发现端口与本次实例一致", parts[5] == str(DISCOVERY_PORT),
                  f"{parts[5]} vs {DISCOVERY_PORT}")
            check("  不含任何凭据（应答对整个广播域可见）",
                  not any(w in text.lower() for w in ("token", "secret", "password", "key")),
                  text)
    finally:
        sock.close()

    # 乱码不该让监听退出 —— 随后再发一条正常的，必须还能收到应答。
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(5)

    try:
        sock.sendto(b"\x00\x01\x02 garbage", ("127.0.0.1", DISCOVERY_PORT))
        sock.sendto(b"CS-HELLO wrong keyword", ("127.0.0.1", DISCOVERY_PORT))
        sock.sendto(b"CS-DISCOVER smoke-after-garbage 0.1.0", ("127.0.0.1", DISCOVERY_PORT))

        got = False
        for _ in range(3):
            payload, _ = sock.recvfrom(2048)
            if payload.decode("utf-8", "replace").startswith("CS-HERE"):
                got = True
                break

        check("乱码报文之后监听仍在工作", got)
    except socket.timeout:
        check("乱码报文之后监听仍在工作", False, "超时")
    finally:
        sock.close()

    # 超长报文应被丢弃（广播端口对整个局域网开放）。
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(2)

    try:
        sock.sendto(b"CS-DISCOVER " + b"x" * 1000 + b" 0.1.0", ("127.0.0.1", DISCOVERY_PORT))

        try:
            payload, _ = sock.recvfrom(2048)
            check("超长报文被丢弃（不应答）", False, f"却收到了 {len(payload)} 字节")
        except socket.timeout:
            check("超长报文被丢弃（不应答）", True)
    finally:
        sock.close()


# ═══════════════════════════════════════════════════════════════
# D. 真客户端端到端
# ═══════════════════════════════════════════════════════════════


def section_d(session):
    print()
    print("D. 真客户端端到端（控制台形态的 Agent 服务）")

    if not os.path.isfile(AGENT_EXE):
        skip("启动真客户端",
             f"找不到 {os.path.relpath(AGENT_EXE, REPO)}（先 dotnet build 再跑冒烟）")
        return

    # 2026-10-13 起注册免凭据：客户端不需要任何令牌 / 加入码，
    # 只要 --Agent:ServerUrl 指对（或能广播发现到）就会自动注册上线。
    state_dir = os.path.join(tempfile.gettempdir(), f"cs-agent-smoke-{os.getpid()}")
    shutil.rmtree(state_dir, ignore_errors=True)

    log_path = os.path.join(tempfile.gettempdir(), f"cs-agent-smoke-{os.getpid()}.log")
    log = open(log_path, "wb")

    env = dict(os.environ)
    # apphost 会回退到 Program Files 找运行时，本机没有 —— 必须指到托管 SDK 目录。
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")

    argv = [
        AGENT_EXE,
        f"--Agent:ServerUrl={BASE}",
        f"--Agent:StateDirectory={state_dir}",
        "--Agent:ServiceName=ControlServerAgentSmoke",
        # 单实例互斥是给"本机只跑一个服务"用的；冒烟可能与真的客户端共存，关掉更稳。
        "--Agent:AllowBroadcastDiscovery=false",
        # 🔥 UDP 指令通道走**演练模式**：报文解析照常走完（那才是要验的东西），
        #    只是最后一步不真的动手。冒烟脚本**绝不允许把跑它的这台机器关掉**，
        #    而这个开关是唯一的保险 —— 少了它，冒烟里发的那条明文 DOWN
        #    会把跑测试的这台机器直接关掉。
        "--Agent:Tasks:ExecuteUdpCommands=false",
    ]

    print(f"  客户端： {os.path.relpath(AGENT_EXE, REPO)}")
    print(f"  状态目录：{state_dir}")

    proc = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, cwd=REPO, env=env)

    try:
        # ── ① 它应当自己注册并出现在在线表里（同一台机器也算一台新设备）──
        def find_new():
            status, body, _ = session.call("GET", "/api/agents")
            if status != 200:
                return None
            for host in agents_of(body):
                if host.get("machineKey") != "smoke-machine-0001":
                    return host
            return None

        found, host = wait_for(find_new, timeout=45, interval=1.5)

        check("客户端自动注册并出现在在线表里（≤45s）", found,
              "未出现；客户端日志：%s" % log_path)

        if not found:
            return

        new_id = host.get("id")

        # ── ② 它应当握上长连接（这才是"在线"的确证，比看状态字符串更硬）──
        def is_connected():
            detail = find_agent(session, new_id)
            return bool(detail and detail.get("connected"))

        connected, _ = wait_for(is_connected, timeout=30, interval=1.0)
        check("客户端握上长连接（≤30s）", connected)

        if connected:
            detail = find_agent(session, new_id)
            check("  心跳把它标记成在线/不稳定",
                  (detail or {}).get("host", {}).get("state") in ("online", "unstable"),
                  str((detail or {}).get("host", {}).get("state")))
            check("  上报了主机名与操作系统",
                  bool((detail or {}).get("info", {}).get("hostName")),
                  str((detail or {}).get("info", {}).get("hostName")))
            check("  上报了客户端版本",
                  bool((detail or {}).get("host", {}).get("agentVersion")),
                  str((detail or {}).get("host", {}).get("agentVersion")))
            check("  心跳里带上了来源地址",
                  bool((detail or {}).get("host", {}).get("lastAddress")),
                  str((detail or {}).get("host", {}).get("lastAddress")))

            # ── ③ 遥测（M4.2）：时序明细 / 硬件清单 / 开关机记录 ──
            #
            # 三条链路各有各的失败形态，必须分开断言：
            #   · 指标走"内存缓冲 + 异步落盘" ⇒ 摄入成功不等于库里有，要轮询等
            #   · 清单与开关机是同步写 ⇒ 能读到就说明落盘了
            #   · 开关机记录是**读 Windows 事件日志**得到的，与前三项完全不同源，
            #     它坏掉不会影响 CPU 曲线（当初就是这里先炸的）
            print("  → 等待遥测落库（指标 / 硬件清单 / 开关机）…")

            def telemetry_ready():
                st, body, _ = session.call("GET", f"/api/agents/{new_id}/metrics")
                if st != 200:
                    return None
                # ⚠️ 门槛是 **4 条**而不是 1 条，别调回去。
                #    CPU / 磁盘 / 网络都是**差分计数器**：第一帧没有"上一帧"可比，值必然是 null。
                #    只等 1 条就判"就绪"会得到一个 CPU 全 null 的图，
                #    然后失败在"至少一个点带 CPU 值"上 —— 看起来像采集坏了，其实只是等得不够。
                if (body.get("rawCount") or 0) < 4:
                    return None
                if len(body.get("points") or []) < 1:
                    return None
                return body

            ready, series = wait_for(telemetry_ready, timeout=60, interval=2.0)

            check("时序指标已落库（≤60s）", ready, "客户端日志：%s" % log_path)

            if ready:
                pts = series.get("points") or []
                ladder = (5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400)

                check("  分桶粒度取自固定梯级", series.get("bucketSeconds") in ladder,
                      str(series.get("bucketSeconds")))
                check("  短窗口走明细路径", series.get("source") == "raw",
                      str(series.get("source")))
                # 前端 fmtDateTime 是**截 ISO 字符串**显示的；回一个 ...Z 会让横轴少 8 小时。
                check("  回显窗口已归一化到 +08:00",
                      str(series.get("from", "")).endswith("+08:00")
                      and str(series.get("to", "")).endswith("+08:00"),
                      f"{series.get('from')} ~ {series.get('to')}")

                cpus = [p.get("cpuPct") for p in pts if p.get("cpuPct") is not None]
                check("  至少一个点带 CPU 值", len(cpus) > 0, f"{len(cpus)}/{len(pts)}")
                check("  CPU 值在 0-100", all(0 <= c <= 100 for c in cpus), str(cpus[:4]))
                mems = [p.get("memPct") for p in pts if p.get("memPct") is not None]
                check("  至少一个点带内存占用率", len(mems) > 0, str(mems[:3]))
                check("  无负数的速率",
                      not [p for p in pts
                           if p.get("diskReadBps") is not None and p.get("diskReadBps") < 0])

                # ── 逐设备读数（M4.8，#234）：真机上这条链**有五层** ──
                #    采集（PDH 逐实例）→ 命名（清单补型号）→ 上报 → 落库（DevicesJson）→ 聚合（按 Key 分桶）。
                #    五层里任何一层断了，前端表现都只是"硬件选择器空着" —— 静悄悄，没有任何报错。
                #    而这台机器上至少有一块物理磁盘和一块网卡，所以"一条都没有"必定是坏消息。
                device_points = [p for p in pts if p.get("devices")]
                check("  逐设备读数已随明细落库（不是 null）", len(device_points) > 0,
                      f"{len(device_points)}/{len(pts)} 个点带 devices")

                if device_points:
                    devs = device_points[-1].get("devices") or []
                    kinds = sorted({d.get("kind") for d in devs})

                    check("  至少报了磁盘与网卡（真机必有的两类）",
                          "disk" in kinds and "net" in kinds, f"kinds={kinds}")

                    # 🔥 Key 是曲线接续的唯一依据：缺了它，前端只能拿型号名当身份，
                    #    而改一次名就会把曲线断成两截。
                    check("  每条设备都有稳定 Key", all(d.get("key") for d in devs),
                          str([d.get("key") for d in devs])[:120])

                    # 每种设备只填自己那一两个字段，其余必须保持 null —— 不许串味，更不许拿 0 冒充。
                    bad = [d for d in devs
                           if d.get("kind") == "net"
                           and (d.get("cpuPct") is not None or d.get("readBps") is not None)]
                    check("  非本种类的字段保持 null（不串味）", not bad, str(bad)[:120])

                    # ── 显卡型号（需求②，2026-10-14）──
                    #    过去这里是 `GPU 适配器 1 / 2`：PDH 的 GPU Engine 只给 LUID，
                    #    型号要靠 DXGI / D3DKMT 按 LUID 去问。占位名在**多卡机器**上恰恰最误导 ——
                    #    两块卡的占用率曲线长得很像，"适配器 1 / 2"没有任何办法对上是哪块。
                    gpus = [d for d in devs if d.get("kind") == "gpu"]

                    if gpus:
                        placeholder = [d.get("name") for d in gpus
                                       if str(d.get("name") or "").startswith("GPU 适配器")]
                        check("  显卡用的是具体型号（不是「GPU 适配器 N」占位名）",
                              not placeholder,
                              f"占位的：{placeholder}")
                        print(f"     （显卡：{', '.join(str(d.get('name')) for d in gpus)}）")

                        # 软件光栅器（WARP / Microsoft Basic Render Driver）不是显卡：
                        # 它占用率恒为 0，混进硬件清单会让人以为这台机器装了三块显卡。
                        warp = [d.get("name") for d in gpus
                                if "Basic Render Driver" in str(d.get("name") or "")]
                        check("  软件光栅器没被当成显卡", not warp, str(warp)[:120])

                    print(f"     （{len(devs)} 块硬件：{', '.join(kinds)}）")

            # 硬件清单（同步写：读到 capturedAt 就是已经落盘）
            def inventory_ready():
                st, body, _ = session.call("GET", f"/api/agents/{new_id}/inventory")
                return body if (st == 200 and body.get("capturedAt")) else None

            inv_ok, inv_env = wait_for(inventory_ready, timeout=60, interval=2.0)
            check("硬件清单已上报（≤60s）", inv_ok)

            if inv_ok:
                inv = inv_env.get("inventory") or {}
                check("  清单里有 CPU 名", bool((inv.get("cpu") or {}).get("name")),
                      str((inv.get("cpu") or {}).get("name"))[:44])
                check("  清单里有内存总量", bool(inv.get("totalMemoryMb")),
                      str(inv.get("totalMemoryMb")))
                check("  清单里有分区", len(inv.get("volumes") or []) > 0)
                # 客户端**自己声明**本平台支不支持开关机记录，服务端不猜 ——
                # 页面上据此显示"当前平台不支持"而不是一张让人误会的空表。
                check("  客户端声明了本平台支持开关机记录",
                      inv.get("powerEventsSupported") is True)

                # ── 硬件温度（M4.3）：默认值在**安装侧**，不在这条链路里（需求①）──
                #
                # 🔥 这里原先写的是「默认启用温度监控（不是 disabled）」——
                #    它把需求①读成了"客户端不带配置也该默认开"，**那是错的**，是一条**假 FAIL**：
                #      · 需求①要的是**安装向导那个勾选框默认勾上**；
                #      · 安装脚本会把 `Agent:Telemetry:ThermalEnabled=true` **显式**写进
                #        appsettings.json（由 **F 段的静态检查**钉住，那才是需求①的落点）；
                #      · 本冒烟是**裸起 exe**（没有 appsettings.json）⇒ 键缺失 ⇒ 关。
                #    于是它在"代码完全正确"时照样红 —— 假 FAIL 比没有断言更坏：
                #    它会把下一轮的人骗去改一段本来就是对的代码。
                #
                # ⚠️ 而"配置缺失 = 关"是**故意**的（方案 §22「默认不装驱动」）：
                #    一个会释放并加载内核级驱动（WinRing0）的能力，不该"没人告诉它就自己开"。
                #
                # 所以这里只钉两条**各自都真**的不变量：
                #   · 状态词只能是 ok / disabled / failed（不许冒出第四种）；
                #   · 拿不到温度时**必须给原因**，且温度字段是 null 而不是 0
                #     （0 ℃ 会被读成"机器很凉快"，那是假数据）。
                thermal = inv.get("thermalState")
                check("  温度状态取值合法（ok/disabled/failed）",
                      thermal in ("ok", "disabled", "failed"), str(thermal))
                # 裸起（无 appsettings.json）⇒ 未启用。这是**事实**，不是缺陷。
                # ⚠️ 不要写成 `thermal == "ok"`：装上驱动之后真机确实会变 ok，两者都对；
                #    这条要钉的是"没人告诉它，它就不该自己开"。
                check("  裸起（无 appsettings.json）时如实报「未启用」，不偷偷自己加载驱动",
                      thermal == "disabled", str(thermal))
                check("  没有温度时说明「为什么没有温度」（而不是留空让人猜）",
                      thermal == "ok" or bool(inv.get("thermalNote")),
                      str(inv.get("thermalNote")))

                if thermal == "disabled":
                    # 🔥 只是"有一句话"不够 —— 那句话还得**指对方向**。
                    #
                    # 这里跑的是**裸起 exe**：没装过、也没有 appsettings.json
                    # ⇒ 正确的原因是"配置里没有这一项"。
                    #
                    # ⚠️ 而旧文案写死的是「安装时未启用硬件温度监控。」：
                    #    它非空、没有错别字、看着还挺像人话 —— 于是上面那条
                    #    "有原因吗"的断言**照样通过**，可它会把人支去翻一个
                    #    从没用过的安装包。含糊的一句话，把断言骗过去了。
                    check("  而且指对了方向：说的是「配置里没有这一项」，不是甩给安装向导",
                          "配置里没有" in (inv.get("thermalNote") or ""),
                          str(inv.get("thermalNote")))

                if ready and thermal != "ok":
                    temps = [p.get(k)
                             for p in (series.get("points") or [])
                             for k in ("cpuTempC", "gpuTempC", "storageTempC")
                             if p.get(k) is not None]
                    check("  拿不到温度时温度字段全为 null（不是 0）", not temps, str(temps[:4]))

            # 在线表带不带"最新一帧"（热层）—— 列表页的「实时负载」列靠它
            st, list_body, _ = session.call("GET", "/api/agents")
            frame = {}
            if st == 200:
                me = [h for h in agents_of(list_body) if h.get("id") == new_id]
                frame = ((me[0].get("metrics") if me else None) or {})

            check("在线表带「最新一帧」负载摘要（不等落盘）", bool(frame.get("at")),
                  str(frame.get("cpuPct")))

            # 摘要里**必须**带逐设备读数（M4.8 修，2026-10-13）。
            # 详情页的曲线是"打开时拉一段历史，之后每来一帧摘要往前挪一格"——
            # 摘要不带逐设备的话，挪的只有整机那几条线，硬件选择器上的「当前值」
            # 与硬件曲线会永远停在打开页面那一刻。
            frame_devs = frame.get("devices")
            check("在线表的摘要带逐设备读数（详情页硬件曲线靠它往前挪）",
                  isinstance(frame_devs, list) and len(frame_devs) > 0,
                  "null/空 = 详情页的硬件曲线不会动" if not frame_devs
                  else f"{len(frame_devs)} 块")

            # ── ④.4 中心下发的「关机 / 重启」指令（2026-10-15 修的那个 BUG）──
            #
            # 🔥 症状：中心页面上点关机 / 重启**毫无效果**，而 UDP 明文指令一直正常。
            #    根因：客户端收到中心的 `power` 指令后**没有任何人处理** ——
            #    主循环只认 refresh-config，其余转交宿主，宿主的 switch 里没有
            #    power / volume ⇒ 落到 default ⇒「收到未知指令（已忽略）」。
            #    现象就是"中心说已下发、机器毫无反应"，而唯一的证据躺在客户机日志里。
            #
            # ⚠️ **绝不真关机**：这台客户机跑在演练模式（ExecuteUdpCommands=false），
            #    所以这里断言的是"这条指令**被认出来并走到了执行器**" ——
            #    证据就是客户端日志里那句"演练模式已开启…不执行"，
            #    而**不是**修复前的"收到未知指令：power（已忽略）"。
            #
            # ⚠️ 断言顺序有讲究：这段必须排在下面"真发 UDP 报文"那一段**之前** ——
            #    UDP 那条路也会记同一句"演练模式已开启"，排在其后就分不出
            #    到底是中心的指令走到了执行器，还是 UDP 的功劳（那是条假 PASS）。
            st, pw_body, _ = session.call("POST", f"/api/agents/{new_id}/power", {"action": "shutdown"})
            check("中心下发「关机」指令成功（客户端在线）",
                  st == 200 and pw_body.get("ok") is True, f"HTTP {st} / {str(pw_body)[:120]}")

            check("  返回语说清了「已下发 ≠ 已执行」（不再只回一句已下发）",
                  "接收到的数据" in str(pw_body.get("message") or ""),
                  str(pw_body.get("message"))[:160])

            got_it, _ = wait_for(
                lambda: any("收到服务端指令：power" in m for m in client_messages(state_dir)),
                timeout=15, interval=1.0)
            check("  客户端经 SignalR 收到了这条指令", got_it,
                  f"客户端日志目录：{os.path.join(state_dir, 'logs')}")

            reached, _ = wait_for(
                lambda: any("演练模式已开启" in m for m in client_messages(state_dir)),
                timeout=15, interval=1.0)
            check("  指令真的走到了本机执行器（演练模式拦下并留痕，而不是「未知指令已忽略」）",
                  reached, f"客户端日志目录：{os.path.join(state_dir, 'logs')}")

            unknown = [m for m in client_messages(state_dir) if "收到未知指令" in m]
            check("  日志里不再出现「收到未知指令」（那正是修复前的症状）",
                  not unknown, str(unknown[:2]))

            if frame_devs:
                # 🔥 光"有这条记录"不够：速率类计数器要两次采样才有值，
                #    而**首帧**的 cpuPct / readBps / gpuPct 本来就是 null。
                #    这里要的是"至少有一项真的取到了数"，否则页面上是「读 — / 写 —」这种
                #    看着像功能坏了的空值（第一轮 UI 验证就是被这个假通过放过去的）。
                numeric = [d for d in frame_devs
                           if any(isinstance(d.get(k), (int, float))
                                  for k in ("cpuPct", "readBps", "writeBps", "upBps", "downBps", "gpuPct"))]

                check("  至少一块硬件真的取到了读数（不是一片 —）", len(numeric) > 0,
                      f"{len(numeric)}/{len(frame_devs)} 块有数")

            # 开关机 / 异常记录
            st, pw, _ = session.call("GET", f"/api/agents/{new_id}/power-events?limit=200")
            check("开关机记录端点可读", st == 200, f"HTTP {st}")

            if st == 200:
                check("  统计块随列表一起下发", isinstance(pw.get("stats"), dict))
                check("  统计里有平均运行时长（或明确为 null，而不是 0 冒充）",
                      "averageUptimeSeconds" in (pw.get("stats") or {}))

                kinds = set(i.get("kind") for i in (pw.get("items") or []))
                check("  事件种类合法",
                      kinds.issubset({"boot", "shutdown", "restart", "unexpected", "bsod", "sleep", "wake"}),
                      str(kinds))

                # ⚠️ 回归守卫：详情列曾经把 byte[] 属性渲染成字面量 "System.Byte[]"，
                #    还把 Kernel-Power 41 的一串裸数字当详情（19 条里一半不可读）。
                #    这一列的**唯一用途**就是与事件查看器逐条核对，出现这类噪音即视为回归。
                details = [i.get("detail") or "" for i in (pw.get("items") or [])]
                leaked = [d for d in details if "System.Byte[]" in d]
                check("  详情里没有 System.Byte[] 类型名泄漏", not leaked,
                      (leaked[:1] or [""])[0][:60])

                noisy = [d for d in details if d.strip() and re.fullmatch(r"[\d\s|.\-]+", d.strip())]
                check("  详情里没有纯数字转储", not noisy, (noisy[:1] or [""])[0][:60])

                if details:
                    print("     详情样例：%s" % details[0][:80])

            # ── ④ 下发重启指令：在线时应是 200 ──
            status, body, _ = session.call("POST", f"/api/agents/{new_id}/reboot")
            check("在线时下发重启指令返回 200", status == 200 and body.get("ok") is True,
                  f"HTTP {status} {body.get('message')}")

        # ── ④.5 UDP 指令通道（M4.5）：服务端下发配置 → 客户机真的把端口绑起来 ──
        #
        # 🔥 2026-10-13 起线路协议是**纯明文**（DOWN / RESTART / + / - / 0 / 1），
        #    "白名单"翻成了**黑名单**，HMAC 验签与防重放整条链路都删了。
        #    于是冒烟终于能验**正向路径**了（以前拿不到只存在于客户机本地的 agentKey，签不出合法报文）：
        #      ① 配置下发后客户机**真的在监听**那个端口（in-process 单测覆盖不到这段接线）；
        #      ② 明文 DOWN 发过去 → 被**认出来**（日志「收到指令：DOWN」），
        #         但演练模式（ExecuteUdpCommands=false）挡住执行 —— 机器**不会**被关掉；
        #      ③ 把来源 127.0.0.1 填进黑名单 → 同一句报文被丢弃。
        #    "真执行关机/音量"那一段只有 UdpCommandListenerTests.cs 敢覆盖
        #    （它用**真 UDP socket + 桩电源控制器**，绝不真动手）。
        if connected:
            udp_port = free_udp_port()
            udp_port2 = free_udp_port()
            while udp_port2 == udp_port:
                udp_port2 = free_udp_port()

            print(f"  → 下发 UDP 指令通道配置（端口 {udp_port}），等客户机绑端口…")

            # 黑名单先留空：这正是"任意来源皆可"的默认形态。
            session.call("PUT", f"/api/agents/{new_id}/config",
                         {"udp": {"enabled": True, "port": udp_port,
                                  "blockedSourceIps": []}})

            udp_bound, _ = wait_for(lambda: udp_listening(udp_port, proc.pid), timeout=30, interval=1.0)

            check("配置下发后客户机真的绑定了 UDP 指令端口（≤30s）", udp_bound,
                  f"端口 {udp_port}；日志目录：{os.path.join(state_dir, 'logs')}")

            if udp_bound:
                # 顺手把"配置真的下到了监听器"这一句也在这里收掉：
                # 它是客户端启动通道时写的，此刻必定已经落盘。
                started, _ = wait_for(
                    lambda: any("UDP 指令通道已启动" in m for m in client_messages(state_dir)),
                    timeout=10, interval=1.0)

                check("客户端日志里写明 UDP 指令通道已启动", started,
                      f"日志目录：{os.path.join(state_dir, 'logs')}")

                # ② 正向路径（明文）：一条 `DOWN` 必须被**认出来**。
                #
                # ⚠️ 证据在**客户机自己的日志**里，而且必须在它**还活着**的时候读
                #    （见 client_messages 的注释：被杀掉的进程可能留不住日志尾巴）。
                #    🔥 这一条正是"指令无效果"那个现场 Bug 的回归守卫：
                #    协议还是五段式签名时，明文 DOWN 会在"报文段数不对"那关被静默丢弃。
                send_udp(udp_port, "DOWN")

                recognized, _ = wait_for(
                    lambda: any("收到指令：DOWN" in m for m in client_messages(state_dir)),
                    timeout=20, interval=1.0)

                check("明文 DOWN 被识别为合法指令（不再要求签名）", recognized,
                      f"端口 {udp_port}；日志目录：{os.path.join(state_dir, 'logs')}")

                if recognized:
                    # 演练模式必须挡住执行 —— 否则这条断言通过就意味着跑冒烟的机器刚刚关机了。
                    dry, _ = wait_for(
                        lambda: any("演练模式已开启" in m for m in client_messages(state_dir)),
                        timeout=10, interval=1.0)
                    check("  演练模式挡住了执行（机器没有被关掉）", dry,
                          f"日志目录：{os.path.join(state_dir, 'logs')}")

                # ⚠️ 丢弃日志**按时间节流**（同类最多 5 秒一条）。两次"应当被丢弃"的发送
                #    之间必须隔开，否则第二条被压掉，断言会红得莫名其妙。
                time.sleep(6)

                # ③ 黑名单：把本机来源填进去 → 同一句报文必须被丢弃。
                #
                # 🔥 这里**换一个端口**，不是"改黑名单后睡几秒再发"。
                #    原因是"黑名单改没改到客户机上"在外部**看不出来** ——
                #    睡多久都是猜；猜短了第二条报文仍可能照常执行，断言会红得莫名其妙。
                #    换端口之后 netstat 就能告诉我们答案：新端口绑上了
                #    = 这份配置（含新黑名单）确实已经生效。
                session.call("PUT", f"/api/agents/{new_id}/config",
                             {"udp": {"enabled": True, "port": udp_port2,
                                      "blockedSourceIps": ["127.0.0.1"]}})

                applied, _ = wait_for(lambda: udp_listening(udp_port2, proc.pid), timeout=30, interval=1.0)

                check("改端口 / 黑名单后客户机按新配置重新绑定（≤30s）", applied,
                      f"端口 {udp_port2}；日志目录：{os.path.join(state_dir, 'logs')}")

                if applied:
                    send_udp(udp_port2, "DOWN")

                    blocked, _ = wait_for(
                        lambda: any("来源 IP 在黑名单内" in m for m in client_messages(state_dir)),
                        timeout=20, interval=1.0)

                    # ⚠️ 诊断文本别用 "黑名单" 做过滤：启动那行就写明「来源黑名单 N 条」，
                    #    于是失败时打印出来的是一句毫不相干的启动日志（旧版踩过同类坑）。
                    dropped = [m for m in client_messages(state_dir) if "丢弃来自" in m]

                    check("黑名单来源的报文被丢弃（第 2 关）", blocked,
                          str(dropped[-1] if dropped else "（日志里一条丢弃记录都没有）")[:90])

                # ── ④.55 中心看到的"实际监听状态"（2026-10-14，需求⑤）──
                #
                # 需求原文："服务端与客户端的监听状态需要能够实时同步 ——
                #           之前指令不生效主要就是因为状态不同步导致判断失误"。
                # 这一段就是那条需求的验收：中心不再靠"我下发过的配置"去猜，
                # 而是读**客户机自己报上来的**状态，而它必须与 netstat 看到的真相一致。
                def _live_matches():
                    state = agent_live_udp(session, new_id)
                    return bool(state) and state.get("listening") is True and state.get("port") == udp_port2

                live_ok, _ = wait_for(_live_matches, timeout=30, interval=1.0)

                state_now = agent_live_udp(session, new_id) or {}
                check("中心在线表里的 UDP 实际状态与客户机真实监听一致（≤30s）", live_ok,
                      f"中心：listening={state_now.get('listening')} port={state_now.get('port')}"
                      f"；netstat 期望端口 {udp_port2}")

                # ── ④.6 「接收到的数据」端点（2026-10-13，#233）──
                #
                # 这一段守的是需求③那条链路的**最后一段**：
                #   客户机留痕 → Hub 应答 → 服务端 AgentReceivedDataService → HTTP 端点。
                # 上面那些断言全部只看**客户机自己的日志**，看不到"这份记录到底有没有被中心读到"。
                # 而这条链上任何一环断了，表现都是"页面上空空如也" —— 与"机器真的没收到"长得一模一样，
                # 正是需求③想消灭的那种歧义。
                #
                # ⚠️ 必须在**关掉通道之前**读（下一段就把它关了）：关掉之后 udpListening 就是 False，
                #    而"通道此刻到底开没开"正是这个端点最有价值的一句话 ——
                #    它一眼分清"服务端发的指令有没有到达这台机器"。
                recv_status, recv, _ = session.call("GET", f"/api/agents/{new_id}/received")

                check("GET /api/agents/{id}/received 可达（客户机在线时应答）",
                      recv_status == 200, f"HTTP {recv_status}")

                if recv_status == 200 and isinstance(recv, dict):
                    check("  客户机回报 UDP 通道正在监听", recv.get("udpListening") is True,
                          str(recv.get("udpStateText"))[:90])
                    check("  回报的端口与刚下发的配置一致",
                          recv.get("udpPort") == udp_port2,
                          f"期望 {udp_port2}，得到 {recv.get('udpPort')}")

                    entries = recv.get("entries") or []
                    outcomes = [e.get("outcome") for e in entries]

                    # 演练模式挡下的那条 DOWN：**认出了、但没执行** —— 与"根本没收到"是两回事。
                    check("  留痕里有「认出了但没执行」（演练模式）", "ignored" in outcomes,
                          f"outcomes={outcomes}")

                    # 黑名单挡下的那条：**收到了、但被本机拒绝** —— 与"没收到"也是两回事。
                    check("  留痕里有「被本机黑名单拒绝」", "blocked" in outcomes,
                          f"outcomes={outcomes}")

                    # 原文必须原样保留：这正是"报文多一个空格 / 段数不对导致认不出"唯一能查的线索，
                    # 也是把三种"机器毫无反应"分开的最后一根证据。
                    raws = [e.get("raw") for e in entries]
                    check("  留痕保留了报文原文", "DOWN" in raws, f"raws={raws[:8]}")

                    print(f"     （累计收到 {recv.get('receivedCount')} 条，缓冲保留 {len(entries)} 条）")

            # 收尾：把通道关掉。它一路开着会一直占着那个端口，影响后面的步骤与本机其它冒烟。
            session.call("PUT", f"/api/agents/{new_id}/config",
                         {"udp": {"enabled": False, "port": udp_port2}})

            # ── ④.7 关掉之后中心必须"知道它关了"（2026-10-14，需求⑤ 的核心）──
            #
            # 🔥 这是整条需求的分水岭。在此之前，中心判"能不能发"只看配置 ——
            #    于是"关机"按钮会在机器**早已不监听**的时候回一句"已发出"，
            #    现场看到的只是"点了没反应"，而且两边都没错。
            #    现在中心拿的是客户机自己报的状态，它必须如实说"这台机器此刻没在听"，
            #    并把那台机器给出来的**原因**一并带出来。
            def _live_stopped():
                state = agent_live_udp(session, new_id)
                return bool(state) and state.get("listening") is False

            off_ok, _ = wait_for(_live_stopped, timeout=45, interval=1.0)

            check("关掉通道后中心同步到「未在监听」（≤45s，含一个心跳周期）", off_ok,
                  f"中心：{agent_live_udp(session, new_id)}")

            if off_ok:
                status, body, _ = session.call(
                    "POST", f"/api/agents/{new_id}/udp-command", {"command": "DOWN"})

                check("此时代发指令被拒（409），不再谎报「已发出」", status == 409,
                      f"HTTP {status} {str(body.get('message'))[:100]}")

                if status == 409:
                    msg = str(body.get("message") or "")
                    check("  拒绝理由转述的是客户机自己的说法（中心不再自己猜）",
                          "没有在监听" in msg and "客户机自己给的原因是" in msg,
                          msg[:140])

        # ── ⑤ 杀掉客户端 → 阈值内变离线 ──
        print("  → 结束客户端进程，等待服务端判定离线…")
        proc.terminate()

        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()

        def is_offline():
            detail = find_agent(session, new_id)
            if not detail:
                return None
            return detail.get("host", {}).get("state") == "offline"

        elapsed = time.time()
        offline, _ = wait_for(is_offline, timeout=60, interval=1.0)
        elapsed = time.time() - elapsed

        check("客户端退出后服务端判定离线", offline, f"{elapsed:.0f}s（阈值 30s）")

        if offline:
            detail = find_agent(session, new_id)
            check("  离线后长连接标记为断开", detail.get("connected") is False)
            check("  最后一次心跳时间被保留（不是 null）",
                  bool(detail.get("host", {}).get("lastSeenAt")),
                  str(detail.get("host", {}).get("lastSeenAt")))

        # ── ⑥ 温度驱动（2026-10-14，需求①）──
        #
        # 这一节读的是**客户机自己的日志**（不是接口），所以它验的是另一半：
        # "拿不到温度时，现场能不能一眼看出**为什么**" —— 是没启用、还是启用但驱动挂了。
        #
        # 🔥 为什么这里**不断言"默认开"**、以及"默认值到底在哪一头"的完整说明，
        #    写在 D 段开头 `thermal = inv.get("thermalState")` 上方那段注释里。一句话：
        #    · 需求①的落点是**安装向导那个勾选框** ⇒ 由 **F 段静态检查**钉住（.iss / install-agent.ps1）；
        #    · 裸起 exe（无 appsettings.json）⇒ 关，是**故意**的（方案 §22「默认不装驱动」）。
        #
        # ⚠️⚠️ 客户端**自己的日志走 <StateDir>\logs\<日期>.ndjson**（Serilog 文件 sink），
        #       控制台上只有 ASP.NET 那 3 行 "Application started / Hosting environment / Content root"。
        #       读错地方（比如读 stdout）会让下面这些断言**永远通过** ——
        #       那比没有断言更糟：它给了一个假的安心感。
        client_log = ""
        logs_dir = os.path.join(state_dir, "logs")
        if os.path.isdir(logs_dir):
            for name in sorted(os.listdir(logs_dir)):
                if name.endswith(".ndjson"):
                    try:
                        with open(os.path.join(logs_dir, name), "rb") as lf:
                            client_log += lf.read().decode("utf-8", errors="replace")
                    except OSError:
                        pass

        thermal_lines = []
        noisy = []
        for line in client_log.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except ValueError:
                continue

            msg = str(rec.get("Message") or "")
            level = int(rec.get("Level") or 1)          # 1=Info 2=Warning 3=Error
            level_text = str(rec.get("LevelText") or "")

            if "硬件温度" in msg:
                thermal_lines.append((level_text, msg))

            # 只有 Warning 及以上才算"刷日志"。Info 里提一句温度是正常的
            # （启动时会打印一行"硬件温度：未启用"）。
            if level >= 2 and any(k in msg for k in ("WinRing0", "驱动", "温度", "Thermal")):
                noisy.append("%s: %s" % (level_text, msg))

        check("  客户端日志里写明了温度监控的状态（不是留空让人猜）",
              any("硬件温度" in m for _lv, m in thermal_lines),
              str(thermal_lines[:1]))
        check("  温度驱动相关只记有限几行（失败也不重试、不刷屏）",
              len(noisy) <= 2, (noisy[:2] or [""])[0][:110])

        # UDP 指令通道的断言**不在这里**：那三条必须趁客户机还活着的时候读日志
        # （见 ④.5 与 client_messages 的注释 —— 进程被 terminate 掉之后日志尾巴可能就没了）。
    finally:
        if proc.poll() is None:
            proc.kill()

        log.close()

        # 状态目录里有 DPAPI 保护过的凭据 —— 测试产物一律清掉。
        shutil.rmtree(state_dir, ignore_errors=True)

        failed = bool(FAIL)
        if failed:
            print(f"  客户端日志已保留（便于排查）：{log_path}")
        else:
            try:
                os.remove(log_path)
            except OSError:
                pass


# ═══════════════════════════════════════════════════════════════
# E. 收尾（权限 + 移除）
# ═══════════════════════════════════════════════════════════════


def section_e(session, agent_id):
    print()
    print("E. 权限矩阵与移除")

    if agent_id is None:
        skip("权限矩阵", "B 段未拿到 agentId")
        return

    # 建一个 Operator：看板能读，写操作必须被 403 挡住。
    status, _, _ = session.call(
        "POST", "/api/users",
        {"userName": "smoke-operator", "password": "Operator@2026!", "role": "Operator"})

    if status != 200:
        skip("Operator 权限矩阵", f"创建账号失败 HTTP {status}")
    else:
        operator = Session()
        status, _, _ = operator.call(
            "POST", "/api/auth/login",
            {"userName": "smoke-operator", "password": "Operator@2026!"})

        if status != 200:
            check("Operator 能登录", False, f"HTTP {status}")
        else:
            check("Operator 能登录", True)

            status, _, _ = operator.call("GET", "/api/agents")
            check("  Operator 可读在线表（运维日常要看）", status == 200, f"HTTP {status}")

            status, _, _ = operator.call("POST", f"/api/agents/{agent_id}/block")
            check("  Operator 不可拉黑（仅 Admin）", status == 403, f"HTTP {status}")

            status, _, _ = operator.call("PUT", f"/api/agents/{agent_id}", {"note": "x"})
            check("  Operator 不可改名（仅 Admin）", status == 403, f"HTTP {status}")

            # ⚠️ 同样不能按状态码判（SPA 回退会给 200）—— 见 B 段同一处注释。
            status, body, _ = operator.call("GET", "/api/agents/enrollment")
            check("  令牌端点已整体移除（不再是令牌清单）",
                  not (status == 200 and isinstance(body, dict) and "tokens" in body),
                  f"HTTP {status} body={str(body)[:60]}")

    # ── 移除 ──
    # ⚠️ 这里**不能**断言"列表随之清空"：D 段起的那个**真客户端**已经用它自己的机器码
    #    在表里注册过一条记录，删掉 B 段这台之后列表里仍应剩 1 条。
    #    断言改成"这台不在了 + 条数正好减 1"，与表里还有别的机器无关。
    _, before_body, _ = session.call("GET", "/api/agents")
    total_before = total_of(before_body)

    status, body, _ = session.call("DELETE", f"/api/agents/{agent_id}")
    check("移除记录成功", status == 200 and body.get("ok") is True, f"HTTP {status}")

    remaining = agents_of(body)
    check("  被移除的机器不再出现在列表里",
          all(h.get("id") != agent_id for h in remaining),
          f"剩 {len(remaining)} 台")
    check("  列表条数正好减 1",
          total_before is not None and total_of(body) == total_before - 1,
          f"{total_before} -> {total_of(body)}")
    check("  移除的说明里讲清「不会卸载客户机上的 Agent」",
          "卸载" in (body.get("message") or ""),
          str(body.get("message"))[:80])


# ═══════════════════════════════════════════════════════════════
# F. 安装脚本静态检查（需求① 的落点）
# ═══════════════════════════════════════════════════════════════


def read_text(path):
    """读一个文本文件（容忍 UTF-8 BOM）。读不到返回空串，由调用方如实报 FAIL。"""
    try:
        with open(path, "rb") as fh:
            return fh.read().decode("utf-8-sig", errors="replace")
    except OSError:
        return ""


def section_f():
    """
    钉住温度开关的**默认值方向** —— 它在安装侧，不在 exe 里。

    <p>D 段起的是**裸 exe**（没有 appsettings.json）⇒ 温度如实报「未启用」。
    需求①要的却是"客户端的硬件温度监控设置为默认勾选"，那个勾**不在 exe 里**，
    在**安装向导**里，由两处串起来：</p>

    <ol>
      <li><c>ControlServer.Agent.iss</c> 的向导复选框 <c>ThermalCheck.Checked := GetThermalValue()</c>，
          而 <c>GetThermalValue()</c> 在 <c>/THERMAL=</c> 缺失时返回 True ⇒ <b>默认勾上</b>；</li>
      <li>勾着 ⇒ 不追加 <c>-NoThermal</c> ⇒ <c>install-agent.ps1</c> 的
          <c>$thermal = -not $NoThermal.IsPresent</c> 得到 True ⇒ 把
          <c>Agent:Telemetry:ThermalEnabled=true</c> 写进 appsettings.json。</li>
    </ol>

    <p>⚠️ 为什么用静态检查、而不是"跑一遍安装"：安装要管理员、会建服务、还会加载内核驱动，
    冒烟里跑不起（也不该跑）。而这里恰恰只要钉住一个纯文本事实 —— "默认值在哪一边"，
    读文件比进程内观察更直接、更稳，而且**在任何机器上都能跑**。</p>

    <p>🔥 同样要钉住的是**方向不许写反**：旧写法是"勾了才传 <c>-ThermalEnabled</c>"，
    默认值翻过来之后它会静默地**反过来**（不传 = 关）—— 图形界面看着还是勾着的，
    现场却拿到"未启用"。这类反向错误只能靠把方向本身钉死来防。</p>
    """
    print()
    print("F. 安装脚本静态检查（温度默认值 → 需求①）")

    ps1_path = os.path.join(REPO, "deploy", "windows", "install-agent.ps1")
    iss_path = os.path.join(REPO, "deploy", "windows", "ControlServer.Agent.iss")

    for path in (ps1_path, iss_path):
        check(f"{os.path.basename(path)} 存在", os.path.isfile(path), path)

    ps1 = read_text(ps1_path)
    iss = read_text(iss_path)

    # ── install-agent.ps1：不传 -NoThermal ⇒ 开 ──
    assigns = re.findall(r"^\s*\$thermal\s*=", ps1, flags=re.M)
    check("install-agent.ps1：$thermal 只有一处赋值（不会冒出两个互相打架的默认值）",
          len(assigns) == 1, f"{len(assigns)} 处")
    check("install-agent.ps1：不传 -NoThermal 就是开（默认值在「开」这一边）",
          re.search(r"\$thermal\s*=\s*-\s*not\s+\$NoThermal\.IsPresent", ps1) is not None)
    check("install-agent.ps1：写进 appsettings 的 ThermalEnabled 取自那个变量（不是写死的 false）",
          re.search(r"-\s*ThermalEnabled\s+\$thermal\b", ps1) is not None)
    check("install-agent.ps1：已废弃的 -ThermalEnabled 不再参与计算（保留只为不破坏旧批处理）",
          re.search(r"\$thermal\s*=[^\n]*\$ThermalEnabled", ps1) is None)

    # ── ControlServer.Agent.iss：静默装与图形装两条路必须同向 ──
    check("Agent.iss：/THERMAL= 缺失时 GetThermalValue() 返回 True（静默装默认开）",
          re.search(r"if\s+V\s*=\s*''\s*then\s*\n\s*Result\s*:=\s*True", iss) is not None)
    check("Agent.iss：向导复选框的初始状态取自同一个函数（图形装默认勾上）",
          re.search(r"ThermalCheck\.Checked\s*:=\s*GetThermalValue\(\)", iss) is not None)
    check("Agent.iss：只有**没勾**才追加 -NoThermal（勾着时命令行里不出现这个开关）",
          re.search(r"if\s+not\s+ThermalValue\s+then\s*\n\s*Cmd\s*:=\s*Cmd\s*\+\s*'[^']*-NoThermal'",
                    iss) is not None)
    check("Agent.iss：没有留下「勾了才传 -ThermalEnabled」的旧方向（默认值翻过来后它会变成反向错误）",
          re.search(r"\bif\s+ThermalValue\s+then[\s\S]{0,300}?-ThermalEnabled", iss) is None)


# ═══════════════════════════════════════════════════════════════
# G. 离线客户端独立可用（2026-10-14，需求①）
#
# 需求原话：「客户端功能优化，在未连接到服务端时，必须打开 udp 监听。」
#
# 这一段起一台**找不到任何中心**的客户机（不配 ServerUrl + 关掉广播发现），验：
#   ① 崭新机器（无配置 / 无注册 / 无中心）netstat 里就必须有 8090；
#   ② 显式把 UDP 关掉、再换个端口 ⇒ 未连接期间**仍然绑着**（端口沿用配置）；
#   ③ 这条通道是**活的**：真发一条明文 DOWN，必须"收到了、认出来了、没执行"；
#   ④ 本机配置页离线也能开，且「服务端」卡片如实说"还不知道"（null，不是 0）。
#
# 🔥 为什么单开一段、不并进 D 段：D 段验的是「**连着中心时**按配置走」，
#    这里验的是「**没有中心时**怎么办」。两条路的分岔点正是这个缺陷藏身的地方 ——
#    改版前 UdpCommandListener.Apply 只在"本机有配置"或"连上中心"之后才被调用，
#    于是一台从没连过中心、也没有配置的新机器**一次都没被 Apply 过**，
#    netstat 里连 8090 都不存在。现场形态就是"机器装好了，发指令毫无反应"。
#
# ⚠️⚠️ 全程 ExecuteUdpCommands=false（演练模式）：这一段会真的发一条明文 DOWN，
#       少了那个开关，跑冒烟的这台机器会被**真的关掉**。
# ═══════════════════════════════════════════════════════════════


def section_g():
    print()
    print("G. 离线客户端独立可用（未连接中心 → 必须开着 UDP 监听 · 需求①）")

    if not os.path.isfile(AGENT_EXE):
        skip("离线客户机", f"找不到 {os.path.relpath(AGENT_EXE, REPO)}（先 dotnet build 再跑冒烟）")
        return

    state_dir = os.path.join(tempfile.gettempdir(), f"cs-agent-offline-{os.getpid()}")
    shutil.rmtree(state_dir, ignore_errors=True)

    log_path = os.path.join(tempfile.gettempdir(), f"cs-agent-offline-{os.getpid()}.log")
    log = open(log_path, "wb")

    env = dict(os.environ)
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")

    ui_port = free_tcp_port()

    # 换端口那一步要用的端口：**绝不能**等于 8090，否则"换端口到底生效没有"就分辨不出来。
    offline_port = free_udp_port()
    while offline_port == 8090:
        offline_port = free_udp_port()

    # ⚠️ 开工前先问一句"8090 有没有被别人占着"（见 udp_port_owner 的注释）。
    squatter = udp_port_owner(8090)

    argv = [
        AGENT_EXE,
        f"--Agent:StateDirectory={state_dir}",
        # 🔥 不配 ServerUrl + 关掉广播发现 ⇒ 一个中心都找不到。
        #    这比"中心挂了"更干净：没有超时抖动、没有半连接状态，
        #    就是一份纯粹的"这台机器此刻孤立着"。
        "--Agent:AllowBroadcastDiscovery=false",
        "--Agent:ServiceName=ControlServerAgentSmokeOffline",
        # 🔥 演练模式 —— 这一段唯一的安全带（见段头注释）。
        "--Agent:Tasks:ExecuteUdpCommands=false",
        f"--Agent:LocalUi:Port={ui_port}",
        "--Agent:Telemetry:Enabled=false",
    ]

    print(f"  客户端： {os.path.relpath(AGENT_EXE, REPO)}")
    print(f"  状态目录：{state_dir}")
    print(f"  本机配置页： http://127.0.0.1:{ui_port}/ （没有中心，它也要能开）")

    proc = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, cwd=REPO, env=env)
    local = f"http://127.0.0.1:{ui_port}"

    try:
        # ── ① 崭新机器 + 没有中心 ⇒ 默认端口必须已经绑上 ──
        if squatter:
            skip("崭新机器（无配置 / 无中心）默认就监听 8090",
                 f"本机 8090 已经被 pid={squatter} 占着（多半是已安装的客户端服务在跑）—— "
                 "分不清是不是我们绑的，跳过而不是误报 FAIL")
        else:
            bound, _ = wait_for(lambda: udp_listening(8090, proc.pid), timeout=45, interval=1.0)

            check("崭新机器（无配置 / 无中心）默认就监听 8090（≤45s）", bound,
                  f"netstat 里没有 pid={proc.pid} 占着 8090；客户端日志：{log_path}")

            # 顺手钉住"为什么在听"这句话必须落在日志里 ——
            # 少了它，现场只看到端口开着，却不知道为什么（而原因正是这个需求的全部内容）。
            started, _ = wait_for(
                lambda: any("UDP 指令通道已启动" in m and "未连接中心" in m
                            for m in client_messages(state_dir)),
                timeout=15, interval=1.0)

            check("  日志里说清了「为什么在听」= 未连接中心（不是配置的功劳）", started,
                  f"日志目录：{os.path.join(state_dir, 'logs')}")

        # ── ② 本机配置页：离线也必须可用，且「服务端」卡片要如实说"还不知道" ──
        up, _ = wait_for(lambda: raw_get(local + "/api/config")[0] == 200, timeout=40, interval=1.0)
        check("离线时本机配置页照常可用（≤40s）", up, local)

        if not up:
            return

        _, page = raw_get(local + "/")

        # 写操作要带令牌，令牌嵌在页面 JS 里（跨站页面读不到它，见 AgentLocalConfigUi 的三道闸门）。
        try:
            token = page.split("const TOKEN = '")[1].split("'")[0]
        except IndexError:
            check("本机配置页里能取到写操作令牌", False, "页面里找不到 const TOKEN = '…'")
            return

        _, body = raw_get(local + "/api/config")
        state = json.loads(body)

        check("「服务端」卡片：连接状态如实为 False", state.get("connected") is False,
              str(state.get("connected")))

        # 🔥 这三个 null 的语义都是"**还不知道**"，不是 0 / 空串。
        #    离线机器上把服务端版本显示成 "0.0.0" 会让人以为"中心很旧"——
        #    那比什么都不显示更误导（"未知一律 null，不用 0 代替"那条铁律的落点）。
        check("「服务端」卡片：服务端版本为 null（还不知道，而不是编一个 0）",
              state.get("serverVersion") is None, str(state.get("serverVersion")))
        check("「服务端」卡片：最后通信时间为 null（没通信过 ≠ 通信于 1970 年）",
              state.get("lastSeenAt") is None, str(state.get("lastSeenAt")))
        check("「服务端」卡片：服务端地址为空（没连过就是不知道）",
              not state.get("serverUrl"), str(state.get("serverUrl")))

        # ── ②-补 「服务端」明细**默认收起**，收起时只留"已连接中心 + 地址"（2026-10-15）──
        #
        # ⚠️ 断言的是**结构**（谁在谁里面、hidden 在不在），不是"页面里有没有这几个字"。
        #    后者对着"明细被挪到卡片外""hidden 被删掉"这两类回归一样会点头，
        #    而它们正是这条需求的全部内容。
        # ⚠️ 还顺带钉住一件事：这是**从跑着的客户端手里取回来的页面**，
        #    所以它同时证明了"新页面真的被嵌进 exe 了"（纯读源文件是证不了的）。
        # ⚠️ detail 一律写**中性证据**（属性列表 / 名单），别写失败诊断 ——
        #    check() 在 PASS 时也照印 detail，写成"没有 hidden"就会读成一条假 FAIL。
        idx = parse_page(page)

        check("「服务端」明细默认收起（serverDetail 带 hidden）",
              "hidden" in idx.attrs.get("serverDetail", {}),
              f"serverDetail 的属性：{sorted(idx.attrs.get('serverDetail', {}))}")

        # 🔥 存在性单独断言：元素**不存在**时 parents 也是空列表，
        #    会被下面"不在明细里"那条顺顺当当地放过 —— 那就是一条假 PASS。
        summary_members = ["serverBadge", "serverAddr", "serverStatus", "serverToggle"]
        missing = [i for i in summary_members if i not in idx.attrs]
        check("  摘要行四件都在（徽标 / 地址 / 状态 / 展开开关）",
              not missing, f"缺：{missing}")

        detail_members = ["serverInfo", "serverNote", "serverTarget", "serverEditNote"]
        folded = [i for i in detail_members if "serverDetail" in idx.parents.get(i, [])]
        check("  被收进去的就是那份明细（信息表 / 说明 / 改地址的输入框）",
              len(folded) == len(detail_members),
              f"在里头：{folded}")

        # 🔥 收起态的门槛：徽标 + 地址必须在明细**之外** —— 收起来以后这两样要是也看不见，
        #    那这块卡片就退化成一句"服务端"，现场连"连的是哪个中心"都读不到。
        #    状态文案同理留在外面：连不上时它正是"为什么"（被拉黑 / 在线表满 / 正在广播找）。
        stayed = [i for i in summary_members if "serverDetail" not in idx.parents.get(i, [])]
        check("  收起后该露的仍露着（四件都在明细之外）",
              len(stayed) == len(summary_members),
              f"不在明细里：{stayed}")

        # ── ③ 需求① 的核心断言：配置说「关」+ 没有中心 ⇒ 端口仍然绑着 ──
        print(f"  → 在本机页面把 UDP 显式关掉、端口改成 {offline_port}，看它到底听不听…")

        st, saved = raw_put(
            local + "/api/config",
            {
                "udp": {"enabled": False, "port": offline_port, "blockedSourceIps": []},
                "sendTasks": [],
                "scheduledTasks": [],
                "launchTasks": [],
            },
            headers={"X-CS-Local-Token": token},
        )

        check("离线时本机保存成功（配置里明写 udp.enabled=false）", st == 200, saved[:160])

        still, _ = wait_for(lambda: udp_listening(offline_port, proc.pid), timeout=45, interval=1.0)

        check("配置说「关」、又没有中心 ⇒ 端口仍然绑着（需求① 核心）", still,
              f"端口 {offline_port}。这正是这个缺陷原来的形态："
              "中心不在 + 配置关 = 这台机器一条指令都收不到，而两边都不报错")

        # ── ④ 页面上必须看得见"通道在听 + 为什么在听 + 听的是哪个端口" ──
        _, rx_text = raw_get(local + "/api/received")
        received = json.loads(rx_text)
        state_text = str(received.get("udpStateText") or "")

        check("  「接收到的数据」回报通道正在监听", received.get("udpListening") is True,
              state_text[:110])
        check("  状态文案说得出「未连接中心，已强制开启」", "未连接中心" in state_text,
              state_text[:110])
        check("  回报的端口 = 配置里那个（未连接时端口沿用配置）",
              received.get("udpPort") == offline_port,
              f"期望 {offline_port}，得到 {received.get('udpPort')}")

        # ── ⑤ 这条通道是**活的**：真发一条明文报文 ──
        #
        # ⚠️ 证据从**客户机自己的页面**取（不是从日志）：页面上的「接收到的数据」
        #    本来就是给现场用的那个视图，"这条记录能不能读到"本身就是被验的一部分。
        send_udp(offline_port, "DOWN")

        def seen():
            st2, rx2 = raw_get(local + "/api/received")
            if st2 != 200:
                return None
            for e in (json.loads(rx2).get("entries") or []):
                if (e.get("raw") or "").strip() == "DOWN":
                    return e
            return None

        got, entry = wait_for(seen, timeout=25, interval=1.0)

        check("离线通道真的收得到：明文 DOWN 被认出来了（≤25s）", bool(got),
              f"端口 {offline_port}；页面：{local}/api/received —— "
              "端口开着却认不出报文，与「根本没开」在现场长得一模一样")

        if got:
            # 🔥 演练模式必须挡住执行 —— 否则这条断言通过就意味着跑冒烟的机器刚被关掉了。
            check("  演练模式挡住了执行（机器没有被关掉）",
                  entry.get("outcome") == "ignored", str(entry.get("outcome")))
            check("  留痕保留了报文原文", entry.get("raw") == "DOWN", str(entry.get("raw")))

        # ── ⑥ 停机：端口必须收回去 ──
        # 一个"进程没了但端口还开着"的入站口是最糟的形态：报文能进来、没人处理、也没人知道。
        proc.terminate()

        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()

        released, _ = wait_for(
            lambda: not udp_listening(offline_port, proc.pid), timeout=20, interval=1.0)

        check("客户机退出后端口被收回（不留一个没人处理的入站口）", released,
              f"端口 {offline_port}")

    finally:
        if proc.poll() is None:
            proc.kill()

        log.close()

        # 状态目录里有 DPAPI 保护过的凭据 —— 测试产物一律清掉。
        shutil.rmtree(state_dir, ignore_errors=True)

        if FAIL:
            print(f"  客户端日志已保留（便于排查）：{log_path}")
        else:
            try:
                os.remove(log_path)
            except OSError:
                pass


# ═══════════════════════════════════════════════════════════════
# H. 移除之后加得回来（2026-10-14 修）
#
# 🔥 这一段是为一个**真漏洞**写的回归：管理员在网页上把一台客户机「移除」之后，
#    那台机器再也回不来了 —— 而且**连重启客户端都救不回来**，
#    只能去客户机上把本地状态目录删掉。
#
#    两个洞（缺一不可，都在这一段里被钉住）：
#      ① 在线时被移除 ⇒ 服务端**不告诉客户端**：心跳查不到这台机器时走的是
#         "视为正常"的分支（`MarkSeen` 还会给一个已不存在的 Id 建内存态），
#         于是客户端以为自己还在线，长连接变成僵尸，服务端却已经把它忘了。
#      ② 重连被拒之后客户端**不重新注册**：`EnsureRegisteredAsync` 只要"本地凭据里
#         的 serverUrl + machineKey 与当前一致"就直接复用 ⇒ 记录被删后那把钥匙
#         必然 401，而客户端把 401 当普通连接失败，拿着它无限重试。
#
#    所以断言必须落在**两件事**上，缺一个都可能是假绿：
#      · 列表里那台机器回来了，而且 Id 变了（＝**新记录**，不是旧记录被复活）；
#      · 客户机自己的日志里出现了**第二次「注册成功」**（＝它真的重注册了，
#        而不是别的机器补上来、或者列表碰巧又有了它）。
# ═══════════════════════════════════════════════════════════════


def section_h(session):
    print()
    print("H. 移除之后加得回来（管理员「移除」⇒ 客户机必须自己重新注册回来）")

    if not os.path.isfile(AGENT_EXE):
        skip("移除后自动回归",
             f"找不到 {os.path.relpath(AGENT_EXE, REPO)}（先 dotnet build 再跑冒烟）")
        return

    state_dir = os.path.join(tempfile.gettempdir(), f"cs-agent-rejoin-{os.getpid()}")
    shutil.rmtree(state_dir, ignore_errors=True)

    log_path = os.path.join(tempfile.gettempdir(), f"cs-agent-rejoin-{os.getpid()}.log")
    log = open(log_path, "wb")

    env = dict(os.environ)
    # apphost 会回退到 Program Files 找运行时，本机没有 —— 必须指到托管 SDK 目录。
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")

    argv = [
        AGENT_EXE,
        f"--Agent:ServerUrl={BASE}",
        f"--Agent:StateDirectory={state_dir}",
        "--Agent:ServiceName=ControlServerAgentRejoin",
        "--Agent:AllowBroadcastDiscovery=false",
        # 与 D 段同一个理由：这一段会让客户机反复重连，演练模式是唯一的安全带。
        "--Agent:Tasks:ExecuteUdpCommands=false",
        "--Agent:Telemetry:Enabled=false",
    ]

    print(f"  客户端： {os.path.relpath(AGENT_EXE, REPO)}")
    print(f"  状态目录：{state_dir}")

    proc = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, cwd=REPO, env=env)

    # ⚠️ 判据用列表里的 `state`（online / unstable），**不用** `connected`：
    #    `connected` 只在详情端点里（"此刻握着长连接"）；列表端点给的是
    #    由最后心跳算出来的在线态 —— 而"重新注册之后能不能再上线"要的正是后者。
    def online_host():
        status, body, _ = session.call("GET", "/api/agents")
        if status != 200:
            return None
        for host in agents_of(body):
            if host.get("state") in ("online", "unstable"):
                return host
        return None

    def online_host_except(exclude_id):
        host = online_host()
        return host if host and host.get("id") != exclude_id else None

    try:
        # ── ① 先让它上线，并记下服务端给它的 Id ──
        up, first = wait_for(online_host, timeout=60, interval=1.5)

        check("客户机上线（移除之前）", up, f"客户端日志：{log_path}")

        if not up:
            return

        first_id = first.get("id")
        first_key = first.get("machineKey")

        # ── ② 管理员在网页上「移除」它（＝硬删记录）──
        status, _body, _headers = session.call("DELETE", f"/api/agents/{first_id}")
        check("移除返回成功", status == 200, f"HTTP {status}")

        gone, _ = wait_for(
            lambda: not any(h.get("id") == first_id
                            for h in agents_of(session.call("GET", "/api/agents")[1])),
            timeout=15, interval=1.0)

        check("移除后它从在线表里消失", gone)

        # ── ③ 关键：它必须自己回来（作废旧凭据 → 重新注册 → 重新上线）──
        # 上限给足 150 秒：服务端得先把那条僵尸长连接掐掉，客户端才会撞上 401。
        back, second = wait_for(lambda: online_host_except(first_id), timeout=150, interval=2.0)

        check("移除后客户机自动重新注册回来（≤150s）", back,
              f"没回来；客户端日志：{log_path}")

        if back:
            check("  回来的还是同一台机器（机器码一致）",
                  second.get("machineKey") == first_key,
                  f"{first_key} → {second.get('machineKey')}")

            # 硬删之后重新注册必然**新插一行** ⇒ Id 必须变。
            # 不变就说明"回来的"其实是旧记录被复活（那不是这条链路该有的行为）。
            check("  是**新记录**而不是旧记录被复活（Id 变了）",
                  second.get("id") != first_id,
                  f"Id {first_id} → {second.get('id')}")

            # 日志证据（**趁它活着读**，见 client_messages 的注释）：
            # "注册成功"必须出现**两次** —— 只断言"列表里又有了"是不够的，
            # 那也可能是别的机器补上来的。
            registrations = [m for m in client_messages(state_dir) if "注册成功" in m]

            check("  客户机日志里出现了第二次「注册成功」", len(registrations) >= 2,
                  f"只找到 {len(registrations)} 次：{registrations}")

            # 而且要**说清原因**：凭据被中心作废了（而不是"网断了"这种含糊结论）。
            # 少了它，现场只看到"它自己回来了"，却不知道为什么。
            reason, _ = wait_for(
                lambda: any("作废" in m or "凭据" in m for m in client_messages(state_dir)),
                timeout=10, interval=1.0)

            check("  日志里说明了原因是「凭据被中心作废」", reason,
                  f"日志目录：{os.path.join(state_dir, 'logs')}")
    finally:
        if proc.poll() is None:
            proc.kill()

        log.close()
        shutil.rmtree(state_dir, ignore_errors=True)

        # 这一段失败时日志必须留着 —— 它的全部证据都在里面。
        if FAIL:
            print(f"  客户端日志已保留（便于排查）：{log_path}")
        else:
            try:
                os.remove(log_path)
            except OSError:
                pass


# ═══════════════════════════════════════════════════════════════


print("══════════════════ 客户端Server 冒烟（M4.1）══════════════════")
print(f"  服务端：{BASE}")
print(f"  发现端口：{DISCOVERY_PORT}")
print(f"  数据目录：{DATA_DIR}")

session = Session()
ok, body = login_admin(session)

if not ok:
    print(f"  [X] 管理员登录失败（HTTP）：{body}")
    sys.exit(1)

print("  → 管理员已登录")

section_a(session)
agent_id = section_b(session)
section_c()
section_d(session)
section_e(session, agent_id)
section_f()
section_g()
section_h(session)

print()
print("══════════════════ 汇总 ══════════════════")
print(f"  通过 {len(PASS)} / 失败 {len(FAIL)}")

for name in FAIL:
    print(f"  [FAIL] {name}")

sys.exit(0 if not FAIL else 1)
