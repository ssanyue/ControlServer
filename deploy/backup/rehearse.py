r"""M6.1 验收演练：备份 -> 删掉整个数据目录 -> 恢复 -> 逐项比对。

用法：
    python deploy/backup/rehearse.py [源数据目录]
    CONTROLSERVER_EXE=<exe 路径> python deploy/backup/rehearse.py "C:\ProgramData\ControlServer"

它刻意用**现场数据目录的副本**：只有真实形态的库（13 条迁移历史、真实用户、真实审计、
真实文件仓库）才能证明"恢复回来的是一份能用的数据"，而不是一个刚好能打开的空壳。

⚠️ 脚本会把复制出来的那份整个删掉（这就是演练的核心动作），所以源目录只读不写。
"""

import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.environ.get(
    "CONTROLSERVER_EXE",
    os.path.join(REPO, "src", "ControlServer.Server", "bin", "Debug", "net8.0", "ControlServer.Server.exe"),
)

# 源数据目录：**必须是现场数据的一份副本，不是现场目录本身**。
# 演练会把它整个删掉，直接指着现场就是一次真的数据丢失。
LIVE_SRC = sys.argv[1] if len(sys.argv) > 1 else r"C:\ProgramData\ControlServer"

WORK = os.path.join(REPO, ".tools", "rehearsal")
LIVE = os.path.join(WORK, "live")
BACKUP = os.path.join(WORK, "backup-1")
RESTORED = os.path.join(WORK, "restored-check")


