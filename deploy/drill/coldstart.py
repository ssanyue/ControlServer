#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
冷启动演练（M6.5）—— 在一台"干净机器"上，只照文档从零装到能用。

M6 的验收原话是「一线人员按文档能独立部署」，而 `docs/M6-加固交付方案.md` §0 把它翻译成了
一个可测的判据：**在干净机器上，只照文档，从零装到能用（冷启动演练）**。
这个脚本就是去执行那条判据。

它凭什么算"干净"
    ① 一个全新的空目录当"安装目录"（模拟解压绿色版 / 装完的 Program Files）；
    ② 一个**不存在**的数据目录（服务第一次启动才建它）；
    ③ 全新端口（连默认的 8080/9999/9998 都不用），确保不撞开发机上正在跑的东西；
    ④ 不写任何注册表、不建服务、不改系统设置 —— 演练结束后整个临时根目录删掉。

它凭什么算"只照文档"
    每一步都对应文档里的**一条命令**，脚本里把那条命令**原样**打出来，
    再用文档自己写的成功判据去判（`--health` 退出码、`/healthz` 200、`--dbinfo` 逐行一致……）。
    ⚠️ 诚实的边界：脚本能证明"文档里的命令原样执行是通的"，
    **证明不了"人能读懂那段文档"** —— 后者只能靠找个人真的照着做一次。

用法：
    python deploy/drill/coldstart.py
    python deploy/drill/coldstart.py <exe路径>

退出码：全通过 0；否则 1（失败步骤会在结论里点名，并保留现场供排查）。
"""

import hashlib
import http.cookiejar
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

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "dist", "win-x64", "ControlServer.Server.exe")

class DrillAborted(Exception):
    """
    严重到没法继续的失败（服务根本起不来）。

    刻意**不用** SystemExit：那样会跳过最后那张汇总表，
    而失败时恰恰最需要看到「一共过了几步、哪几步没过」。
    """


RESULTS = []          # [(步骤号, 文档出处, 描述, ok, 证据)]
TIMINGS = {}


# ═══════════════════════════════════════════════════════════════
# 小工具
# ═══════════════════════════════════════════════════════════════

def free_port(kind):
    """要一个当前空闲的端口（先绑定再释放，只用回环）。"""
    s = socket.socket(socket.AF_INET, kind)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


_opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
)


def step(no, doc, what, ok, evidence=""):
    RESULTS.append((no, doc, what, ok, evidence))
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {no}. {what}")
    if evidence:
        print(f"         {evidence}")
    return ok


def show(command):
    """把"文档里那条命令"打出来 —— 这是本脚本最重要的输出。"""
    print(f"         $ {command}")


def call(method, path, body=None, timeout=60):
    data, headers = None, {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with _opener.open(req, timeout=timeout) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8") or "null")
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, raw


def wait_ready(timeout=45.0):
    """等 /healthz 200；进程先退出就立刻返回 (False, '进程已退出')。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if PROC.poll() is not None:
            return False, f"进程已退出（code={PROC.returncode}）"
        try:
            req = urllib.request.Request(BASE + "/healthz")
            with _opener.open(req, timeout=3) as resp:
                if resp.status == 200:
                    return True, json.loads(resp.read().decode("utf-8"))
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(0.2)
    return False, "超时"


def server_argv():
    return [
        EXE,
        f"--Server:Storage:DataDir={DATA_DIR}",
        f"--Server:Http:Urls={BASE}",
        f"--Server:Http:Port={HTTP_PORT}",
        f"--Server:Listener:PairingPort={PAIRING_PORT}",
        f"--Server:Discovery:Port={DISCOVERY_PORT}",
        "--Server:Startup:AutoOpenBrowser=false",
        "--Server:Tray:Enabled=false",
        "--Server:Startup:SingleInstance=false",
    ]


def start_server():
    global PROC
    log_path = os.path.join(ROOT, f"server-{len(STARTS)}.log")
    STARTS.append(log_path)
    log = open(log_path, "wb")
    PROC = subprocess.Popen(server_argv(), stdout=log, stderr=subprocess.STDOUT, cwd=APP_DIR)
    return log_path


def stop_server():
    global PROC
    if PROC is None or PROC.poll() is not None:
        return
    PROC.terminate()
    try:
        PROC.wait(timeout=15)
    except subprocess.TimeoutExpired:
        PROC.kill()
        PROC.wait(timeout=5)


