r"""M6.4 升级 / 回滚演练：把现场库往回倒一版 -> 走完整升级 -> 再走完整回滚。

用法：
    python deploy/upgrade/rehearse-upgrade.py [源数据目录]
    CONTROLSERVER_EXE=<exe 路径> python deploy/upgrade/rehearse-upgrade.py "C:\ProgramData\ControlServer"

它回答四个问题，每个都有硬证据（不是"看着没问题"）：

    1. 升级时数据库真的会被自动改结构吗？        -> 看 agent_hosts 的状态列名变没变
    2. 升级会丢数据吗？                          -> 升级前后九项逐项比对
    3. 回滚能不能把数据带回升级前？              -> 恢复升级前备份后，九项必须**完全等于**升级前
    4. 升级/回滚各要多久、期间服务可不可用？      -> 量启动到 /healthz 的时间，以及不可用窗口

⚠️ 为什么"把库往回倒一版"而不是另造一个空库：
   空库上跑迁移证明不了任何事 —— 迁移在空库和在 5 MB、13 条迁移历史、3 台真实 Agent、
   4787 行遥测的库上，风险完全不同。唯一能只用现有代码造出"旧版库"的办法，是把**最后一条**
   迁移的对象与历史记录一起撤掉。这里选的 `M4_12_AgentBlock` 是一次纯粹的列重命名
   （`IsRevoked` -> `IsBlocked`），撤掉它库就退回 `M4_8` 的形态，而且**能再被正向应用一次**。

⚠️ 它只碰**副本**。源目录只读打开，演练产物在 .tools/upgrade-rehearsal/ 下。
"""

import hashlib
import json
import os
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.environ.get(
    "CONTROLSERVER_EXE",
    os.path.join(REPO, "src", "ControlServer.Server", "bin", "Debug", "net8.0", "ControlServer.Server.exe"),
)

LIVE_SRC = sys.argv[1] if len(sys.argv) > 1 else r"C:\ProgramData\ControlServer"

WORK = os.path.join(REPO, ".tools", "upgrade-rehearsal")
LIVE = os.path.join(WORK, "live")
BACKUP = os.path.join(WORK, "backup-pre-upgrade")

# 被"撤掉"的那条迁移。改这里之前先确认：它必须是纯加法/纯重命名，撤掉后能再正向应用一次。
TARGET_MIGRATION = "20261014171130_M4_12_AgentBlock"
RENAMED_TABLE = "agent_hosts"
NEW_COLUMN = "IsBlocked"
OLD_COLUMN = "IsRevoked"


def decode(raw: bytes) -> str:
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest().upper()[:16]


def tree_hash(root: str) -> str:
    lines = []
    for base, _, files in os.walk(root):
        for name in sorted(files):
            full = os.path.join(base, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            h = hashlib.sha256()
            with open(full, "rb") as f:
                for chunk in iter(lambda: f.read(1 << 20), b""):
                    h.update(chunk)
            lines.append(f"{rel}|{os.path.getsize(full)}|{h.hexdigest()}")
    return sha256_text("\n".join(sorted(lines)))


def ro(db_path: str):
    return sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)


def scalar(con, sql, default="（不可读）"):
    try:
        value = con.execute(sql).fetchone()[0]
        return value if value is not None else default
    except Exception:
        return default


def status_column(con) -> str:
    """当前库里状态列到底叫什么 —— 升级是否真的执行了，看这一项。"""
    cols = [r[1] for r in con.execute(f"pragma table_info({RENAMED_TABLE})")]
    if NEW_COLUMN in cols:
        return NEW_COLUMN
    if OLD_COLUMN in cols:
        return OLD_COLUMN
    return "（两个都没有，表结构异常）"


