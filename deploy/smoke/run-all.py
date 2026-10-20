#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · 冒烟总入口（M2-G8）

一条命令跑完全部冒烟，并且**每个脚本都对着一个全新实例**。

为什么必须"全新实例"
    六个脚本对初始状态的假设不同，混在同一个实例上跑会互相污染：
      · web-console-smoke 断言「配置为空、version=0」—— 跑过一次就不成立了
      · admin-api-smoke   断言「指令库为空」—— 上一个脚本建过指令就会重名
      · pairing-smoke     直接往 SQLite 里写配置 —— 要求库表已建好（即服务得先起来）
      · agent-smoke       断言「在线表为空、无安装包、无令牌」—— 且会启动真的客户端进程
                          （M4.5 起还会**给它下发一份启用 UDP 指令通道的配置**，
                           跑完再关掉；客户端以 `ExecuteUdpCommands=false` 演练模式启动，
                           绝不可能把跑冒烟的这台机器关掉。
                           G 段还会再起一台**找不到中心**的客户机 —— 同样带演练模式开关，
                           因为"未连接就必须监听"这条规则让它一定会绑着 8090。
                           H 段（2026-10-14）再起一台真客户机并**把它从服务端移除**，
                           看它能不能自己重新注册回来 —— 同样带演练模式开关）
      · license-smoke     要求「无授权文件、无试用记录」且档位为 Enforce
      · rbac-smoke        断言「出厂管理员直接可用、且不再被强制改密」
    所以这里对每个脚本：**开新数据目录 + 新端口 → 起 exe → 等就绪 → 跑 → 收摊**。

关于强制改密闸门（M3）
    2026-10-09 起产品**取消了"首次使用强制改密"**：出厂管理员、新建账号、
    管理员重置密码都不再被标记 `MustChangePassword`，且闸门默认关闭 ——
    因此服务端开箱即用，本脚本**无需**再为任何场景关闸门
    （过去要传 `--Server:Auth:EnforcePasswordChange=false`，现在那纯属多余）。
    闸门机制本身仍在：显式置 `--Server:Auth:EnforcePasswordChange=true` 并手工标记账号后照常拦截，
    由 `Server.IntegrationTests/UserRbacTests.ForcedPasswordChangeTests` 覆盖。

用法：
    python deploy/smoke/run-all.py                      # 默认跑 dist/win-x64 的打包 exe
    python deploy/smoke/run-all.py <exe路径>            # 指定被测程序（如 bin/Debug 的开发产物）
    python deploy/smoke/run-all.py --only admin         # 只跑一个（名字见下）

覆盖（每个脚本一个独立实例）：
    web       Web 控制台配置导入导出往返（web-console-smoke.py）
    pairing   对接码三级匹配 + 真 UDP 收发（pairing-smoke.py）
    admin     管理面 G1/G6/G7 + 单条动作执行 G9（admin-api-smoke.py）
    agent     客户端Server：安装包下载 / 免凭据准入 / 在线表 / 真客户端端到端 /
              离线客户端独立可用（2026-10-14 新增 G 段）/
              **移除之后加得回来**（2026-10-14 新增 H 段）（agent-smoke.py）
    transfer  文件分发契约（M4.4）：分块上传 / 401-409 语义 / Range 续传 / 任务汇总（transfer-smoke.py）
    sync      客户端配置双向同步 + 无中心独立使用（M4.5）（sync-e2e.py）
    browse    客户机目录浏览：选"存放路径 / 回传源路径"那条链路（M4.4+）（browse-e2e.py）
    license   设备授权闸门（Enforce 下 403 矩阵 / 真 UDP 停摆与恢复 / 验签拦截）（license-smoke.py）
    rbac      三角色权限矩阵 + 账号生命周期（rbac-smoke.py）

退出码：全部通过 0，否则 1。
"""

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
PY = sys.executable

_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))  # 绕过本机代理


def free_port(kind):
    """要一个当前空闲的端口号（先绑定再释放；只用回环，避免暴露在外网）。"""
    s = socket.socket(socket.AF_INET, kind)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def wait_ready(base_url, proc, timeout=45.0):
    """轮询首页直到 200；进程若先退出则立刻返回 False（附日志路径）。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        try:
            with _opener.open(base_url + "/", timeout=3) as r:
                if r.status == 200:
                    return True
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(0.4)
    return False


