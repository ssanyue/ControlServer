#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""双向配置同步端到端冒烟（M4.5 · 2026-10-13，#219 → #221 并入冒烟）。

验的是"两边都能改、谁后写谁生效"这条链路的**真机行为**：
  ① 客户机注册上线；
  ② **本机页面**改一版 → 自动推给中心（中心版本要对，内容要一致）；
  ③ **中心页面**（走 HTTP API）改一版 → 客户机自动采纳；
  ④ 中心回的版本号比"本机以为的"高一格时，客户机要对齐到中心的数字；
  ⑤ **无中心独立使用**：可编辑、可持久化、「待同步」标志跨重启存活。

⚠️ **中心由 `run-all.py` 起好**，本脚本只负责客户机那一侧（这是它与其它冒烟脚本的
统一分工）。单独跑也可以，但要自己先把中心起在同一个 base_url 上。

用法（由 run-all.py 调用）：
    python deploy/smoke/sync-e2e.py <base_url> <work_dir> <agent_ui_port> <standalone_ui_port>

客户机 exe：`CS_AGENT_EXE` 环境变量（打包产物），否则退回本仓库的调试构建。
"""

import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from http.cookiejar import CookieJar

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# ⚠️ 与 agent-smoke.py 同一套约定：可用环境变量指向**打包产物**（安装目录里那个），
#    否则退回落在本仓库的调试构建。
AGENT_EXE = os.environ.get("CS_AGENT_EXE") or os.path.join(
    REPO, "agents", "ControlServer.Agent.Service", "bin", "Debug", "net8.0",
    "ControlServer.Agent.Service.exe")

FAILURES = []


def check(name, ok, detail=""):
    print(("  [PASS] " if ok else "  [FAIL] ") + name + ("   " + detail if detail else ""))
    if not ok:
        FAILURES.append(name + " :: " + detail)


def http(method, url, body=None, opener=None, headers=None, timeout=10):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    send = opener.open if opener is not None else urllib.request.urlopen
    try:
        with send(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", "replace")
            return r.status, (json.loads(raw) if raw.strip().startswith(("{", "[")) else raw)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, raw


def wait_for(fn, seconds, what):
    deadline = time.time() + seconds
    last = None
    while time.time() < deadline:
        try:
            last = fn()
            if last:
                return last
        except Exception as e:  # noqa: BLE001
            last = e
        time.sleep(0.4)
    print(f"  （等待超时：{what}，最后一次 = {last}）")
    return None


def env_for_agent():
    env = dict(os.environ)
    # 调试构建的 apphost 需要 DOTNET_ROOT 才不会回退去找 Program Files 里的运行时。
    # 打包产物自带运行时，这个变量对它无害。
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")
    return env


def standalone_check(work_dir, ui_port):
    """⑤ 「客户端本身可独立使用」：完全不起中心，客户机自己就能编辑、生效、重启后还在。

    与 ①–④ 的分工：那几步验的是"连上中心之后怎么同步"，这一步验的是
    **没有中心时的能力**（离线编辑 + 降级运行 + 跨重启持久化）——
    需求原话是"客户端本身可独立使用"，少了这一步就只验了一半。

    注：run-all 起的那个中心此刻还在，但这台客户机既**没配 ServerUrl**、
    又**关掉了广播发现** ⇒ 它一个中心都找不到，与"世上没有中心"等价。
    """
    print("== ⑤ 独立使用（无中心）→ 可编辑、可持久化 ==")

    agent_dir = os.path.join(work_dir, "standalone")
    env = env_for_agent()

    def start_agent():
        return subprocess.Popen(
            [
                AGENT_EXE,
                f"--Agent:StateDirectory={agent_dir}",
                # 既不配 ServerUrl、又关掉广播发现 ⇒ 一个中心都找不到（这才是真正的"独立使用"）。
                "--Agent:AllowBroadcastDiscovery=false",
                f"--Agent:LocalUi:Port={ui_port}",
                "--Agent:Telemetry:Enabled=false",
                # 🔥 UDP 指令通道走**演练模式**（2026-10-14 补）：
                #    这台客户机默认就会在 8090 上听着（未连接中心时更是**强制**听着），
                #    而本机若恰好有人朝 8090 发一条明文 DOWN，跑冒烟的这台机器就真被关掉了。
                #    本脚本一个字都不验 UDP 执行，所以这个保险没有任何代价。
                "--Agent:Tasks:ExecuteUdpCommands=false",
            ],
            env=env, cwd=REPO,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )

    local = f"http://127.0.0.1:{ui_port}"
    proc = start_agent()

    def local_up():
        try:
            st, _ = http("GET", local + "/api/config")
            return st == 200
        except Exception:
            return False

    try:
        check("本机配置页就绪（无中心）", wait_for(local_up, 30, "本机配置页（无中心）") is True)

        st, page = http("GET", local + "/")
        token = page.split("const TOKEN = '")[1].split("'")[0]

        # 三张清单各放一条：延时/循环（sendTasks）、定时（scheduledTasks）、延时启动（launchTasks）。
        program_path = r"D:\Player\player.exe"

        st, saved = http(
            "PUT", local + "/api/config",
            {
                "udp": {"enabled": True, "port": 8090, "blockedSourceIps": []},
                "sendTasks": [{
                    "id": "s1", "name": "循环报数", "protocol": "UDP", "targetIp": "192.168.1.100",
                    "targetPort": 9000, "dataType": "String", "data": "hello",
                    "delaySeconds": 3, "isLoop": True, "loopIntervalSeconds": 30, "enabled": True,
                }],
                "scheduledTasks": [{
                    "id": "t1", "name": "每天八点", "protocol": "UDP", "targetIp": "10.0.0.9",
                    "targetPort": 5000, "dataType": "String", "data": "tick",
                    "times": ["08:00", "12:30"], "enabled": True,
                }],
                "launchTasks": [{
                    "id": "l1", "name": "开机启动", "programPath": program_path,
                    "argument": "--fullscreen", "delaySeconds": 15, "enabled": True,
                }],
            },
            headers={"X-CS-Local-Token": token},
        )
        check("无中心时本机保存成功", st == 200 and saved.get("ok"), json.dumps(saved, ensure_ascii=False)[:160])

        st, state = http("GET", local + "/api/config")
        check("来源标记 = 本机", state.get("origin") == "local", str(state.get("origin")))
        check("本机版本 = 1", state.get("version") == 1, str(state.get("version")))
        check("保留「待同步」标志（离线改的，早晚要推给中心）", state.get("unsynced") is True, str(state.get("unsynced")))

        c = state["config"]
        check("循环发送已在本机生效", len(c["sendTasks"]) == 1 and c["sendTasks"][0]["isLoop"] is True)
        check("定时任务已在本机生效（两个时刻）",
              len(c["scheduledTasks"]) == 1 and c["scheduledTasks"][0]["times"] == ["08:00", "12:30"],
              json.dumps(c["scheduledTasks"][0].get("times"), ensure_ascii=False))
        check("延时启动已在本机生效", len(c["launchTasks"]) == 1)
        check("UDP 通道默认端口 8090", c["udp"]["port"] == 8090, str(c["udp"]["port"]))
        check("反斜杠路径原样保留", c["launchTasks"][0]["programPath"] == program_path,
              c["launchTasks"][0]["programPath"])

        # ── 重启客户机：配置必须从本机文件恢复，且"待同步"也得跟着活过来 ──
        print("  —— 重启客户机（仍无中心）：验证配置与待同步标志都跨重启存活 ——")
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except Exception:
            proc.kill()
        proc = start_agent()

        def restored():
            try:
                st, s = http("GET", local + "/api/config")
                if st == 200 and s.get("version") == 1 and len(s["config"]["scheduledTasks"]) == 1:
                    return s
            except Exception:
                pass
            return None

        back = wait_for(restored, 40, "重启后配置恢复")
        check("重启后配置仍在（来自本机文件）", back is not None)
        if back:
            check("恢复的定时任务名一致", back["config"]["scheduledTasks"][0]["name"] == "每天八点")
            check("恢复的循环发送仍在", back["config"]["sendTasks"][0]["name"] == "循环报数")
            # 🔥 丢掉这个标志 = "离线改的那一版永远不会被推上去"，且两边都不报错。
            check("重启后「待同步」标志仍在", back.get("unsynced") is True, str(back.get("unsynced")))
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=10)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
        print("  已停止客户机（独立）")


def main(argv):
    if len(argv) < 4:
        print("[X] 用法：sync-e2e.py <base_url> <work_dir> <agent_ui_port> <standalone_ui_port>")
        return 2

    base = argv[0]
    work_dir = argv[1]
    agent_ui_port = int(argv[2])
    standalone_ui_port = int(argv[3])

    if not os.path.isfile(AGENT_EXE):
        print(f"[X] 找不到客户机 exe：{AGENT_EXE}")
        print("  先 dotnet build，或设 CS_AGENT_EXE 指向打包产物。")
        return 2

    if os.path.isdir(work_dir):
        shutil.rmtree(work_dir, ignore_errors=True)
    os.makedirs(work_dir, exist_ok=True)

    agent_dir = os.path.join(work_dir, "agent")
    env = env_for_agent()

    print("== 启动客户机（中心由 run-all 起好）==")
    agent = subprocess.Popen(
        [
            AGENT_EXE,
            f"--Agent:StateDirectory={agent_dir}",
            f"--Agent:ServerUrl={base}",
            f"--Agent:LocalUi:Port={agent_ui_port}",
            "--Agent:Telemetry:Enabled=false",
            # 🔥 同 standalone_check：这台客户机默认也在 8090 上听着，
            #    演练模式是"绝不真关机"那条铁律的唯一保险。
            "--Agent:Tasks:ExecuteUdpCommands=false",
        ],
        env=env, cwd=REPO,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )

    local = f"http://127.0.0.1:{agent_ui_port}"

    try:
        # 等中心起来（run-all 已经等过首页；这里再确认一次 API 就绪，避免竞态）。
        wait_for(lambda: http("GET", base + "/api/agents/1/config")[0] in (401, 403, 200), 40, "中心 HTTP 就绪")

        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(CookieJar()))
        st, _ = http("POST", base + "/api/auth/login", {"userName": "admin", "password": "admin123"}, opener=opener)
        check("管理员登录", st == 200, f"HTTP {st}")

        # 等客户机注册上线
        def find_host():
            st, body = http("GET", base + "/api/agents", opener=opener)
            if st == 200 and isinstance(body, dict):
                items = body.get("items") or []
                if items:
                    return items[0]
            return None

        host = wait_for(find_host, 60, "客户机注册上线")
        check("客户机已注册", host is not None)
        if host is None:
            return 1
        agent_id = host["id"]
        print(f"  AgentId = {agent_id}")

        # 等本机配置页起来
        def local_up():
            try:
                st, _ = http("GET", local + "/api/config")
                return st == 200
            except Exception:
                return False

        check("本机配置页就绪", wait_for(local_up, 30, "本机配置页") is True)

        st, page = http("GET", local + "/")
        token = page.split("const TOKEN = '")[1].split("'")[0]
        check("页面带本机令牌", len(token) == 32, token[:8] + "…")

        st, state = http("GET", local + "/api/config")
        check("本机初始无配置（版本 0）", st == 200 and state["version"] == 0, json.dumps(state, ensure_ascii=False)[:120])
        check("本机初始无待同步", state["unsynced"] is False)

        # ── ② 本机页面改一版 ────────────────────────────────────────────
        print("== ② 本机页面保存 → 应自动推给中心 ==")
        st, saved = http(
            "PUT", local + "/api/config",
            {
                "udp": {"enabled": True, "port": 8095, "blockedSourceIps": ["192.168.1.5"]},
                "sendTasks": [{
                    "id": "s1", "name": "开机报数", "protocol": "UDP", "targetIp": "192.168.1.100",
                    "targetPort": 9000, "dataType": "String", "data": "hello",
                    "delaySeconds": 3, "isLoop": True, "loopIntervalSeconds": 30, "enabled": True,
                }],
                "scheduledTasks": [],
                "launchTasks": [],
            },
            headers={"X-CS-Local-Token": token},
        )
        check("本机保存成功（本机 v1）", st == 200 and saved.get("ok") and saved["config"]["version"] == 1,
              json.dumps(saved, ensure_ascii=False)[:160])

        def server_version(expected_min):
            st, body = http("GET", base + f"/api/agents/{agent_id}/config", opener=opener)
            if st == 200 and body.get("version", 0) >= expected_min:
                return body
            return None

        # ⚠️ 中心落库的版本号**永远是"中心那份 + 1"**，不采用客户机送来的数字
        #    （见 AgentConfigService.PushFromAgentAsync）。本例中心起手是空的（v0），
        #    所以本机推上来的 v1 落成中心 v1 —— 不是 v2。
        pushed = wait_for(lambda: server_version(1), 20, "本机改动被推到中心")
        check("中心已收到本机改动（中心 v1）", pushed is not None)
        if pushed:
            check("中心那份的版本 = 1（中心自己的水位 +1）", pushed["version"] == 1, str(pushed["version"]))
            check("中心那份的 UDP 端口 = 8095", pushed["udp"]["port"] == 8095, str(pushed["udp"]["port"]))
            check("中心那份的发送任务数 = 1", len(pushed["sendTasks"]) == 1)
            check("中心那份的任务名一致", pushed["sendTasks"][0]["name"] == "开机报数")

        def local_unsynced_false():
            st, s = http("GET", local + "/api/config")
            return s if (st == 200 and s["unsynced"] is False and s["version"] >= 1) else None

        settled = wait_for(local_unsynced_false, 15, "本机标志落定")
        check("本机已回到「无待同步」", settled is not None,
              json.dumps(settled, ensure_ascii=False)[:140] if settled else "")

        # ── ③ 中心页面改一版 → 客户机应采纳 ─────────────────────────────
        print("== ③ 中心改配置 → 客户机应自动采纳 ==")
        st, res = http(
            "PUT", base + f"/api/agents/{agent_id}/config",
            {"udp": {"enabled": True, "port": 8097, "blockedSourceIps": []},
             "sendTasks": [{
                 "id": "s1", "name": "中心改过", "protocol": "TCP", "targetIp": "10.1.1.1",
                 "targetPort": 7001, "dataType": "Hex", "data": "AABB",
                 "delaySeconds": 0, "isLoop": False, "loopIntervalSeconds": 60, "enabled": True,
             }]},
            opener=opener,
        )
        check("中心保存成功", st == 200, f"HTTP {st}")
        server_now = res["config"]["version"] if isinstance(res, dict) and "config" in res else None
        print(f"  中心版本 = v{server_now}")

        def local_adopted():
            st, s = http("GET", local + "/api/config")
            if st != 200 or s["version"] != server_now:
                return None
            return s

        adopted = wait_for(local_adopted, 25, "客户机采纳中心那份")
        check("客户机版本已对齐中心", adopted is not None,
              json.dumps(adopted, ensure_ascii=False)[:140] if adopted else "")
        if adopted:
            c = adopted["config"]
            check("客户机 UDP 端口 = 8097", c["udp"]["port"] == 8097, str(c["udp"]["port"]))
            check("客户机任务已被中心覆盖", c["sendTasks"][0]["name"] == "中心改过", c["sendTasks"][0]["name"])
            check("客户机来源标记 = 中心", adopted["origin"] == "server", adopted["origin"])

        # ── ④ 中心已应用版本对齐 ────────────────────────────────────────
        # ⚠️ GET /api/agents/{id} 返回的是 AgentDetailDto，形状是
        #    { host: AgentHostDto, info, connected } ——
        #    appliedConfigVersion 在 **host 里**，不在顶层。
        def applied_ok():
            st, body = http("GET", base + f"/api/agents/{agent_id}", opener=opener)
            if st != 200 or not isinstance(body, dict):
                return None
            host = body.get("host") or {}
            if host.get("appliedConfigVersion") == server_now:
                return body
            return None

        applied = wait_for(applied_ok, 25, "中心看到「已应用」")
        detail_host = (applied or {}).get("host", {}) if applied else None
        check("中心页面显示「已应用」= 当前版本", applied is not None,
              f"appliedConfigVersion={detail_host.get('appliedConfigVersion') if detail_host else '（未读到）'}")
        if applied:
            check("中心「已下发」版本 = 当前版本", detail_host.get("configVersion") == server_now,
                  str(detail_host.get("configVersion")))

    finally:
        try:
            agent.terminate()
            agent.wait(timeout=10)
        except Exception:
            try:
                agent.kill()
            except Exception:
                pass
        print("  已停止客户机")

    # ── ⑤ 独立使用（无中心）────────────────────────────────────────────
    try:
        standalone_check(work_dir, standalone_ui_port)
    except Exception as exc:  # noqa: BLE001
        check("独立使用阶段未抛异常", False, repr(exc))

    print()
    if FAILURES:
        print(f"结果：{len(FAILURES)} 项失败")
        for f in FAILURES:
            print("  - " + f)
        return 1

    print("结果：全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
