#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · 文件分发契约冒烟（M4.4）

它验证的是"**客户机侧那套 HTTP 调用能不能真的跑通**"，也就是 ControlServer.Agent
里 FileTransferClient 所假设的那份契约。这一层最容易出的问题是**静默型**的：
路径写错一个字母、字段名对不上、身份策略把它挡在门外 —— 都不会有编译错误，
只会表现为"客户端日志说发了、服务端什么都没有"。

覆盖：
  A. 身份           同一个端点，人（Cookie）与机器（X-Agent-Key）都能用；Viewer 不能
  B. 上传（回传）    建会话 → 分块追加（含 409 偏移纠正）→ 完成（服务端自算摘要）
  C. 下发            建任务（离线机器）→ 客户机补领 → Range 下载 → 回报进度 → 完成
  D. 汇总            任务状态由明细汇总；终态后重复上报不再改写

⚠️ 它**不启动真客户端进程**（那是 agent-smoke.py 的 D 段与本文件的后续扩展）。
   这里用"假装成客户机的 HTTP 调用"来钉住契约，跑得快、定位准。

用法：
    python deploy/smoke/transfer-smoke.py <baseUrl> <dataDir>
"""

import hashlib
import http.cookiejar
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18120").rstrip("/")
DATA_DIR = sys.argv[2] if len(sys.argv) > 2 else ""

SEED_ADMIN = "admin"
SEED_PASSWORD = "admin123"

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# Agent 服务端 exe（F 段用）。找不到就 SKIP —— 与 agent-smoke.py 的 D 段同一约定：
# "跑一次冒烟要先编客户端"会挡住绝大多数只是改服务端的改动。
# 可用 CS_AGENT_EXE 指向打包产物，验证"真正发出去的那个 exe"行为一致。
AGENT_EXE = os.environ.get("CS_AGENT_EXE") or os.path.join(
    REPO, "agents", "ControlServer.Agent.Service", "bin", "Debug", "net8.0",
    "ControlServer.Agent.Service.exe")

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))


def skip(name, why):
    print(f"  [SKIP] {name} —— {why}")


def wait_for(fn, timeout=60.0, interval=1.0):
    """轮询直到 fn() 返回真值。返回 (是否成功, 最后一次的非假值)。"""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = fn()
        if last:
            return True, last
        time.sleep(interval)
    return False, last


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def covers(whitelist, target):
    """
    `target` 是否落在 `whitelist` 中某一个目录之内（镜像服务端的 `FileTransferMath.IsInside`）。

    ⚠️ **盘根（`C:\\`）自带尾分隔符，不能再补一个** —— 补成 `C:\\\\` 会让整个盘都判成
    "不在目录内"，表现是"白名单里明明写了 D 盘、却一个文件都取不回来"（2026-10-15 修）。
    """
    t = os.path.normcase(os.path.normpath(target))

    for raw in whitelist:
        d = os.path.normcase(os.path.normpath(raw))

        if t == d:
            return True

        prefix = d if d.endswith(("\\", "/")) else d + os.sep

        if t.startswith(prefix):
            return True

    return False


def _decode(content):
    try:
        return json.loads(content.decode("utf-8"))
    except Exception:
        return content.decode("utf-8", errors="replace")


class Client:
    """一个会话（独立 Cookie jar）+ 可选的 AgentKey 身份。"""

    def __init__(self, agent_key=None):
        self.agent_key = agent_key
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
        )

    def _headers(self, extra=None):
        headers = dict(extra or {})
        if self.agent_key:
            headers["X-Agent-Key"] = self.agent_key
        return headers

    def call(self, method, path, body=None, headers=None):
        data = None
        hdrs = self._headers(headers)
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            hdrs["Content-Type"] = "application/json"

        req = urllib.request.Request(BASE + path, data=data, headers=hdrs, method=method)
        try:
            with self.opener.open(req, timeout=60) as resp:
                return resp.status, _decode(resp.read()), dict(resp.headers)
        except urllib.error.HTTPError as e:
            return e.code, _decode(e.read()), dict(e.headers)

    def raw(self, method, path, data=None, headers=None):
        """字节级调用（分块上传 / Range 下载）。"""
        req = urllib.request.Request(
            BASE + path, data=data, headers=self._headers(headers), method=method)
        try:
            with self.opener.open(req, timeout=60) as resp:
                return resp.status, resp.read(), dict(resp.headers)
        except urllib.error.HTTPError as e:
            return e.code, e.read(), dict(e.headers)


def login_admin(client):
    status, body, _ = client.call(
        "POST", "/api/auth/login", {"userName": SEED_ADMIN, "password": SEED_PASSWORD})
    if status != 200:
        print(f"  登录失败：HTTP {status} {body}")
        sys.exit(2)


def enroll_agent(admin):
    """免凭据注册一台"假的"客户机（只走 HTTP，不建 SignalR 连接）。"""
    anon = Client()
    status, result, _ = anon.call("POST", "/api/agents/register", {
        "hostInfo": {
            "machineKey": "transfer-smoke-0001",
            "hostName": "TRANSFER-SMOKE",
            "osDescription": "Windows 11 (smoke)",
            "cpuName": "Smoke CPU @ 1.00GHz",
            "logicalProcessors": 8,
            "totalMemoryMb": 16384,
            "agentVersion": "0.1.0",
            "userName": "smoke",
            "interfaces": [
                {"name": "以太网", "address": "192.168.1.60", "mac": "AA:BB:CC:DD:EE:61", "isUp": True}
            ],
        },
    })

    if status != 200 or not result.get("ok"):
        print(f"  客户机注册失败：HTTP {status} {result}")
        sys.exit(2)

    return result.get("agentId"), result.get("agentKey")


def main():
    print(f"== 文件分发契约冒烟（M4.4）base={BASE} ==")

    admin = Client()
    login_admin(admin)

    agent_id, agent_key = enroll_agent(admin)
    machine = Client(agent_key)
    print(f"  已注册一台假客户机：Id={agent_id}")

    # ═══════════════════════════════════════════════════════════
    # A. 身份：机器身份能过、但拿不到人的能力
    # ═══════════════════════════════════════════════════════════
    print("\n-- A. 身份 --")

    status, body, _ = machine.call("GET", "/api/files")
    check("机器身份读仓库被拒（FileRead 不含机器列表权限）", status in (401, 403),
          f"HTTP {status}")

    status, body, _ = machine.call("GET", "/api/agents/transfers")
    check("机器身份可补领待办（初始为空）", status == 200 and body == [], f"HTTP {status} {body}")

    anon = Client()
    status, _, _ = anon.call("GET", "/api/agents/transfers")
    check("匿名补领被拒（401）", status == 401, f"HTTP {status}")

    # ═══════════════════════════════════════════════════════════
    # B. 上传（回传方向用同一条协议）
    # ═══════════════════════════════════════════════════════════
    print("\n-- B. 分块上传 --")

    payload = bytes((i * 7 + 13) % 251 for i in range(300 * 1024))
    sha = hashlib.sha256(payload).hexdigest()

    status, session, _ = admin.call("POST", "/api/files/uploads", {
        "fileName": "smoke-payload.bin", "size": len(payload)})
    check("建上传会话成功", status == 200 and bool(session.get("uploadId")),
          f"HTTP {status} {session}")
    upload_id = session.get("uploadId")
    chunk = int(session.get("chunkSizeBytes") or 0)
    check("  会话回传了建议分块大小", chunk > 0, str(chunk))
    check("  初始 receivedBytes=0", session.get("receivedBytes") == 0,
          str(session.get("receivedBytes")))

    # 故意先发一个错误偏移：服务端应回 409 + 真实进度（这是续传的对齐信号，不是错误）
    status, raw_body, _ = admin.raw("PUT", f"/api/files/uploads/{upload_id}?offset=999999",
                                    data=payload[:16], headers={"Content-Type": "application/octet-stream"})
    body = _decode(raw_body)
    check("偏移不符回 409 并带真实 receivedBytes", status == 409 and "receivedBytes" in body,
          f"HTTP {status} {body}")
    check("  真实 receivedBytes 为 0（还没收过）", body.get("receivedBytes") == 0,
          str(body.get("receivedBytes")))

    # 正式分块上传
    sent = 0
    while sent < len(payload):
        piece = payload[sent:sent + chunk]
        status, raw_body, _ = admin.raw("PUT", f"/api/files/uploads/{upload_id}?offset={sent}",
                                        data=piece, headers={"Content-Type": "application/octet-stream"})
        body = _decode(raw_body)
        if status != 200:
            check(f"分块上传（offset={sent}）", False, f"HTTP {status} {body}")
            sys.exit(1)
        sent = body.get("receivedBytes", sent + len(piece))

    check("全部分块上传完成", sent == len(payload), f"{sent}/{len(payload)}")

    status, body, _ = admin.call("GET", f"/api/files/uploads/{upload_id}")
    check("查会话进度 = 文件大小", status == 200 and body.get("receivedBytes") == len(payload),
          f"HTTP {status} {body.get('receivedBytes')}")

    # 摘要不符应被拒（客户端自报只是"期望值"，服务端自己算）
    status, body, _ = admin.call("POST", f"/api/files/uploads/{upload_id}/complete",
                                 {"sha256": "0" * 64})
    check("摘要不符被拒（409）", status == 409, f"HTTP {status} {body}")

    status, result, _ = admin.call("POST", f"/api/files/uploads/{upload_id}/complete",
                                   {"sha256": sha})
    check("完成上传成功", status == 200 and bool(result.get("id")), f"HTTP {status} {result}")
    check("  服务端算出的摘要与本地一致", result.get("sha256") == sha,
          f"{result.get('sha256')}")
    file_id = result.get("id")

    status, repo, _ = admin.call("GET", "/api/files")
    check("仓库列表里能看到它", status == 200 and any(
        x.get("id") == file_id for x in (repo.get("items") or [])), f"HTTP {status}")

    # ═══════════════════════════════════════════════════════════
    # C. 下发：离线机器靠补领拿到任务，用 Range 拉字节
    # ═══════════════════════════════════════════════════════════
    print("\n-- C. 下发与 Range 续传 --")

    status, task, _ = admin.call("POST", "/api/distributions", {
        "fileId": file_id, "targetAgentIds": [agent_id], "targetDir": "inbox"})
    check("建下发任务成功", status == 200 and bool(task.get("id")), f"HTTP {status} {task}")
    task_id = task.get("id")
    check("  任务初始状态为 pending（机器离线）", task.get("state") == "pending",
          str(task.get("state")))
    check("  明细为每台目标各建一行", len(task.get("items") or []) == 1,
          str(len(task.get("items") or [])))

    status, pending, _ = machine.call("GET", "/api/agents/transfers")
    check("客户机补领到 1 条任务（离线机器也能收到）",
          status == 200 and len(pending) == 1, f"HTTP {status} {pending}")

    if not pending or not isinstance(pending, list):
        report()
        return

    job = pending[0]
    check("  载荷带方向 push", job.get("direction") == "push", str(job.get("direction")))
    check("  载荷带仓库文件 id", job.get("fileId") == file_id, str(job.get("fileId")))
    check("  载荷带期望 SHA256（客户端落地前必须校验）", job.get("sha256") == sha,
          str(job.get("sha256")))
    check("  载荷带目标目录标识", job.get("targetDir") == "inbox", str(job.get("targetDir")))
    item_id = job.get("itemId")

    # Range 续传：先取前 1000 字节，再从 1000 处接着取
    status, first, headers = machine.raw(
        "GET", f"/api/files/{file_id}/download", headers={"Range": "bytes=0-999"})
    check("机器身份 Range 下载返回 206", status == 206, f"HTTP {status}")
    check("  首段正好 1000 字节", len(first) == 1000, str(len(first)))

    status, rest, headers = machine.raw(
        "GET", f"/api/files/{file_id}/download", headers={"Range": "bytes=1000-"})
    check("从断点续下载返回 206", status == 206, f"HTTP {status}")
    check("  续传剩余字节数与理论一致", len(rest) == len(payload) - 1000, str(len(rest)))
    check("  两段拼起来内容完整", first + rest == payload, "拼接比对")
    check("  拼起来摘要一致", hashlib.sha256(first + rest).hexdigest() == sha, "SHA256")

    # 回报进度
    status, ack, _ = machine.call(
        "POST", f"/api/agents/transfers/{item_id}/progress",
        {"receivedBytes": 150 * 1024, "totalBytes": len(payload), "error": None})
    check("回报进度成功", status == 200 and ack.get("ok") is True, f"HTTP {status} {ack}")

    # 回报完成
    status, ack, _ = machine.call(
        "POST", f"/api/agents/transfers/{item_id}/complete",
        {"ok": True, "receivedBytes": len(payload), "sha256": sha, "error": None})
    check("回报完成成功", status == 200 and ack.get("status") == "completed",
          f"HTTP {status} {ack}")

    # ═══════════════════════════════════════════════════════════
    # D. 汇总与幂等
    # ═══════════════════════════════════════════════════════════
    print("\n-- D. 任务汇总与幂等 --")

    status, detail, _ = admin.call("GET", f"/api/distributions/{task_id}")
    check("任务详情可读", status == 200, f"HTTP {status}")
    check("  任务总状态已汇成 completed", detail.get("state") == "completed",
          str(detail.get("state")))
    check("  成功计数为 1", detail.get("succeeded") == 1, str(detail.get("succeeded")))
    item = (detail.get("items") or [{}])[0]
    check("  明细进度为 100%", item.get("progressPct") == 100.0, str(item.get("progressPct")))
    check("  明细记下了 SHA256", item.get("state") == "completed", str(item.get("state")))

    # 重复上报"完成"不应改写（客户端重连后重复上报是常态）
    status, ack, _ = machine.call(
        "POST", f"/api/agents/transfers/{item_id}/complete",
        {"ok": False, "receivedBytes": 0, "error": "伪造的失败"})
    check("终态后重复上报被忽略（不会把完成改回失败）",
          status == 200 and ack.get("status") == "completed", f"HTTP {status} {ack}")

    status, pending, _ = machine.call("GET", "/api/agents/transfers")
    check("终态条目不再出现在补领列表里", status == 200 and pending == [],
          f"HTTP {status} {pending}")

    # 别人的条目动不了（归属校验）
    other = Client(agent_key + "x")
    status, _, _ = other.call("POST", f"/api/agents/transfers/{item_id}/complete",
                              {"ok": True, "receivedBytes": 1})
    check("伪造的 AgentKey 被拒（401）", status == 401, f"HTTP {status}")

    # ═══════════════════════════════════════════════════════════
    # E. 回传：客户机上传一律落成 collected
    # ═══════════════════════════════════════════════════════════
    print("\n-- E. 回传（客户机上传）--")

    status, task2, _ = admin.call("POST", "/api/distributions/collect", {
        "targetAgentIds": [agent_id],
        "sourcePath": r"C:\somewhere\site-log.txt",
        "note": "冒烟"})
    check("建回传任务成功", status == 200 and bool(task2.get("id")), f"HTTP {status} {task2}")

    status, pending, _ = machine.call("GET", "/api/agents/transfers")
    check("客户机补领到回传任务",
          status == 200 and len(pending) == 1 and pending[0].get("direction") == "collect",
          f"HTTP {status} {pending}")

    if pending:
        collect_item = pending[0].get("itemId")
        body = b"hello from the machine\n"
        status, session, _ = machine.call("POST", "/api/files/uploads", {
            "fileName": "site-log.txt", "size": len(body), "kind": "installer"})
        check("客户机建上传会话成功（不能自称 kind）", status == 200, f"HTTP {status} {session}")
        check("  服务端把 kind 强制成 collected",
              session.get("kind") == "collected", str(session.get("kind")))

        status, up, _ = machine.raw(
            "PUT", f"/api/files/uploads/{session.get('uploadId')}?offset=0",
            data=body, headers={"Content-Type": "application/octet-stream"})
        check("客户机分块上传成功", status == 200, f"HTTP {status} {up}")

        digest = hashlib.sha256(body).hexdigest()
        status, done, _ = machine.call(
            "POST", f"/api/files/uploads/{session.get('uploadId')}/complete", {"sha256": digest})
        check("客户机完成上传成功", status == 200, f"HTTP {status} {done}")

        collected_id = done.get("id")
        status, repo, _ = admin.call("GET", "/api/files?kind=collected")
        found = [x for x in (repo.get("items") or []) if x.get("id") == collected_id]
        check("仓库回传分区里能看到它", status == 200 and len(found) == 1,
              f"HTTP {status}")
        if found:
            check("  记录了来源客户机（页面能显示来自哪台机器）",
                  found[0].get("originAgentId") == agent_id,
                  str(found[0].get("originAgentId")))

        status, ack, _ = machine.call(
            "POST", f"/api/agents/transfers/{collect_item}/complete",
            {"ok": True, "receivedBytes": len(body), "sha256": digest})
        check("回报回传完成成功", status == 200, f"HTTP {status} {ack}")

    # ═══════════════════════════════════════════════════════════
    # F. 真客户端端到端：启动真的 Agent 进程，让它把文件拉下来
    # ═══════════════════════════════════════════════════════════
    agent_end_to_end(admin, file_id, len(payload), sha)

    report()


def agent_end_to_end(admin, file_id, size, sha):
    """
    启动真的 ControlServer.Agent.Service.exe，下发一个文件，等它真的落地。

    这一段才是那批客户端代码的**真凭据**：前面 A–E 段验的是"契约"，
    这里验的是"照契约写出来的那份实现到底行不行"。
    缺 exe 时 SKIP（与 agent-smoke.py 的 D 段同一约定）。
    """
    print("\n-- F. 真客户端端到端（下发落地）--")

    if not os.path.isfile(AGENT_EXE):
        skip("启动真客户端", f"找不到 {os.path.relpath(AGENT_EXE, REPO)}（先 dotnet build）")
        return

    # 2026-10-13 起注册免凭据：客户端不需要任何令牌 / 加入码，指对 ServerUrl 即自动注册上线。
    state_dir = os.path.join(os.environ.get("TEMP", "."), f"cs-transfer-agent-{os.getpid()}")
    shutil.rmtree(state_dir, ignore_errors=True)

    log_path = os.path.join(os.environ.get("TEMP", "."), f"cs-transfer-agent-{os.getpid()}.log")
    log = open(log_path, "wb")

    env = dict(os.environ)
    # apphost 会回退到 Program Files 找运行时，本机没有 —— 必须指到托管 SDK 目录。
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")

    def start_agent(extra_args=None):
        """
        起一个真客户端。**同一状态目录 ⇒ 同一个 AgentId** ——
        所以"重起"只是给它换一份配置，服务端那边仍是同一台机器（见 ⑥ 段）。
        """
        argv = [
            AGENT_EXE,
            f"--Agent:ServerUrl={BASE}",
            f"--Agent:StateDirectory={state_dir}",
            "--Agent:ServiceName=ControlServerAgentTransferSmoke",
            "--Agent:AllowBroadcastDiscovery=false",
        ] + list(extra_args or [])

        print(f"  客户端： {os.path.relpath(AGENT_EXE, REPO)}"
              + (f"   额外参数：{extra_args}" if extra_args else ""))
        print(f"  状态目录：{state_dir}")

        return subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, cwd=REPO, env=env)

    proc = start_agent()

    try:
        # ── ① 它自己注册并握上长连接 ──
        # ⚠️ 在线**列表**（AgentHostDto）里没有 connected 字段（那是 state 字符串），
        #    "真的握上了长连接"只能从**详情**端点拿（AgentDetailDto.Connected）。
        #    只按 state 判会把"注册了但没连上"也算成功。
        def find_candidate():
            status, body, _ = admin.call("GET", "/api/agents")
            if status != 200:
                return None
            hosts = body.get("items") if isinstance(body, dict) else body
            for host in (hosts or []):
                if host.get("machineKey") != "transfer-smoke-0001":
                    return host
            return None

        def is_connected():
            host = find_candidate()
            if not host:
                return None
            status, detail, _ = admin.call("GET", f"/api/agents/{host.get('id')}")
            if status == 200 and detail.get("connected"):
                return detail.get("host") or host
            return None

        found, host = wait_for(is_connected, timeout=60, interval=1.5)
        check("真客户端自动注册并握上长连接（≤60s）", found,
              "客户端日志目录：%s" % os.path.join(state_dir, "logs"))

        if not found:
            return

        agent_id = host.get("id")
        print(f"  客户端 Id={agent_id}（{host.get('displayName')}）")

        # ── ①b 它把本机可写目录 / 可访问盘符**上报**给了服务端 ──
        # 分发页据此给"目标目录 / 源路径"提候选（见 AgentInventoryDto.transfer*Directories）。
        # ⚠️ 两边含义**不对称**：可写目录是白名单（越界会被客户机拒绝，下面的 ②/③ 段验证）；
        #    盘符只是提示，2026-10-15 起回传不限制目录（下面的 ⑤b 段验证）。
        def read_inventory():
            status, env, _ = admin.call("GET", f"/api/agents/{agent_id}/inventory")
            if status != 200:
                return None
            return (env or {}).get("inventory") or None

        got, inv = wait_for(read_inventory, timeout=30, interval=1.0)
        check("客户端把硬件清单上报给了服务端（≤30s）", got, "" if got else "清单未上报")

        if got:
            def norm(p):
                return os.path.normcase(os.path.normpath(p))

            inbox = [norm(d) for d in (inv.get("transferInboxDirectories") or [])]
            outbox = [norm(d) for d in (inv.get("transferOutboxDirectories") or [])]

            want_inbox = norm(os.path.join(state_dir, "inbox"))
            want_outbox = norm(os.path.join(state_dir, "outbox"))

            check("  上报了可写（下发）目录白名单", len(inbox) > 0, str(inbox))
            check("  上报了可访问盘符（回传候选提示，非限制）", len(outbox) > 0, str(outbox))
            check("  默认下发目录 = 状态目录下的 inbox", want_inbox in inbox,
                  "" if want_inbox in inbox else f"{want_inbox} ∉ {inbox}")

            # 🔥 2026-10-15 起：**回传不限制目录**（原来那个可收窄的白名单已连同机制删除）。
            # 客户机仍会报"本机有哪些盘"，但那只是回传页的输入提示 —— 与「浏览」的顶层同一集合。
            # 从前这里断言的是"默认可读范围 = 整盘"，那个默认值已经不存在（没有范围了）。
            drives = {norm(f"{c}:\\") for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
                      if os.path.exists(f"{c}:\\")}
            missing = sorted(d for d in drives if d not in set(outbox))

            check("  上报的盘符 = 本机全部盘符（回传页据此提候选）", not missing,
                  f"缺少：{missing}" if missing else f"{len(drives)} 个盘符")
            check("  盘符提示覆盖状态目录所在的盘", covers(outbox, want_outbox),
                  "" if covers(outbox, want_outbox) else f"{want_outbox} ∉ {outbox}")

        # ── ② 下发（目标目录留空 ⇒ 用客户机的默认目录）──
        status, task, _ = admin.call("POST", "/api/distributions", {
            "fileId": file_id, "targetAgentIds": [agent_id], "targetDir": None})
        check("向真客户端建下发任务成功", status == 200 and bool(task.get("id")),
              f"HTTP {status} {task}")

        if status != 200:
            return

        task_id = task.get("id")

        # ── ③ 文件真的落到客户机的 inbox 里 ──
        # 默认白名单是 {状态目录}/inbox（见 TransferPaths）；文件名沿用仓库里的那个。
        landed = os.path.join(state_dir, "inbox", "smoke-payload.bin")

        def file_ready():
            if not os.path.isfile(landed):
                return False
            try:
                return os.path.getsize(landed) == size
            except OSError:
                return False

        ok, _ = wait_for(file_ready, timeout=90, interval=1.0)
        check("文件在客户机上落地（≤90s）", ok,
              f"客户端日志目录：{os.path.join(state_dir, 'logs')}")
        check("  没有留下 .cs-part 半成品",
              not os.path.exists(landed + ".cs-part"), "暂存文件已被消费")

        if ok:
            check(f"  落地大小 = {size}", os.path.getsize(landed) == size,
                  str(os.path.getsize(landed)))
            check("  落地内容 SHA256 与仓库一致", sha256_file(landed) == sha,
                  sha256_file(landed))

        # ── ④ 服务端那边也应当看到"完成" ──
        def task_done():
            status, detail, _ = admin.call("GET", f"/api/distributions/{task_id}")
            if status != 200:
                return None
            return detail if detail.get("state") == "completed" else None

        done, detail = wait_for(task_done, timeout=30, interval=1.0)
        check("服务端把任务标记为 completed", done,
              str(detail.get("state") if detail else "（未完成）"))

        # ── ⑤ 回传（collect）：客户机上的文件被收上来 ──
        # 2026-10-15 起回传**不限制目录**，放哪儿都该取得到；这里放在 outbox 只是因为
        # 它必然存在、且是"旧默认值"那个目录 —— 顺便证明旧默认也照样能取。
        outbox = os.path.join(state_dir, "outbox")
        os.makedirs(outbox, exist_ok=True)

        collected_payload = ("现场日志 (smoke) " * 300).encode("utf-8")
        collected_sha = hashlib.sha256(collected_payload).hexdigest()
        collected_path = os.path.join(outbox, "field-log.txt")

        with open(collected_path, "wb") as handle:
            handle.write(collected_payload)

        status, ctask, _ = admin.call("POST", "/api/distributions/collect", {
            "targetAgentIds": [agent_id], "sourcePath": collected_path, "note": "冒烟回传"})
        check("向真客户端建回传任务成功", status == 200 and bool(ctask.get("id")),
              f"HTTP {status} {ctask}")

        if status != 200:
            return

        ctask_id = ctask.get("id")

        def collected_in_repo():
            status, repo, _ = admin.call("GET", "/api/files?kind=collected")
            if status != 200:
                return None
            for entry in (repo.get("items") or []):
                if entry.get("name") == "field-log.txt" and entry.get("originAgentId") == agent_id:
                    return entry
            return None

        got, entry = wait_for(collected_in_repo, timeout=90, interval=1.0)
        check("回传文件出现在仓库的回传分区（≤90s）", got,
              f"客户端日志目录：{os.path.join(state_dir, 'logs')}")

        if got:
            check("  回传大小一致", entry.get("size") == len(collected_payload),
                  str(entry.get("size")))
            check("  回传 SHA256 一致", entry.get("sha256") == collected_sha,
                  str(entry.get("sha256")))

        def collect_done():
            status, detail2, _ = admin.call("GET", f"/api/distributions/{ctask_id}")
            if status != 200:
                return None
            return detail2 if detail2.get("state") == "completed" else None

        done2, detail2 = wait_for(collect_done, timeout=30, interval=1.0)
        check("回传任务标记为 completed", done2,
              str(detail2.get("state") if detail2 else "（未完成）"))

        # ── ⑤b 状态目录之外的文件也必须取得回来（2026-10-15 两次修复的落点）──
        # 现场事故：网页上「浏览」能一路点进 D 盘并选中文件，建单后客户机却拒绝
        # （"源路径不在本机白名单目录内"），同一条路径连撞 5 次。
        # 先修成"默认可读整盘"，随后用户要求**连机制一起删掉** —— 现在任意绝对路径都能取。
        # 这里特意挑一个**既不是 outbox 也不是 inbox**的兄弟目录来取。
        plain_dir = os.path.join(state_dir, "plain-not-whitelisted")
        os.makedirs(plain_dir, exist_ok=True)

        plain_payload = ("现场日志 · 旧默认下必然被拒的那种路径 " * 200).encode("utf-8")
        plain_sha = hashlib.sha256(plain_payload).hexdigest()
        plain_path = os.path.join(plain_dir, "field-log-2.txt")

        with open(plain_path, "wb") as handle:
            handle.write(plain_payload)

        status, ptask, _ = admin.call("POST", "/api/distributions/collect", {
            "targetAgentIds": [agent_id], "sourcePath": plain_path, "note": "整盘回传冒烟"})
        check("向真客户端建整盘回传任务成功", status == 200 and bool(ptask.get("id")),
              f"HTTP {status} {ptask}")

        if status == 200:
            ptask_id = ptask.get("id")

            def plain_in_repo():
                status, repo, _ = admin.call("GET", "/api/files?kind=collected")
                if status != 200:
                    return None
                for entry in (repo.get("items") or []):
                    if entry.get("name") == "field-log-2.txt" and entry.get("originAgentId") == agent_id:
                        return entry
                return None

            got2, entry2 = wait_for(plain_in_repo, timeout=90, interval=1.0)
            check("旧默认之外的文件也取得回来（≤90s）", got2,
                  f"客户端日志目录：{os.path.join(state_dir, 'logs')}")

            if got2:
                check("  大小一致", entry2.get("size") == len(plain_payload),
                      str(entry2.get("size")))
                check("  回传 SHA256 一致", entry2.get("sha256") == plain_sha,
                      str(entry2.get("sha256")))

            def plain_done():
                status, detail4, _ = admin.call("GET", f"/api/distributions/{ptask_id}")
                if status != 200:
                    return None
                return detail4 if detail4.get("state") == "completed" else None

            done3, detail5 = wait_for(plain_done, timeout=30, interval=1.0)
            check("整盘回传任务标记为 completed", done3,
                  str(detail5.get("state") if detail5 else "（未完成）"))

        # ── ⑥ 回传"白名单"机制已删除（2026-10-15）──
        #
        # 这里曾经是"换一个配了 Agent:Transfer:OutboxDirectories 的实例重起，断言越界仍被拒"。
        # 用户随后要求**连同机制一起删除**：回传不限制目录，客户机只校验
        # "绝对路径 + 文件真实存在"。所以这一段没有可断言的对象了 ——
        # 留着只会是一条"永远为真"的假绿，直接删掉比改成弱断言诚实。
        #
        # 覆盖"放开之后确实取得到"的那条在上面的 ⑤b（状态目录之外的兄弟目录）与
        # 单测 tests/ControlServer.Agent.Tests/TransferReadScopeTests.cs（含系统目录、别的盘）。
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=15)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass

        log.close()

        # ⚠️ 客户端的自述日志是 NDJSON（落在状态目录里），**不是**被重定向的那个 stdout ——
        #    所以失败时既要留着状态目录，也要把最后几行打出来，否则排查得靠猜。
        if FAIL:
            print(f"  客户端默认输出（stdout/stderr）：{log_path}")
            print(f"  客户端结构化日志：{os.path.join(state_dir, 'logs')}")
            dump_agent_log(state_dir)
        else:
            shutil.rmtree(state_dir, ignore_errors=True)
            try:
                os.remove(log_path)
            except OSError:
                pass


def dump_agent_log(state_dir, lines=30):
    """把客户端最新的 NDJSON 日志尾部打出来（失败时用，省掉一次手工翻目录）。"""
    log_dir = os.path.join(state_dir, "logs")

    if not os.path.isdir(log_dir):
        print("    （没有日志目录 —— 客户端可能根本没起来）")
        return

    candidates = [
        os.path.join(log_dir, name) for name in os.listdir(log_dir) if name.endswith(".ndjson")
    ]

    if not candidates:
        print("    （日志目录是空的）")
        return

    newest = max(candidates, key=os.path.getmtime)
    print(f"    ── {os.path.basename(newest)} 尾部 ──")

    try:
        with open(newest, "r", encoding="utf-8", errors="replace") as handle:
            tail = handle.readlines()[-lines:]
    except OSError as error:
        print(f"    （读日志失败：{error}）")
        return

    for line in tail:
        print("    " + line.rstrip())


def report():
    print(f"\n== 结果：{len(PASS)} 通过 / {len(FAIL)} 失败 ==")
    if FAIL:
        for name in FAIL:
            print(f"  失败：{name}")
        sys.exit(1)
    sys.exit(0)


if __name__ == "__main__":
    main()