def run_scenario(name, title, script, build_args, extra_ports=0, extra_args=None):
    """
    起一个全新实例 → 跑一个冒烟脚本 → 收摊。
    build_args(ctx) 返回传给脚本的命令行参数；ctx 里有 base_url / data_dir / http / pairing / ports。
    extra_args 是追加给被测程序的额外 `--Server:…` 参数（如授权档位）。
    """
    print()
    print("═" * 72)
    print(f"  {name} · {title}")
    print("═" * 72)

    http_port = free_port(socket.SOCK_STREAM)
    pairing_port = free_port(socket.SOCK_DGRAM)

    # 发现端口（M4）也单独要一个。
    # ⚠️ 不能沿用默认的 9998：本机上可能已经有真的服务端在听它（开发时很常见），
    #    撞上不会让实例启动失败（发现监听失败只记日志），但会让 agent 场景的
    #    UDP 发现断言打到一个**别人的**进程上，得到一个莫名其妙的结果。
    discovery_port = free_port(socket.SOCK_DGRAM)
    while discovery_port == pairing_port:
        discovery_port = free_port(socket.SOCK_DGRAM)

    ports = [free_port(socket.SOCK_DGRAM) for _ in range(extra_ports)]
    while any(p in (pairing_port, discovery_port) for p in ports):
        ports = [free_port(socket.SOCK_DGRAM) for _ in range(extra_ports)]

    data_dir = os.path.join(tempfile.gettempdir(), f"cs-smoke-{name}-{os.getpid()}")
    shutil.rmtree(data_dir, ignore_errors=True)

    # 客户机的**本机配置页**是个 HTTP 服务 ⇒ 要 TCP 端口，且要两个
    # （sync 场景除了"连着中心那台"，还要一台"谁都不连"的用于验证独立使用）。
    agent_ui = free_port(socket.SOCK_STREAM)
    standalone_ui = free_port(socket.SOCK_STREAM)
    while standalone_ui in (agent_ui, http_port):
        standalone_ui = free_port(socket.SOCK_STREAM)

    # 需要落到磁盘的临时目录（客户机状态目录 / 白名单目录都在里面）。
    work_dir = os.path.join(tempfile.gettempdir(), f"cs-smoke-{name}-work-{os.getpid()}")
    shutil.rmtree(work_dir, ignore_errors=True)

    base_url = f"http://127.0.0.1:{http_port}"
    ctx = {
        "base_url": base_url,
        "data_dir": data_dir,
        "work_dir": work_dir,
        "http": http_port,
        "pairing": pairing_port,
        "discovery": discovery_port,
        "agent_ui": agent_ui,
        "standalone_ui": standalone_ui,
        "ports": ports,
    }

    log_path = os.path.join(tempfile.gettempdir(), f"cs-smoke-{name}-{os.getpid()}.log")
    log = open(log_path, "wb")

    argv = [
        EXE,
        f"--Server:Storage:DataDir={data_dir}",
        f"--Server:Http:Urls={base_url}",
        f"--Server:Listener:PairingPort={pairing_port}",
        f"--Server:Discovery:Port={discovery_port}",
        "--Server:Startup:AutoOpenBrowser=false",
        # 关托盘：这是 4 个纯 API 场景，不需要 UI；否则每跑一个场景都会在
        # 使用者桌面右下角闪出/消失一个托盘图标（4 次噪音）。
        # 托盘本身的显示/摘除由 Server.IntegrationTests/TrayIconTests 覆盖。
        "--Server:Tray:Enabled=false",
        # 关单实例：互斥体名是固定的 Local\ControlServer.Server.SingleInstance，
        # 4 个场景虽串行、但"上一个刚 terminate、下一个就起"存在竞态窗口 ——
        # 一旦撞上，实例会判定"已有实例在运行"直接退出，冒烟报"未就绪"这种假故障。
        "--Server:Startup:SingleInstance=false",
    ]

    if extra_args:
        argv.extend(extra_args)

    print(f"  实例： {EXE}")
    print(f"  数据目录：{data_dir}")
    print(f"  端口：HTTP {http_port} / 对码 {pairing_port} / 发现 {discovery_port}"
          + (f" / 附加 {ports}" if ports else ""))
    print(f"  本机页端口：{agent_ui}（独立模式 {standalone_ui}）")
    if extra_args:
        print(f"  额外参数：{' '.join(extra_args)}")

    proc = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, cwd=REPO)

    ok = False
    try:
        if not wait_ready(base_url, proc):
            print(f"  [X] 实例未能在超时内就绪。启动日志：{log_path}")
            return False

        print(f"  → 就绪，运行 {os.path.basename(script)}")
        print()
        result = subprocess.run(
            [PY, os.path.join(HERE, script)] + build_args(ctx),
            cwd=REPO,
            env=CHILD_ENV,
        )
        ok = result.returncode == 0
        return ok
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=10)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
        log.close()
        shutil.rmtree(data_dir, ignore_errors=True)
        shutil.rmtree(work_dir, ignore_errors=True)

        # 启动日志：**成功即删**。否则每跑一次 %TEMP% 就多 4 个 cs-smoke-*.log，
        # 名字里带 PID 所以永不覆盖，只增不减（2026-10-08 与 cs-noop-<guid>
        # 一起清出来的同类遗留）。失败时保留并打印路径 —— 那时它恰恰最有用。
        if ok:
            try:
                os.remove(log_path)
            except OSError:
                pass
        else:
            print(f"  启动日志已保留（便于排查）：{log_path}")