def decode(raw: bytes) -> str:
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def tree_hash(root: str) -> str:
    """目录内容摘要：按相对路径排序后把 (路径, 大小, 哈希) 拼起来再哈希。"""
    lines = []
    for base, _, files in os.walk(root):
        for name in sorted(files):
            full = os.path.join(base, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            lines.append(f"{rel}|{os.path.getsize(full)}|{sha256(full)}")
    return hashlib.sha256("\n".join(sorted(lines)).encode()).hexdigest().upper()


def query(db: str, sql: str):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        return con.execute(sql).fetchone()[0]
    finally:
        con.close()


def snapshot(label: str, root: str) -> dict:
    db = os.path.join(root, "control.db")
    files_dir = os.path.join(root, "files")
    return {
        "① 用户数": query(db, "select count(*) from users"),
        "② 配置文档数": query(db, "select count(*) from config_documents"),
        "③ 审计条数": query(db, "select count(*) from audit_logs"),
        "④ 文件仓库摘要": tree_hash(files_dir) if os.path.isdir(files_dir) else "（无 files 目录）",
        "⑤ license.lic 哈希": (
            sha256(os.path.join(root, "license.lic"))
            if os.path.isfile(os.path.join(root, "license.lic"))
            else "（无授权文件）"
        ),
        "⑥ 迁移历史条数": query(db, "select count(*) from __EFMigrationsHistory"),
    }


def run(*args):
    env = os.environ.copy()
    env["Server__Storage__DataDir"] = LIVE
    started = time.time()
    result = subprocess.run(
        [EXE, *args], cwd=REPO, env=env, capture_output=True, timeout=300
    )
    elapsed = time.time() - started
    return result.returncode, decode(result.stdout), decode(result.stderr), elapsed


def main():
    if os.path.isdir(WORK):
        shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)

    print("=" * 78)
    print("M6.1 备份 / 恢复演练")
    print("=" * 78)

    # ── 0. 把现场数据目录整份复制过来当"活库" ──
    print(f"\n[0] 复制现场数据目录\n    {LIVE_SRC}\n    -> {LIVE}")
    shutil.copytree(LIVE_SRC, LIVE)
    before = snapshot("备份前", LIVE)
    print("\n    备份前的五项：")
    for k, v in before.items():
        print(f"      {k:22} {v}")

    # ── 1. 备份（允许服务在跑；这里模拟"库是热的"）──
    print(f"\n[1] --backup {BACKUP}")
    code, out, err, elapsed = run("--backup", BACKUP)
    print(f"    退出码 {code}，耗时 {elapsed:.2f}s")
    if out.strip():
        print("\n".join("    | " + line for line in out.rstrip().splitlines()))
    if err.strip():
        print("\n".join("    ! " + line for line in err.rstrip().splitlines()))
    if code != 0:
        print("\n❌ 备份失败，演练中止")
        return 1

    # ── 2. 把整个数据目录删掉 ──
    print(f"\n[2] 删除整个数据目录：{LIVE}")
    shutil.rmtree(LIVE)
    print(f"    存在吗？ {os.path.exists(LIVE)}")

    # ── 3. 从备份恢复 ──
    print(f"\n[3] --restore {BACKUP} --force")
    code, out, err, elapsed = run("--restore", BACKUP, "--force")
    print(f"    退出码 {code}，耗时 {elapsed:.2f}s")
    if out.strip():
        print("\n".join("    | " + line for line in out.rstrip().splitlines()))
    if err.strip():
        print("\n".join("    ! " + line for line in err.rstrip().splitlines()))
    if code != 0:
        print("\n❌ 恢复失败")
        return 1

    # ── 4. 五项逐项比对 ──
    after = snapshot("恢复后", LIVE)
    print("\n" + "=" * 78)
    print("五项逐项比对")
    print("=" * 78)
    print(f"  {'项目':22} {'备份前':>40}   {'恢复后':>40}   判定")
    all_ok = True
    for key in before:
        same = before[key] == after[key]
        all_ok = all_ok and same
        print(f"  {key:22} {str(before[key]):>40}   {str(after[key]):>40}   {'✅' if same else '❌'}")

    # ── 5. 附带检查：私钥没被恢复走、也没被删掉 ──
    print("\n" + "=" * 78)
    print("附带检查")
    print("=" * 78)
    pfx = os.path.join(LIVE, "https", "ca.pfx")
    in_backup = os.path.join(BACKUP, "https", "ca.pfx")
    pub_in_backup = os.path.join(BACKUP, "https", "controlserver-ca.crt")
    checks = [
        ("备份里没有 ca.pfx（私钥不外带）", not os.path.exists(in_backup)),
        ("备份里有 controlserver-ca.crt（公钥可分发）", os.path.exists(pub_in_backup)),
        ("备份里没有 downloads/", not os.path.exists(os.path.join(BACKUP, "downloads"))),
        ("manifest 存在", os.path.exists(os.path.join(BACKUP, "manifest.json"))),
    ]
    for label, ok in checks:
        print(f"  {'✅' if ok else '❌'} {label}")
        all_ok = all_ok and ok

    # manifest 里 skipped 的措辞
    with open(os.path.join(BACKUP, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)
    print(f"\n  manifest: {len(manifest['files'])} 个文件, "
          f"{len(manifest['skipped'])} 项跳过, 格式版本 {manifest['formatVersion']}")
    for s in manifest["skipped"]:
        print(f"    ✗ {s['path']}  —— {s['reason'][:48]}…")

    # ── 6. 恢复出来的库真的能用吗（迁移历史 + 完整性检查）──
    db = os.path.join(LIVE, "control.db")
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    integrity = con.execute("pragma integrity_check").fetchone()[0]
    con.close()
    print(f"\n  integrity_check = {integrity}")
    all_ok = all_ok and integrity == "ok"

    # 恢复后不应该留下"属于旧库"的 -wal
    for suffix in ("-wal", "-shm"):
        stale = os.path.exists(db + suffix)
        print(f"  {'❌' if stale else '✅'} 恢复后没有遗留 control.db{suffix}")
        all_ok = all_ok and not stale

    print("\n" + "=" * 78)
    print("✅ 全部通过" if all_ok else "❌ 有不一致项，见上")
    print("=" * 78)
    return 0 if all_ok else 2


if __name__ == "__main__":
    code = main()

    # 演练产物里有真实数据。成功就清掉；失败则保留，否则"红了但现场没了"只能重跑。
    # （有意的：不用 try/finally 包 sys.exit —— 那会在清理抛异常时吞掉真实退出码。）
    if code == 0:
        shutil.rmtree(WORK, ignore_errors=True)
    else:
        print(f"\n（演练产物保留在 {WORK}，供排查）")

    sys.exit(code)