def cli(*args):
    """
    以前台子进程跑一条维护命令（--health / --dbinfo / --backup / --restore）。

    为什么要显式给环境变量：
      `--health` 靠 `Server:Http:Port` 找服务、靠 `Server:Storage:DataDir` 找目录。
      忘了给，它会去读**默认目录**里的另一份数据 —— 那份"看起来完全正常"，
      于是你会对着错的那份做比对（`deploy/upgrade/README.md` 里第 3 条坑）。
    """
    env = dict(os.environ)
    env["Server__Storage__DataDir"] = DATA_DIR
    env["Server__Http__Port"] = str(HTTP_PORT)

    result = subprocess.run(
        [EXE] + list(args),
        cwd=APP_DIR,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=300,
    )

    # ⚠️ 输出编码：中文 Windows 控制台是 GBK。这里只解析 ASCII 的 键=值 行，
    #    所以解码用什么编码都不影响判定 —— 但打印给人看的那份要能读。
    text = result.stdout.decode("gbk", errors="replace")
    return result.returncode, text


def parse_kv(text):
    """把 `键=值` 行收成 dict。ASCII 键是契约，别的行（中文标题、确认语）一律忽略。"""
    out = {}
    for line in text.splitlines():
        m = re.match(r"^([A-Za-z][A-Za-z0-9._]*)=(.*)$", line.strip())
        if m:
            out[m.group(1)] = m.group(2)
    return out


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


# ═══════════════════════════════════════════════════════════════
# 0. 准备一台"干净机器"
# ═══════════════════════════════════════════════════════════════

ROOT = os.path.join(tempfile.gettempdir(), f"cs-coldstart-{os.getpid()}")
APP_DIR = os.path.join(ROOT, "app")
DATA_DIR = os.path.join(ROOT, "ControlServerData")
BACKUP_DIR = os.path.join(ROOT, "backup")
STARTS = []
PROC = None

HTTP_PORT = free_port(socket.SOCK_STREAM)
PAIRING_PORT = free_port(socket.SOCK_DGRAM)
DISCOVERY_PORT = free_port(socket.SOCK_DGRAM)
while DISCOVERY_PORT == PAIRING_PORT:
    DISCOVERY_PORT = free_port(socket.SOCK_DGRAM)

BASE = f"http://127.0.0.1:{HTTP_PORT}"

print("═" * 74)
print("  中控服务版 · 冷启动演练（M6.5 验收：干净机器只照文档装到能用）")
print("═" * 74)
print(f"  被测程序：{EXE}")
print(f"  临时根：  {ROOT}")
print(f"  端口：HTTP {HTTP_PORT} / 对码 {PAIRING_PORT} / 发现 {DISCOVERY_PORT}")
print()