def snapshot(root: str) -> dict:
    """九项快照。每项都要能"升完级还在"，也要能"回滚后回到原样"。"""
    db = os.path.join(root, "control.db")
    con = ro(db)
    try:
        col = status_column(con)

        # 状态列的**值**也要比：只比"列存在"证明不了数据没被搬家。
        rows = "（不可读）"
        if col in (NEW_COLUMN, OLD_COLUMN):
            try:
                pairs = con.execute(
                    f"select MachineKey, {col} from {RENAMED_TABLE} order by MachineKey"
                ).fetchall()
                rows = sha256_text("|".join(f"{k}={v}" for k, v in pairs))
            except Exception:
                pass

        return {
            "① 用户数": scalar(con, "select count(*) from users"),
            "② 配置文档": f"{scalar(con, 'select count(*) from config_documents')} 份 / v"
                          f"{scalar(con, 'select max(Version) from config_documents')}",
            "③ 审计条数": scalar(con, "select count(*) from audit_logs"),
            "④ Agent 台数": scalar(con, f"select count(*) from {RENAMED_TABLE}"),
            "⑤ Agent 业务摘要": rows,
            "⑥ 遥测行数": scalar(con, "select count(*) from agent_metrics"),
            "⑦ 文件仓库摘要": tree_hash(os.path.join(root, "files"))
                              if os.path.isdir(os.path.join(root, "files")) else "（无 files 目录）",
            "⑧ license.lic 哈希": (
                hashlib.sha256(open(os.path.join(root, "license.lic"), "rb").read()).hexdigest().upper()[:16]
                if os.path.isfile(os.path.join(root, "license.lic")) else "（无授权文件）"
            ),
            "⑨ 迁移历史条数": scalar(con, "select count(*) from __EFMigrationsHistory"),
            "⑩ 状态列名": col,
        }
    finally:
        con.close()


def free_port() -> int:
    s = socket.socket()
    try:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]
    finally:
        s.close()


def wait_health(port: int, timeout: float = 60.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz", timeout=1) as r:
                if r.status == 200:
                    return True
        except Exception:
            time.sleep(0.2)
    return False


def port_listening(port: int) -> bool:
    s = socket.socket()
    s.settimeout(0.5)
    try:
        s.connect(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def run_exe(*args, expect_free_port: bool = False):
    """跑一次维护子命令（--backup / --restore）。"""
    env = os.environ.copy()
    env["Server__Storage__DataDir"] = LIVE
    started = time.time()
    result = subprocess.run([EXE, *args], cwd=REPO, env=env, capture_output=True, timeout=600)
    return result.returncode, decode(result.stdout), decode(result.stderr), time.time() - started


def latest_startup_log(root: str):
    """找最新那个日志文件里【最后一次】启动的那一段，返回 (日志路径, 文本)。"""
    log_dir = os.path.join(root, "logs")
    if not os.path.isdir(log_dir):
        return None, ""
    files = sorted(
        (os.path.join(log_dir, n) for n in os.listdir(log_dir) if n.startswith("server-")),
        key=os.path.getmtime,
    )
    for path in reversed(files):
        with open(path, "rb") as f:
            text = decode(f.read())
        if "启动中" in text:
            return path, text
    return None, ""


TS = r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})"


def measure_startup(root: str):
    """量「启动中 -> 数据库就绪」的耗时，并顺手把版本行抓出来。

    口径说明：这一段包含 Migrate + WAL 设置 + 种子账号 + 清理历史标记，
    是"数据库初始化"整体，**不是**纯 EF Migrate 的时间。报告里必须这么写。
    """
    path, text = latest_startup_log(root)
    if not path:
        return None, None, None

    # 只取最后一次启动（同一个日志文件里可能已经攒了好几次启动）
    # ⚠️ 必须 max(0, …)：日志只有几百字符时 idx-200 是负数，而 Python 的负索引切片
    #    text[-140:] 取的是**末尾 140 个字符**，时间戳刚好被切掉 —— 于是正则永远匹配不上，
    #    输出一个看起来合理的"—"。（这个坑踩过一次，症状是"日志明明有，就是读不出来"。）
    tail = text[max(0, text.rfind("启动中") - 200):]
    begin = re.search(TS + r"\s+\S+\s+\[INF\].*启动中", tail)
    ready = re.search(TS + r"\s+\S+\s+\[INF\] 数据库就绪", tail)
    version = re.search(r"程序版本：(.+)", tail)

    elapsed = None
    if begin and ready:
        # 🔴 不能用 time.mktime(strptime(...))：mktime 收的是 struct_time，**微秒被丢掉**，
        #    两个时刻各自截断到秒之后再相减 ⇒ 结果永远是个整数秒（1.000 / 2.000）。
        #    这个数字看起来毫无破绽，但它把 1.4s 报成 1.0s、1.9s 报成 1.0s。
        fmt = "%Y-%m-%d %H:%M:%S.%f"
        t0 = datetime.strptime(begin.group(1), fmt)
        t1 = datetime.strptime(ready.group(1), fmt)
        elapsed = (t1 - t0).total_seconds()

    return elapsed, (version.group(1).strip() if version else "（日志里没有版本行）"), path