# ═══════════════════════════════════════════════════════════════

argv = sys.argv[1:]
EXE = os.path.join(REPO, "dist", "win-x64", "ControlServer.Server.exe")
only = None

if "--only" in argv:
    i = argv.index("--only")
    only = argv[i + 1]
    del argv[i:i + 2]
if argv:
    EXE = os.path.abspath(argv[0])

if not os.path.isfile(EXE):
    print(f"[X] 找不到被测程序：{EXE}")
    print("  先跑：powershell -NoProfile -ExecutionPolicy Bypass -File deploy\\windows\\build-portable-exe.ps1")
    sys.exit(2)


def _resolve_agent_exe():
    """
    客户机 exe —— sync / browse / agent 三个场景要用它（它们各自起一台真客户端）。

    解析顺序：环境变量 → 被测程序旁边 → **与被测服务端同源**的那一份。
    找不到不算错误：那几个场景会自己报"找不到客户机 exe"并跳过；其余场景不受影响。

    🔥 **"同源"这一条是 2026-10-14 补的**。
    在此之前，拿 `--artifacts-path .artifacts-check` 那份**服务端**跑冒烟时，
    客户机却会一路落到 `dist/agent/`（上一次打包的产物）上 —— 于是：
    服务端测的是**现在的代码**，客户机测的是**上一次打包的代码**，
    结果是四条与客户端能力有关的断言（显卡型号 / 温度默认开 / UDP 实况上报）**全红**，
    而它们红得毫无道理：那几处代码早就改了，只是没进这个包。
    ⚠️ 这种"假 FAIL"比不做断言更坏：它会让人去改一段本来就是对的代码。

    🔥 **2026-10-16 补完**：光有"排在 dist/agent 前面"还不够 ——
    被测服务端是**仓库里的 `bin/Debug`**（最常见的一种跑法）时，`sibling` 那条
    相对路径指到 `src\\ControlServer.Agent.Service\\debug\\`（**不存在**），
    于是照样一路落到 `dist/agent` 上。后果是**反向**的假象：
    服务端是**现在的代码**、客户机是**上一次打包的代码**，
    而打包产物上次是什么时候出的没人记得 —— 于是"代码早就修好了、发出去的包还是旧的"
    这种事故在冒烟里表现为**全绿**（客户机那边的断言根本没测到新代码）。
    ⇒ 现在按**被测服务端住在哪**决定先看谁：
      · 服务端在仓库 `dist\\` 下 ⇒ 它要配的是**同一批交付产物**（`dist\\agent`）打头；
      · 否则（`bin\\Debug` / `.artifacts-check` …）⇒ **同一次构建的调试产物**打头。
    两边都保留为候选，所以"只有一个存在"时行为不变。
    """
    server_dir = os.path.dirname(os.path.abspath(EXE))

    # `.artifacts-check/bin/ControlServer.Server/debug/` → `.artifacts-check/bin/ControlServer.Agent.Service/debug/`
    # （`dist/win-x64/` 里没有这个兄弟目录，会自动落到下面的候选上）
    sibling = os.path.abspath(
        os.path.join(server_dir, "..", "..", "ControlServer.Agent.Service", "debug",
                     "ControlServer.Agent.Service.exe"))

    packaged = os.path.join(REPO, "dist", "agent", "ControlServer.Agent.Service.exe")
    repo_debug = os.path.join(REPO, "agents", "ControlServer.Agent.Service", "bin", "Debug", "net8.0",
                              "ControlServer.Agent.Service.exe")

    dist_root = os.path.join(REPO, "dist") + os.sep
    testing_packaged_server = os.path.abspath(EXE).startswith(dist_root)

    same_source = (packaged, repo_debug) if testing_packaged_server else (repo_debug, packaged)

    for c in (
        os.environ.get("CS_AGENT_EXE"),
        os.path.join(server_dir, "ControlServer.Agent.Service.exe"),
        sibling,
        *same_source,
    ):
        if c and os.path.isfile(c):
            return os.path.abspath(c)
    return None