try:
    if not os.path.isfile(EXE):
        print(f"  [X] 找不到被测程序：{EXE}")
        sys.exit(1)

    print("─" * 74)
    print("  第 1 段：装（干净目录 + 全新数据目录）")
    print("─" * 74)

    shutil.rmtree(ROOT, ignore_errors=True)
    os.makedirs(APP_DIR)
    os.makedirs(BACKUP_DIR)

    # 文档《绿色版-使用说明》的等价动作：把 exe 放进一个空目录就算"装好了"。
    show(f"copy {EXE} -> {APP_DIR}")
    shutil.copy2(EXE, os.path.join(APP_DIR, os.path.basename(EXE)))

    step(
        "1.1",
        "绿色版-使用说明.md",
        "把服务程序放进一个干净目录（不建服务、不写注册表）",
        os.path.isfile(os.path.join(APP_DIR, os.path.basename(EXE))),
        f"{os.path.getsize(os.path.join(APP_DIR, os.path.basename(EXE)))} 字节",
    )

    step(
        "1.2",
        "Windows-安装与复测说明.md",
        "数据目录此时**不存在**（服务第一次启动才建它）",
        not os.path.exists(DATA_DIR),
        DATA_DIR,
    )

    # ── 1.3 空数据目录上先跑一次巡检（这是"还没装"的基线）───────────────
    show(f"{os.path.basename(EXE)} --health")
    code_before, text_before = cli("--health")
    kv_before = parse_kv(text_before)

    step(
        "1.3",
        "运维手册.md §1",
        "启动之前跑 --health：应如实报「目录不存在」，而不是崩或假装健康",
        code_before == 2 and kv_before.get("data.exists") == "0",
        f"退出码 {code_before}，data.exists={kv_before.get('data.exists')}",
    )

    print()
    print("─" * 74)
    print("  第 2 段：起（第一次启动 = 建库 + 播种管理员 + 自动试用）")
    print("─" * 74)

    show("ControlServer.Server.exe --Server:Storage:DataDir=<新目录> …")
    t0 = time.time()
    log_path = start_server()
    ok, health = wait_ready()
    TIMINGS["启动 → /healthz 200"] = time.time() - t0

    step(
        "2.1",
        "Windows-安装与复测说明.md",
        "服务能起来，/healthz 返回 200",
        ok,
        f"耗时 {TIMINGS['启动 → /healthz 200']:.2f} s" if ok else health,
    )

    if not ok:
        print(f"         启动日志：{log_path}")
        raise DrillAborted()

    step(
        "2.2",
        "M6.4-升级回滚.md §5",
        "启动自述带版本号（升级/回滚后能证明「跑的是哪个包」）",
        bool(health.get("version")),
        f"version={health.get('version')}",
    )

    step(
        "2.3",
        "运维手册.md §3.4",
        "/healthz 暴露了日志落盘的观察窗（logSink）",
        isinstance(health.get("logSink"), dict) and health["logSink"].get("healthy") is True,
        f"logSink={health.get('logSink')}",
    )

    print()
    print("─" * 74)
    print("  第 3 段：用（登录 + 改一处配置，证明它不是「只能启动」）")
    print("─" * 74)

    show('POST /api/auth/login  {"userName":"admin","password":"admin123"}')
    status, body = call("POST", "/api/auth/login", {"userName": "admin", "password": "admin123"})
    step(
        "3.1",
        "Windows-安装与复测说明.md",
        "出厂管理员能直接登录（不再强制改密）",
        status == 200,
        f"HTTP {status}",
    )

    show("GET /api/config")
    status, cfg = call("GET", "/api/config")
    version_before = cfg.get("version") if isinstance(cfg, dict) else None
    step(
        "3.2",
        "Windows-安装与复测说明.md",
        "空部署的配置可读，版本号是一个数",
        status == 200 and isinstance(version_before, int),
        f"HTTP {status}，version={version_before}",
    )

    show("PUT /api/config  （改 appName，expectedVersion=上面那个）")
    status, _ = call(
        "PUT",
        "/api/config",
        {
            "settings": {
                "appName": "冷启动演练",
                "defaultButtonImage": "",
                "buttons": [],
                "subButtonSets": [],
            },
            "expectedVersion": version_before,
            "updatedBy": "coldstart-drill",
        },
    )
    step("3.3", "Windows-安装与复测说明.md", "能改配置（写通）", status == 200, f"HTTP {status}")

    status, cfg = call("GET", "/api/config")
    version_after = cfg.get("version") if isinstance(cfg, dict) else None
    step(
        "3.4",
        "Windows-安装与复测说明.md",
        "配置版本号 +1（这就是「改成功了」的证据）",
        status == 200 and version_after == version_before + 1,
        f"version {version_before} → {version_after}",
    )

    print()
    print("─" * 74)
    print("  第 4 段：巡检（服务在跑的时候跑 --health，应该基本全绿）")
    print("─" * 74)

    show(f"{os.path.basename(EXE)} --health")
    code_running, text_running = cli("--health")
    kv_running = parse_kv(text_running)

    step(
        "4.1",
        "运维手册.md §1",
        "服务在跑时巡检能连上本机探活",
        kv_running.get("service.healthz") == "200",
        f"service.healthz={kv_running.get('service.healthz')}",
    )

    step(
        "4.2",
        "运维手册.md §1",
        "巡检看得到库与日志的体积（不是一堆 0）",
        int(kv_running.get("db.bytes", "0")) > 0 and int(kv_running.get("logs.bytes", "0")) > 0,
        f"db.bytes={kv_running.get('db.bytes')}，logs.bytes={kv_running.get('logs.bytes')}",
    )

    step(
        "4.3",
        "运维手册.md §3.4",
        "日志保留策略默认是开着的（M6.5 之前它根本不存在）",
        kv_running.get("logs.retention.enabled") == "1",
        f"保留 {kv_running.get('logs.retention.days')} 天 / "
        f"上限 {kv_running.get('logs.retention.mb')} MB",
    )

    step(
        "4.4",
        "运维手册.md §1.1",
        "自动试用已生效（没有正式授权也能用）",
        kv_running.get("license.present") == "0" and int(kv_running.get("trial.daysleft", "-1")) > 0,
        f"license.present={kv_running.get('license.present')}，trial.daysleft={kv_running.get('trial.daysleft')}",
    )

    step(
        "4.5",
        "运维手册.md §1.2",
        "退出码与结论一致（这里只允许 0 或 1：证书/授权告警不算失败）",
        code_running in (0, 1) and kv_running.get("health") in ("ok", "warn"),
        f"退出码 {code_running}，health={kv_running.get('health')}，warn.count={kv_running.get('warn.count')}",
    )

    # 🔴 这一条是整场演练最"硬"的判据：一台**刚从包里拆出来**的机器，
    #    巡检应当是**零告警**的。只要有告警，值班从此对巡检脱敏 ——
    #    所以这里宁可失败也要把 warn.N 原样打出来，让人当场看到是哪一条。
    warn_lines = [f"{k}={v}" for k, v in kv_running.items() if k.startswith("warn.") and k != "warn.count"]
    step(
        "4.6",
        "运维手册.md §1.1",
        "新装机器巡检零告警（否则值班会对告警脱敏）",
        kv_running.get("warn.count") == "0",
        "0 条告警" if not warn_lines else "；".join(warn_lines),
    )

    print()
    print("─" * 74)
    print("  第 5 段：备份 → 删库 → 恢复（M6 的「备份恢复」验收判据）")
    print("─" * 74)

    show(f"{os.path.basename(EXE)} --dbinfo")
    code_dbinfo, text_dbinfo = cli("--dbinfo")
    dbinfo_before = parse_kv(text_dbinfo)

    step(
        "5.1",
        "M6.4-升级回滚.md §7.4",
        "备份前先留一份数据指纹（--dbinfo）",
        code_dbinfo == 0 and int(dbinfo_before.get("table.users", "0")) >= 1,
        f"users={dbinfo_before.get('table.users')}，"
        f"config_documents={dbinfo_before.get('table.config_documents')}，"
        f"migrations={dbinfo_before.get('migrations')}",
    )

    show(f"{os.path.basename(EXE)} --backup {BACKUP_DIR}")
    code_backup, text_backup = cli("--backup", BACKUP_DIR)
    manifest_path = os.path.join(BACKUP_DIR, "manifest.json")
    backup_kv = parse_kv(text_backup)

    step(
        "5.2",
        "M6-加固交付方案.md §3.5",
        "备份成功，并落了一份 manifest",
        code_backup == 0 and os.path.isfile(manifest_path),
        f"退出码 {code_backup}，manifest {'在' if os.path.isfile(manifest_path) else '不在'}",
    )

    # 私钥不进备份 —— M6.1 的硬结论，冷启动也顺手确认一次。
    leaked = []
    for dirpath, _dirnames, filenames in os.walk(BACKUP_DIR):
        for name in filenames:
            if name.lower().endswith(".pfx"):
                leaked.append(os.path.join(dirpath, name))

    step(
        "5.3",
        "M6.4-升级回滚.md §0",
        "备份里**没有**证书私钥（根 CA 私钥不外带）",
        not leaked,
        "0 个 .pfx" if not leaked else f"泄漏：{leaked}",
    )

    # ── 灾难：停服 + 删库（真删，不是改名）──────────────────────────
    stop_server()
    show("sc stop <服务名>   # 演练里等价于结束进程并确认端口不再应答")

    db_files = ["control.db", "control.db-wal", "control.db-shm"]
    removed = []
    for name in db_files:
        path = os.path.join(DATA_DIR, name)
        if os.path.exists(path):
            os.remove(path)
            removed.append(name)

    step(
        "5.4",
        "M6-加固交付方案.md §3.5",
        "真把库删了（三个文件全删，漏一个 WAL 就是假灾）",
        not os.path.exists(os.path.join(DATA_DIR, "control.db")),
        f"删了 {removed}",
    )

    show(f"{os.path.basename(EXE)} --restore {BACKUP_DIR} --force")
    t0 = time.time()
    code_restore, text_restore = cli("--restore", BACKUP_DIR, "--force")
    TIMINGS["恢复（--restore --force）"] = time.time() - t0

    step(
        "5.5",
        "M6-加固交付方案.md §3.5",
        "恢复成功",
        code_restore == 0 and os.path.exists(os.path.join(DATA_DIR, "control.db")),
        f"退出码 {code_restore}，耗时 {TIMINGS['恢复（--restore --force）']:.2f} s",
    )

    print()
    print("─" * 74)
    print("  第 6 段：恢复之后逐项比对（「恢复成功」必须等于「数据一模一样」）")
    print("─" * 74)

    t0 = time.time()
    log_path = start_server()
    ok, health2 = wait_ready()
    TIMINGS["恢复后再起服 → /healthz 200"] = time.time() - t0

    step(
        "6.1",
        "M6-加固交付方案.md §3.5",
        "恢复之后服务能正常起来",
        ok,
        f"耗时 {TIMINGS['恢复后再起服 → /healthz 200']:.2f} s" if ok else health2,
    )

    if not ok:
        print(f"         启动日志：{log_path}")
        raise DrillAborted()

    code_after, text_after = cli("--dbinfo")
    dbinfo_after = parse_kv(text_after)

    # 逐键比：不只看"有没有"，还要看"是不是同一份"。
    ignored = {"db"}   # 文件字节数会因为页对齐略有差异，行数才是数据
    mismatched = []
    for key, value in dbinfo_before.items():
        if key in ignored:
            continue
        if dbinfo_after.get(key) != value:
            mismatched.append(f"{key}: {value} -> {dbinfo_after.get(key)}")

    step(
        "6.2",
        "M6-加固交付方案.md §3.5",
        "恢复后 --dbinfo 与备份前**逐项一致**",
        not mismatched,
        "全部一致" if not mismatched else "；".join(mismatched),
    )

    show("GET /api/config")
    status, cfg2 = call("GET", "/api/config")
    step(
        "6.3",
        "M6-加固交付方案.md §3.5",
        "配置内容与版本号回到备份那一刻（改动还在）",
        status == 200 and cfg2.get("version") == version_after
        and cfg2.get("settings", {}).get("appName") == "冷启动演练",
        f"HTTP {status}，version={cfg2.get('version') if isinstance(cfg2, dict) else '?'}，"
        f"appName={cfg2.get('settings', {}).get('appName') if isinstance(cfg2, dict) else '?'}",
    )

    step(
        "6.4",
        "运维手册.md §3.4",
        "服务起来后日志落盘恢复正常",
        isinstance(health2.get("logSink"), dict) and health2["logSink"].get("healthy") is True,
        f"logSink={health2.get('logSink')}",
    )

    print()
    print("─" * 74)
    print("  第 7 段：收尾")
    print("─" * 74)

    stop_server()
    step(
        "7.1",
        "运维手册.md §1.1",
        "停服之后巡检如实报 unreachable（不是崩、也不是假装 200）",
        parse_kv(cli("--health")[1]).get("service.healthz") == "unreachable",
        "service.healthz=unreachable",
    )

except DrillAborted:
    # 已经打印过失败证据；这里什么都不做，让下面的汇总照常输出。
    pass
finally:
    stop_server()

# ═══════════════════════════════════════════════════════════════
# 结论
# ═══════════════════════════════════════════════════════════════

passed = [r for r in RESULTS if r[3]]
failed = [r for r in RESULTS if not r[3]]

print()
print("═" * 74)
print("  结论")
print("═" * 74)
print(f"  通过 {len(passed)} / {len(RESULTS)}")
for key, value in TIMINGS.items():
    print(f"  {key}：{value:.2f} s")

if failed:
    print()
    print("  失败项：")
    for no, doc, what, _ok, evidence in failed:
        print(f"    × {no} {what}")
        print(f"      文档：{doc}")
        print(f"      证据：{evidence}")
    print()
    print(f"  现场保留在：{ROOT}")
    print("  （成功才删。失败时这份目录就是排查材料，别急着清。）")
    sys.exit(1)

print()
print("  √ 干净机器只照文档装到能用：登录、改配置、巡检、备份、删库、恢复全部走通。")
print(f"  临时目录已清理：{ROOT}")
shutil.rmtree(ROOT, ignore_errors=True)
sys.exit(0)
