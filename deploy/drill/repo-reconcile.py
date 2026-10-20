#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
文件仓库对账的真机验证（M7.1）。

单测证明了判据，但单测用的是内存替身与假目录。这里要证明的是**打好的 exe 在真数据目录上**
做对了三件事：

  ① 超龄孤儿被真的删掉、未超龄的一律不动（防 Move→落库 的竞态）；
  ② 断链（库里有、盘上没有）**不删记录** —— 只在日志里喊；
  ③ 只有"无主"的 .part 才清，且不走进 repo 的子目录。

⚠️ 全程在临时目录里，不碰 %ProgramData%\\ControlServer。
⚠️ 后台任务第一轮在启动后 1 分钟才跑，所以本脚本要等 75 秒 —— 这段等待是刻意的，
   它同时也是"启动不会立刻扫盘、不和 Migrate 抢 IO"的证据。

用法：python deploy/drill/repo-reconcile.py
"""
import hashlib
import http.cookiejar
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.error

EXE = os.path.join("dist", "win-x64", "ControlServer.Server.exe")
GRACE_HOURS = 24
WAIT_FIRST_ROUND = 75  # 启动后 1 分钟跑第一轮 + 余量

RESULTS = []


def step(no, what, ok, evidence=""):
    RESULTS.append((no, what, ok, evidence))
    print(f"  [{'PASS' if ok else 'FAIL'}] {no}. {what}")
    if evidence:
        print(f"         {evidence}")
    return ok


def free_port():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def make(path, size, age_hours):
    """造一个文件，并把 mtime 设成 age_hours 小时之前（判龄靠它）。"""
    with open(path, "wb") as f:
        f.write(b"x" * size)
    ts = time.time() - age_hours * 3600
    os.utime(path, (ts, ts))
    return path


def main():
    if not os.path.isfile(EXE):
        print(f"找不到 {EXE} —— 先打绿色版包（deploy/windows/build-portable-exe.ps1）")
        return 1

    root = os.path.join(tempfile.gettempdir(), "groom-live-%d" % os.getpid())
    data = os.path.join(root, "data")
    repo = os.path.join(data, "files", "repo")
    staging = os.path.join(data, "files", "uploads")
    os.makedirs(repo)
    os.makedirs(staging)

    http_port, pair_port, disc_port = free_port(), free_port(), free_port()
    base = "http://127.0.0.1:%d" % http_port

    args = [
        EXE,
        "--Server:Storage:DataDir=" + data,
        "--Server:Http:Urls=" + base,
        "--Server:Http:Port=%d" % http_port,
        "--Server:Listener:PairingPort=%d" % pair_port,
        "--Server:Discovery:Port=%d" % disc_port,
        "--Server:Startup:AutoOpenBrowser=false",
        "--Server:Tray:Enabled=false",
        "--Server:Startup:SingleInstance=false",
    ]

    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
    )

    def call(method, path, body=None, raw=None, ctype=None, timeout=60):
        headers = {}
        payload = None
        if body is not None:
            payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"
        elif raw is not None:
            payload = raw
            if ctype:
                headers["Content-Type"] = ctype
        req = urllib.request.Request(base + path, data=payload, headers=headers, method=method)
        try:
            with opener.open(req, timeout=timeout) as resp:
                text = resp.read().decode("utf-8", errors="replace")
                return resp.status, (json.loads(text) if text.strip() else None)
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8", errors="replace")
            try:
                return e.code, json.loads(text)
            except Exception:
                return e.code, text

    proc = None

    def start():
        nonlocal proc
        proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(200):
            try:
                with urllib.request.urlopen(base + "/healthz", timeout=2) as r:
                    if r.status == 200:
                        return True
            except Exception:
                time.sleep(0.2)
        return False

    def stop():
        nonlocal proc
        if proc is None:
            return
        proc.terminate()
        try:
            proc.wait(timeout=20)
        except Exception:
            proc.kill()
        proc = None
        # 留给 WAL 收尾一点时间，否则下一轮可能会读到 -shm 未释放的库。
        time.sleep(1.0)

    try:
        # ── 第 1 段：起服 + 上传一个真文件（这样才有"库里有记录"的样本） ──
        print("\n  第 1 段：起服并上传一个真文件，作为对账的样本")
        if not start():
            print("  [FAIL] 服务起不来")
            return 1

        status, _ = call("POST", "/api/auth/login", {"userName": "admin", "password": "admin123"})
        step("1.1", "管理员登录", status == 200, f"POST /api/auth/login -> {status}")

        blob = b"reconcile-probe" * 64  # 896 字节
        sha = hashlib.sha256(blob).hexdigest()

        status, created = call(
            "POST", "/api/files/uploads",
            {"fileName": "对账样本.bin", "size": len(blob), "kind": "normal", "sha256": sha})
        upload_id = (created or {}).get("uploadId") or (created or {}).get("UploadId")
        step("1.2", "建上传会话", status == 200 and bool(upload_id), f"status={status} uploadId={upload_id}")

        status, _ = call("PUT", f"/api/files/uploads/{upload_id}?offset=0", raw=blob,
                         ctype="application/octet-stream")
        step("1.3", "追加分块", status == 200, f"status={status}")

        status, done = call("POST", f"/api/files/uploads/{upload_id}/complete", {"sha256": sha})
        step("1.4", "完成上传（落库 + 落位）", status == 200, f"status={status}")

        repo_before = sorted(os.listdir(repo))
        step("1.5", "repo 里确实出现了一个文件", len(repo_before) == 1, f"{repo_before}")

        stop()

        # ── 第 2 段：造四种场景 ──
        print("\n  第 2 段：造场景（全部在临时数据目录里）")

        real_name = repo_before[0]
        # ① 断链 + 孤儿：把真文件改名并做旧 ⇒ 记录指向的名字盘上没有（断链），
        #    而改名后的那份是"盘有库无且超龄"（孤儿，该删）。
        renamed = os.path.join(repo, real_name + ".orig")
        os.rename(os.path.join(repo, real_name), renamed)
        old = time.time() - 30 * 3600
        os.utime(renamed, (old, old))

        # ② 超龄孤儿
        stale_orphan = make(os.path.join(repo, "f_orphan_stale.bin"), 4096, 30)

        # ③ 未超龄孤儿（模拟"刚 Move 进 repo、还没落库"的窗口）
        fresh_orphan = make(os.path.join(repo, "f_orphan_fresh.bin"), 4096, 0.2)

        # ④ 无主 .part（超龄）+ 很新的无主 .part
        stale_part = make(os.path.join(staging, "f_ghost.bin.part"), 2048, 30)
        fresh_part = make(os.path.join(staging, "f_racing.bin.part"), 2048, 0.2)

        # ⑤ repo 下的子目录（不许递归进）
        sub_dir = os.path.join(repo, "手工备份")
        os.makedirs(sub_dir)
        nested = make(os.path.join(sub_dir, "别删我.zip"), 512, 30)

        step("2.1", "断链样本已就位（记录指向的文件被改名）", True, f"{real_name} -> {real_name}.orig")
        step("2.2", "三个孤儿 + 两个 .part + 一个子目录已就位", True,
             "stale=30h fresh=0.2h grace=%dh" % GRACE_HOURS)

        # ── 第 3 段：起服，等后台第一轮对账 ──
        print(f"\n  第 3 段：起服，等后台第一轮对账（{WAIT_FIRST_ROUND} 秒）")
        if not start():
            print("  [FAIL] 服务第二次起不来")
            return 1

        time.sleep(WAIT_FIRST_ROUND)

        gone = lambda p: not os.path.exists(p)

        step("3.1", "超龄孤儿被删掉", gone(stale_orphan), stale_orphan)
        step("3.2", "改名后的超龄文件也被删掉", gone(renamed), renamed)
        step("3.3", "未超龄孤儿原封不动（防 Move→落库 竞态）", os.path.exists(fresh_orphan), fresh_orphan)
        step("3.4", "超龄的无主 .part 被删掉", gone(stale_part), stale_part)
        step("3.5", "很新的无主 .part 原封不动", os.path.exists(fresh_part), fresh_part)
        step("3.6", "repo 子目录里的文件没被碰", os.path.exists(nested), nested)

        status, listing = call("GET", "/api/files")
        items = (listing or {}).get("items") or []
        step("3.7", "断链的记录**还在**（对账不替人删记录）", len(items) == 1,
             f"/api/files -> {len(items)} 条")

        stop()

        # ── 第 4 段：日志与巡检 ──
        print("\n  第 4 段：日志里能不能看见，巡检里能不能看见")

        log_dir = os.path.join(data, "logs")
        text = ""
        for name in sorted(os.listdir(log_dir)):
            if name.startswith("log-"):
                with open(os.path.join(log_dir, name), encoding="utf-8") as f:
                    text += f.read()

        groomed = [l for l in text.splitlines() if "文件仓库对账" in l]
        lost_lines = [l for l in text.splitlines() if "断链" in l]

        step("4.1", "日志里有回收记录", bool(groomed),
             (groomed[0][:170] + "…") if groomed else "（没找到）")
        step("4.2", "日志里点名了断链", bool(lost_lines),
             (lost_lines[0][:170] + "…") if lost_lines else "（没找到）")

        env = dict(os.environ)
        env["Server__Storage__DataDir"] = data
        env["Server__Http__Port"] = str(http_port)

        out = subprocess.run([EXE, "--health"], capture_output=True, env=env, timeout=180)
        report = out.stdout.decode("utf-8", errors="replace")

        kv = {}
        for line in report.splitlines():
            if "=" in line and not line.startswith(" "):
                k, _, v = line.partition("=")
                kv[k.strip()] = v.strip()

        step("4.3", "--health 报出断链 1 条", kv.get("files.lost") == "1", f"files.lost={kv.get('files.lost')}")
        step("4.4", "--health 报出孤儿已清零", kv.get("files.orphans") == "0", f"files.orphans={kv.get('files.orphans')}")
        # 只数文件：repo 里还躺着第 3.6 步那个"手工备份"子目录，它不是文件。
        disk_files = sorted(
            f for f in os.listdir(repo) if os.path.isfile(os.path.join(repo, f)))

        # ⚠️ 这里是 1/1 而不是 0/1 —— 因为"未超龄的孤儿"（第 3.3 步那个）本来就该留在盘上。
        #    所以断言不能停在"两个数不等"，要证明**留着的是哪一个**：
        #    盘上只剩 f_orphan_fresh.bin，而记录指向的是那个已被回收的真文件名。
        step("4.5", "--health 把库记录数与盘上文件数分开报，且两者指向的不是同一个文件",
             kv.get("files.db.count") == "1"
             and kv.get("files.disk.count") == "1"
             and disk_files == ["f_orphan_fresh.bin"],
             f"db={kv.get('files.db.count')} disk={kv.get('files.disk.count')} 盘上={disk_files}")
        step("4.6", "--health 因此记了告警（退出码 1）", out.returncode == 1,
             f"exit={out.returncode} health={kv.get('health')}")

        print("\n  " + "─" * 62)
        passed = sum(1 for r in RESULTS if r[2])
        print(f"  结论：{passed} / {len(RESULTS)} 通过")
        if passed != len(RESULTS):
            print("  ❌ 有未通过项，上面标 FAIL 的就是")
            return 1
        print("  ✅ 对账行为与设计一致：孤儿回收、断链只报、子目录不碰")
        return 0

    finally:
        stop()
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