def start_and_stop(root: str, label: str):
    """起一次服务 -> 等 /healthz -> 立刻停。返回 (耗时, 版本, 就绪与否)。"""
    port = free_port()

    # 🔴 启动前把这个库的日志清空：否则 measure_startup 可能解析到**上一次**启动
    #    （含 --backup 那次的半截日志：只有"启动中"、没有"数据库就绪"），
    #    算出来是"—"或者几天前的耗时，而输出看上去完全合理。
    log_dir = os.path.join(root, "logs")
    if os.path.isdir(log_dir):
        for name in os.listdir(log_dir):
            if name.startswith("server-"):
                try:
                    os.remove(os.path.join(log_dir, name))
                except OSError:
                    pass

    env = os.environ.copy()
    env.update({
        "Server__Storage__DataDir": root,
        "Server__Http__Urls": f"http://127.0.0.1:{port}",
        "Server__Https__Enabled": "false",
        "Server__Startup__AutoOpenBrowser": "false",
        "Server__Tray__Enabled": "false",
        "Server__Startup__SingleInstance": "false",
        "Server__Startup__AttachConsole": "true",
        "Server__Time__AutoSync": "false",
        "Server__Timer__Enabled": "false",
        "Server__Discovery__Port": "0",
        "Server__Auth__LoginRateLimit__PermitLimit": "1000",
    })

    started = time.time()
    proc = subprocess.Popen([EXE], cwd=REPO, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    healthy = wait_health(port, timeout=90)
    wall = time.time() - started

    # 🔴 就绪之后必须**停一会儿再杀**。Serilog 的 File sink 有写缓冲，TerminateProcess
    #    不走 CloseAndFlush —— 立刻杀会把"数据库就绪"那几行连同时间戳一起丢掉，
    #    于是量出来是"—"，而"服务起来了"照样是 True（假读数里最难查的一种）。
    time.sleep(1.5)

    proc.terminate()
    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=10)

    # 停服确认：进程退了还不够，端口要真的不再接受连接（否则 restore 会写进"还活着的库"）
    for _ in range(40):
        if not port_listening(port):
            break
        time.sleep(0.25)

    elapsed, version, _log = measure_startup(root)

    # 日志没落盘时读不到，重试几次（不强求：主口径的 wall 已经可靠）
    for _ in range(6):
        if elapsed is not None and version:
            break
        time.sleep(0.5)
        elapsed, version, _log = measure_startup(root)

    return {
        "label": label,
        "healthy": healthy,
        "wall": wall,
        "migrate": elapsed,
        "version": version,
        "port_free": not port_listening(port),
    }


def step(n, text):
    print(f"\n[{n}] {text}")
    print("-" * 78)