AGENT_EXE = _resolve_agent_exe()

# 传给子脚本的环境：统一告诉它们客户机 exe 在哪（与 agent-smoke.py 的既有约定一致）。
CHILD_ENV = dict(os.environ)
if AGENT_EXE:
    CHILD_ENV.setdefault("CS_AGENT_EXE", AGENT_EXE)

SCENARIOS = [
    (
        "web",
        "Web 控制台配置导入导出往返",
        "web-console-smoke.py",
        lambda c: [c["base_url"], str(c["pairing"])],
        0,
    ),
    (
        "pairing",
        "对接码三级匹配 + 真 UDP 收发",
        "pairing-smoke.py",
        lambda c: [
            os.path.join(c["data_dir"], "control.db"),
            str(c["pairing"]),
            str(c["ports"][0]),
            str(c["ports"][1]),
        ],
        2,
    ),
    (
        "admin",
        "管理面 G1 / G6 / G7 / G9 端点",
        "admin-api-smoke.py",
        lambda c: [c["base_url"], str(c["ports"][0])],
        1,
    ),
    # agent 场景（M4）：
    #   · 把心跳/阈值**调快**，只为把"心跳 → 在线 → 断开 → 离线"这条链路在
    #     一次冒烟里跑完。默认值（5s / 15s / 30s→实际 10/30/75）意味着要等 90 秒，
    #     机制完全一样、只是慢。默认阈值本身的正确性由 Core 单测钉住
    #     （AgentProtocolAndPresenceTests）。这里验的是**机制**，不是**标定**。
    #   · 离线阈值必须 > 不稳定阈值，否则"不稳定"永远观察不到。
    (
        "agent",
        "客户端Server：安装包下载 / 免凭据准入 / 在线表 / 真客户端端到端",
        "agent-smoke.py",
        lambda c: [c["base_url"], str(c["discovery"]), c["data_dir"]],
        0,
        [
            "--Server:Agent:HeartbeatSeconds=5",
            "--Server:Agent:StaleAfterSeconds=15",
            "--Server:Agent:OfflineAfterSeconds=30",
            # M4.2 遥测：默认 5 秒采样 / 30 秒落盘 / 30 秒轮询事件日志，
            # 一次冒烟要为此多等 1 分多钟。调快只影响**节奏**，链路一模一样；
            # 默认值本身的正确性由 Core / 集成测试钉住（AgentMetricsAggregatorTests、
            # AgentTelemetryApiTests）。这里验的是"三段遥测能不能端到端跑通"。
            "--Server:Agent:Telemetry:MetricsSeconds=2",
            "--Server:Agent:Telemetry:FlushSeconds=2",
            "--Server:Agent:Telemetry:PowerEventPollSeconds=5",
            "--Server:Agent:Telemetry:NotifyThrottleSeconds=1",
        ],
    ),
    # 文件分发契约（M4.4）：不启真客户端进程，用"假装成客户机的 HTTP 调用"
    # 把 FileTransferClient 依赖的那份契约钉住（身份 / 分块 / 409 对齐 /
    # Range 206 / 任务汇总 / 终态幂等）。真客户端的字节搬运由 agent 场景的
    # D 段与 .tools/transfer-resume.py（断点续传）另行覆盖。
    (
        "transfer",
        "文件分发契约：分块上传 / Range 续传 / 任务汇总",
        "transfer-smoke.py",
        lambda c: [c["base_url"], c["data_dir"]],
        0,
    ),
    # ── 下面两个场景各自还要**起一台真客户端**（中心仍由本脚本提供）──
    # 客户端配置双向同步（M4.5）：本机页面改 → 推给中心；中心改 → 客户机采纳；
    # 外加"无中心也能独立使用"（离线编辑 + 跨重启持久化）。
    (
        "sync",
        "客户端配置双向同步 + 无中心独立使用",
        "sync-e2e.py",
        lambda c: [c["base_url"], c["work_dir"], str(c["agent_ui"]), str(c["standalone_ui"])],
        0,
    ),
    # 客户机目录浏览（M4.4+）：选存放路径 / 回传源路径那条链路（盘符 / 整盘 / notdir / 离线）。
    (
        "browse",
        "客户机目录浏览（选路径，整盘）",
        "browse-e2e.py",
        lambda c: [c["base_url"], c["work_dir"], str(c["agent_ui"])],
        0,
    ),
    # license 场景要求"无授权 + 无试用记录 + 档位 Enforce"：
    #   · AutoTrial=false —— 否则首启会自动生成 30 天试用，设备一上来就是"可用"，
    #     闸门根本没有机会被观察到（测的就是"没授权会怎样"）；
    #   · Enforcement=Enforce —— 默认档次是 Warn（升级不锁死），不显式抬到 Enforce
    #     就只会得到"放行 + 前端横幅"，与这里要验的拦截行为无关。
    (
        "license",
        "设备授权闸门（403 矩阵 / 真 UDP 停摆与恢复 / 验签拦截）",
        "license-smoke.py",
        lambda c: [c["base_url"], str(c["pairing"]), str(c["ports"][0])],
        1,
        ["--Server:License:Enforcement=Enforce", "--Server:License:AutoTrial=false"],
    ),
    # rbac 必须最后一个：它会把 admin 的密码从 admin123 改掉。
    # （每个场景本来就用全新数据目录，这里只是让"万一复用实例"也不出事。）
    (
        "rbac",
        "三角色权限矩阵 + 账号生命周期",
        "rbac-smoke.py",
        lambda c: [c["base_url"]],
        0,
    ),
]

print("══════════════════ 冒烟总入口 ══════════════════")
print(f"  被测程序：{EXE}")
print(f"  客户机 exe：{AGENT_EXE or '（未找到 —— sync / browse 场景会跳过）'}")
print(f"  Python ： {PY}")

results = []
for spec in SCENARIOS:
    name, title, script, build_args, extra = spec[:5]
    extra_args = spec[5] if len(spec) > 5 else None
    if only and only != name:
        continue
    ok = run_scenario(name, title, script, build_args, extra, extra_args)
    results.append((name, title, ok))

if not results:
    print(f"\n[X] --only {only} 不匹配任何场景"
          "（可选：web / pairing / admin / agent / transfer / sync / browse / license / rbac）")
    sys.exit(2)

print()
print("══════════════════ 汇总 ══════════════════")
for name, title, ok in results:
    print(f"  [{'PASS' if ok else 'FAIL'}] {name:<8} {title}")
passed = sum(1 for _, _, ok in results if ok)
print(f"  {passed}/{len(results)} 场景通过")
sys.exit(0 if passed == len(results) else 1)