def main():
    print("=" * 78)
    print("M6.4 升级 / 回滚演练")
    print("=" * 78)
    print(f"  程序      : {EXE}")
    print(f"  现场副本源: {LIVE_SRC}")

    if not os.path.isfile(EXE):
        print(f"\n❌ 找不到程序：{EXE}\n   先 dotnet build src/ControlServer.Server")
        return 1
    if not os.path.isfile(os.path.join(LIVE_SRC, "control.db")):
        print(f"\n❌ 源目录里没有 control.db：{LIVE_SRC}\n   这不是一个真实数据目录，换一个。")
        return 1

    if os.path.isdir(WORK):
        shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)

    failures = []

    def judge(ok, label, detail=""):
        print(f"  {'✅' if ok else '❌'} {label}{('：' + detail) if detail else ''}")
        if not ok:
            failures.append(f"{label}{('：' + detail) if detail else ''}")
        return ok

    # ── 0. 复制现场数据目录 ──────────────────────────────────────
    step(0, "复制现场数据目录")
    shutil.copytree(LIVE_SRC, LIVE)
    print(f"    {LIVE_SRC}\n    -> {LIVE}")

    # ── 1. 把库往回倒一版（造出"旧版现场库"）───────────────────
    step(1, f"倒回一版：撤掉迁移 {TARGET_MIGRATION}")
    db = os.path.join(LIVE, "control.db")

    # 清掉复制过来的历史日志：不然"最后一次启动"会解析成现场上一次开机那一段，
    # 量出来的是几天前的数字，而且看上去完全合理（这是最难发现的一类假读数）。
    log_dir = os.path.join(LIVE, "logs")
    if os.path.isdir(log_dir):
        removed = 0
        for name in os.listdir(log_dir):
            if name.startswith("server-"):
                os.remove(os.path.join(log_dir, name))
                removed += 1
        print(f"    清掉 {removed} 个历史日志（避免量到现场上一次开机的耗时）")

    for suffix in ("-wal", "-shm"):
        stale = db + suffix
        if os.path.exists(stale):
            os.remove(stale)
            print(f"    先删掉遗留的 control.db{suffix}（不删的话下次打开会把旧 WAL 重放回去）")

    con = sqlite3.connect(db)
    con.execute("pragma journal_mode=delete")
    before_count = con.execute("select count(*) from __EFMigrationsHistory").fetchone()[0]
    con.execute(f"alter table {RENAMED_TABLE} rename column {NEW_COLUMN} to {OLD_COLUMN}")
    con.execute("delete from __EFMigrationsHistory where MigrationId = ?", (TARGET_MIGRATION,))
    con.commit()

    cols = [r[1] for r in con.execute(f"pragma table_info({RENAMED_TABLE})")]
    after_count = con.execute("select count(*) from __EFMigrationsHistory").fetchone()[0]
    con.close()
    print(f"    状态列 {NEW_COLUMN} -> {OLD_COLUMN}")
    print(f"    迁移历史 {before_count} -> {after_count} 条")
    if not judge(OLD_COLUMN in cols and NEW_COLUMN not in cols, "库已回到旧版形态"):
        return 2

    # ── 2. 升级前快照 ───────────────────────────────────────────
    step(2, "升级前快照（A）")
    before = snapshot(LIVE)
    for k, v in before.items():
        print(f"    {k:16} {v}")

    # ── 3. 升级前备份（★ 没有这一份就没有回滚）─────────────────
    step(3, f"备份：--backup {BACKUP}")
    code, out, err, elapsed = run_exe("--backup", BACKUP)
    print(f"    退出码 {code}，耗时 {elapsed:.2f}s")
    for line in out.rstrip().splitlines():
        print("    | " + line)
    for line in err.rstrip().splitlines():
        print("    ! " + line)
    # 备份失败必须中止：继续升级就是在没有回滚路径的情况下动现场库（fail-closed）
    if not judge(code == 0 and os.path.isfile(os.path.join(BACKUP, "manifest.json")),
                 "升级前备份成功"):
        print("\n❌ 备份失败 —— 演练中止。真实升级里这里也必须中止，不能「先升了再说」。")
        return 2

    # ── 4. 升级：起新版，让它自己 Migrate ──────────────────────
    step(4, "升级：启动新版（数据库自动升级）")
    up = start_and_stop(LIVE, "升级后")
    print(f"    /healthz 200 ？ {up['healthy']}（进程启动到就绪 {up['wall']:.2f}s）")
    print(f"    日志「启动中 -> 数据库就绪」："
          f"{'—' if up['migrate'] is None else format(up['migrate'], '.3f') + ' s'}")
    print(f"    启动自述里的程序版本：{up['version']}")
    judge(up["healthy"], "升级后服务能起来")
    judge(up["port_free"], "停服后端口已释放（下一步的恢复才敢写库）")

    # ── 5. 升级后快照（B）与 A 比 ──────────────────────────────
    step(5, "升级后快照（B），与 A 比")
    after = snapshot(LIVE)
    print(f"    {'项目':16} {'升级前':>28} {'升级后':>28}   判定")
    for k in before:
        same = before[k] == after[k]
        # ⑨⑩ 本来就该变 —— 它们正是"升级执行了"的证据
        expected_change = k in ("⑨ 迁移历史条数", "⑩ 状态列名")
        mark = "✅" if same else ("➡ 变了（预期）" if expected_change else "❌ 变了（不该）")
        print(f"    {k:16} {str(before[k]):>28} {str(after[k]):>28}   {mark}")
        if not same and not expected_change:
            failures.append(f"升级后 {k} 变了：{before[k]} -> {after[k]}")

    judge(after["⑩ 状态列名"] == NEW_COLUMN, f"结构真的升级了（状态列 = {NEW_COLUMN}）",
          after["⑩ 状态列名"])
    judge(after["⑨ 迁移历史条数"] > before["⑨ 迁移历史条数"], "迁移历史多了一条",
          f"{before['⑨ 迁移历史条数']} -> {after['⑨ 迁移历史条数']}")

    # ── 6. 造一笔"升级后才有的数据" ─────────────────────────────
    step(6, "写入升级后增量（模拟升级之后业务又跑了几天）")
    con = sqlite3.connect(db)
    con.execute(
        "insert into audit_logs (Timestamp, User, Action, Detail, RemoteIp) values (?,?,?,?,?)",
        ("2026-10-17 22:00:00.000 +08:00", "rehearsal", "upgrade-rehearsal",
         "回滚之后这条应该消失", "127.0.0.1"),
    )
    con.commit()
    audit_after_insert = con.execute("select count(*) from audit_logs").fetchone()[0]
    con.close()
    print(f"    审计条数 {before['③ 审计条数']} -> {audit_after_insert}（+1）")

    # ── 7. 回滚：恢复升级前备份 ────────────────────────────────
    step(7, f"回滚：--restore {BACKUP} --force")
    code, out, err, elapsed = run_exe("--restore", BACKUP, "--force")
    print(f"    退出码 {code}，耗时 {elapsed:.2f}s")
    for line in out.rstrip().splitlines():
        print("    | " + line)
    for line in err.rstrip().splitlines():
        print("    ! " + line)
    if not judge(code == 0, "恢复成功"):
        return 2

    # ── 8. 回滚后快照（C）必须完全等于 A ───────────────────────
    step(8, "回滚后快照（C），与升级前 A 比（必须完全一致）")
    rolled = snapshot(LIVE)
    print(f"    {'项目':16} {'升级前 A':>28} {'回滚后 C':>28}   判定")
    for k in before:
        same = before[k] == rolled[k]
        print(f"    {k:16} {str(before[k]):>28} {str(rolled[k]):>28}   {'✅' if same else '❌'}")
        if not same:
            failures.append(f"回滚后 {k} 没回到升级前：A={before[k]} C={rolled[k]}")

    judge(rolled["③ 审计条数"] == before["③ 审计条数"],
          "升级后写入的那条增量确实消失了（这就是回滚的代价）",
          f"回滚前 {audit_after_insert} 条 / 回滚后 {rolled['③ 审计条数']} 条")

    # ── 9. 回滚后能不能正常起 ──────────────────────────────────
    step(9, "回滚后启动（旧版形态的库能不能被正常打开）")
    roll = start_and_stop(LIVE, "回滚后")
    print(f"    /healthz 200 ？ {roll['healthy']}（{roll['wall']:.2f}s）")
    print(f"    启动自述里的程序版本：{roll['version']}")
    judge(roll["healthy"], "回滚后服务能起来")

    # ── 汇总 ───────────────────────────────────────────────────
    print("\n" + "=" * 78)
    if failures:
        print(f"❌ {len(failures)} 项不通过")
        for f in failures:
            print(f"   - {f}")
    else:
        print("✅ 全部通过")
    print("=" * 78)
    print("\n关键数字（写报告时引这几个，别凭印象）：")
    print(f"  升级：进程启动到 /healthz 200 = {up['wall']:.2f}s；"
          f"启动中 -> 数据库就绪 = {'—' if up['migrate'] is None else format(up['migrate'], '.3f') + 's'}")
    print(f"  升级后版本：{up['version']}")
    print(f"  回滚：restore 耗时 = 见上；回滚后版本：{roll['version']}")
    print(f"  回滚窗口内数据代价：升级后新增的 1 条审计被还原掉（升级到回滚之间的业务数据都会丢）")

    return 0 if not failures else 2


if __name__ == "__main__":
    code = main()

    # 演练产物里有真实数据：通过就清，不通过留着排查。
    if code == 0:
        shutil.rmtree(WORK, ignore_errors=True)
    else:
        print(f"\n（演练产物保留在 {WORK}，供排查）")

    sys.exit(code)
