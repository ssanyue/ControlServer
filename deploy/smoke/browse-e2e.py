#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""客户机目录浏览端到端冒烟（M4.4+ · 2026-10-13 整盘浏览，#220 → #221 并入冒烟）。

验的是"选文件存放路径 / 回传源路径"那条链路：网页 → 服务端 →（长连接）→ 客户机。

在客户机的磁盘上造几个目录/文件，然后逐条验证：

  ① 顶层（不带 path）= 这台机器的**全部盘符**（"此电脑"）；
  ② 进任意目录 = 看得到造的目录与文件（目录在前、文件在后）；
  ③ 进子目录 = 看得到里面的文件；`parentPath` 指回上一级；
  ④ 盘根的 `parentPath` 必须是 **null**（"没有上一级了"）；
  ⑤ 🔥 **白名单之外的目录照样能浏览**（2026-10-13 放开为整盘浏览）——
     当年这里断言的是 `denied`；能不能写是"传输"那一步的事，不是浏览这一步；
  ⑥ 指向一个文件 → `notdir`；
  ⑦ 不存在的目录 / 非法路径 → `notfound`（都给人话，不抛 500）；
  ⑧ 客户机停机后 → `offline`。

⚠️ **中心由 `run-all.py` 起好**，本脚本只负责客户机那一侧。

用法（由 run-all.py 调用）：
    python deploy/smoke/browse-e2e.py <base_url> <work_dir> <agent_ui_port>

客户机 exe：`CS_AGENT_EXE` 环境变量（打包产物），否则退回本仓库的调试构建。
"""

import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
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


def http(method, url, body=None, opener=None, timeout=10):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
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


def main(argv):
    if len(argv) < 3:
        print("[X] 用法：browse-e2e.py <base_url> <work_dir> <agent_ui_port>")
        return 2

    base = argv[0]
    work_dir = argv[1]
    agent_ui_port = int(argv[2])

    if not os.path.isfile(AGENT_EXE):
        print(f"[X] 找不到客户机 exe：{AGENT_EXE}")
        print("  先 dotnet build，或设 CS_AGENT_EXE 指向打包产物。")
        return 2

    if os.path.isdir(work_dir):
        shutil.rmtree(work_dir, ignore_errors=True)
    os.makedirs(work_dir, exist_ok=True)

    agent_dir = os.path.join(work_dir, "agent")

    # ── 造一个"客户机上的白名单目录" ──
    whitelist = os.path.join(work_dir, "whitelist")
    subdir = os.path.join(whitelist, "logs")
    os.makedirs(subdir, exist_ok=True)

    log_file = os.path.join(subdir, "app.log")
    with open(log_file, "w", encoding="utf-8") as f:
        f.write("hello\n")

    loose_file = os.path.join(whitelist, "readme.txt")
    with open(loose_file, "w", encoding="utf-8") as f:
        f.write("readme\n")

    # 下发（可写）白名单之外的一个目录（用来验"浏览不受这份名单限制"）。
    outside = os.path.join(work_dir, "outside")
    os.makedirs(outside, exist_ok=True)

    env = dict(os.environ)
    # 调试构建的 apphost 需要 DOTNET_ROOT 才不会回退去找 Program Files 里的运行时。
    env.setdefault("DOTNET_ROOT", r"C:\Users\IYLONG\.workbuddy\binaries\dotnet8")

    print("== 启动客户机（下发白名单 = whitelist 目录；中心由 run-all 起好）==")
    agent = subprocess.Popen(
        [
            AGENT_EXE,
            f"--Agent:StateDirectory={agent_dir}",
            f"--Agent:ServerUrl={base}",
            f"--Agent:LocalUi:Port={agent_ui_port}",
            "--Agent:Telemetry:Enabled=false",
            "--Agent:Transfer:Enabled=true",
            # 只配**下发**白名单。回传（读）2026-10-15 起不限制目录，没有对应开关可配 ——
            # 从前那行 `--Agent:Transfer:OutboxDirectories:0=…` 已随机制一起删除。
            f"--Agent:Transfer:InboxDirectories:0={whitelist}",
        ],
        env=env, cwd=REPO,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )

    try:
        wait_for(lambda: http("GET", base + "/api/agents/1/config")[0] in (401, 403, 200), 40, "中心 HTTP 就绪")

        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(CookieJar()))
        st, _ = http("POST", base + "/api/auth/login", {"userName": "admin", "password": "admin123"}, opener=opener)
        check("管理员登录", st == 200, f"HTTP {st}")

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

        def browse(path=None):
            url = base + f"/api/agents/{agent_id}/browse"
            if path is not None:
                url += "?path=" + urllib.parse.quote(path, safe="")
            return http("GET", url, opener=opener, timeout=30)

        # 等客户机把浏览能力接上（连上长连接后才装配）。
        def browse_top():
            st, body = browse()
            if st == 200 and isinstance(body, dict) and body.get("ok"):
                return body
            return None

        top = wait_for(browse_top, 60, "客户机目录浏览可用")
        check("顶层浏览成功", top is not None, json.dumps(top, ensure_ascii=False)[:160] if top else "")
        if top is None:
            return 1

        # ── ① 顶层 = 盘符列表（"此电脑"）──
        check("顶层 ok = true", top["ok"] is True)
        check("顶层 requestedPath = null", top["requestedPath"] is None, str(top["requestedPath"]))
        check("顶层 parentPath = null", top["parentPath"] is None)
        roots = top.get("roots") or []
        # 🔥 2026-10-13 起顶层是**盘符**，不再是白名单根目录。
        check("顶层列出盘符", len(roots) >= 1 and all(os.path.splitdrive(r)[1] in ("\\", "/") for r in roots),
              json.dumps(roots, ensure_ascii=False))
        check("分隔符是反斜杠", top["separator"] == "\\", str(top["separator"]))
        check("顶层条目数 = 盘符数", len(top["entries"]) == len(roots),
              f"{len(top['entries'])} vs {len(roots)}")
        check("顶层条目全是目录", all(e["isDirectory"] is True for e in top["entries"]))
        check("盘符显示名带类型说明（不只一个字母）",
              all(len(e["name"]) > len(e["path"]) for e in top["entries"]),
              json.dumps([e["name"] for e in top["entries"]], ensure_ascii=False))

        # ── ② 进工作目录（它不在盘根上 ⇒ parentPath 必须是它的真实上级）──
        st, root = browse(whitelist)
        check("进目录成功", st == 200 and root.get("ok"), json.dumps(root, ensure_ascii=False)[:160])
        names = [e["name"] for e in root["entries"]]

        check("目录看得到 logs 与 readme.txt", ("logs" in names) and ("readme.txt" in names), json.dumps(names, ensure_ascii=False))
        check("目录排在文件前面", names[0] == "logs" and names[-1] == "readme.txt", json.dumps(names, ensure_ascii=False))
        check("非盘根目录的 parentPath = 它的上级",
              root["parentPath"] is not None
              and os.path.normcase(root["parentPath"]) == os.path.normcase(os.path.dirname(whitelist)),
              str(root["parentPath"]))

        readme = next(e for e in root["entries"] if e["name"] == "readme.txt")
        # 实测大小而不是写死：Windows 文本模式会把 "\n" 写成 "\r\n"，字节数与 len("readme\n") 不等。
        check("文件条目带大小", readme["isDirectory"] is False and readme["size"] == os.path.getsize(loose_file),
              f"{readme['size']} vs {os.path.getsize(loose_file)}")
        check("文件条目带修改时间", readme["modifiedAt"] is not None, str(readme["modifiedAt"]))

        # ── ③ 进子目录 ──
        st, sub = browse(subdir)
        check("进子目录成功", st == 200 and sub.get("ok"), json.dumps(sub, ensure_ascii=False)[:160])
        check("子目录看得到 app.log", [e["name"] for e in sub["entries"]] == ["app.log"],
              json.dumps([e["name"] for e in sub["entries"]], ensure_ascii=False))
        check("子目录 parentPath 指回上一级",
              sub["parentPath"] is not None and os.path.normcase(sub["parentPath"]) == os.path.normcase(whitelist),
              str(sub["parentPath"]))

        # ── ④ 盘根没有上一级 ──
        drive_root = os.path.splitdrive(whitelist)[0] + "\\"
        st, at_root = browse(drive_root)
        check("盘根可浏览，且 parentPath = null（没有上一级）",
              st == 200 and at_root.get("ok") is True and at_root["parentPath"] is None,
              json.dumps(at_root, ensure_ascii=False)[:160])

        # ── ⑤ 🔥 整盘浏览：白名单之外照样看得到 ──
        #
        # 2026-10-13 之前这里断言的是 `denied`。产品决定改成"像远程资源管理器一样能挑目录"，
        # 所以现在必须能进 —— 能不能**写**是传输那一步的事（由客户机白名单拦）。
        for target, label in (
            (outside, "白名单外的目录"),
            (os.path.join(whitelist, "..", "outside"), "用 .. 走上去"),
            (os.path.join(subdir, "..", ".."), "多级 .. 走上去"),
        ):
            st, seen = browse(target)
            check(f"整盘可浏览：{label}",
                  st == 200 and seen.get("ok") is True,
                  json.dumps(seen, ensure_ascii=False)[:140])

        # `..` 必须被**解析**后再返回，否则上层拿到的路径与客户机写入校验用的路径对不上。
        st, resolved = browse(os.path.join(subdir, ".."))
        check("`..` 被解析成本身（不是原样返回）",
              st == 200 and resolved.get("ok") is True
              and os.path.normcase(resolved["requestedPath"]) == os.path.normcase(whitelist),
              str(resolved.get("requestedPath")))

        # ── ⑥ 指向文件 ──
        st, notdir = browse(log_file)
        check("指向文件 → notdir", st == 200 and notdir.get("ok") is False and notdir["status"] == "notdir",
              json.dumps(notdir, ensure_ascii=False)[:140])

        # ── ⑦ 不存在的目录 / 非法路径 ──
        st, missing = browse(os.path.join(whitelist, "no-such-dir"))
        check("不存在的目录 → notfound", st == 200 and missing.get("ok") is False and missing["status"] == "notfound",
              json.dumps(missing, ensure_ascii=False)[:140])

        st, illegal = browse("|:\\nope")
        check("非法路径 → notfound（给人话，不是 500）",
              st == 200 and illegal.get("ok") is False and illegal["status"] == "notfound"
              and bool(illegal.get("message")),
              json.dumps(illegal, ensure_ascii=False)[:140])

        # ── ⑧ 不存在这台机器 ──
        st, _ = http("GET", base + f"/api/agents/{agent_id + 999}/browse", opener=opener)
        check("机器不存在 → 404", st == 404, f"HTTP {st}")

        # ── ⑧ 客户机停机 → offline ──
        print("  —— 停掉客户机，验证离线时给的是 offline（不是 500）——")
        agent.terminate()
        try:
            agent.wait(timeout=15)
        except Exception:
            agent.kill()

        def offline_ok():
            code, body = browse()
            if code == 200 and isinstance(body, dict) and body.get("status") == "offline":
                return body
            return None

        off = wait_for(offline_ok, 30, "离线状态生效")
        check("客户机离线 → offline", off is not None, json.dumps(off, ensure_ascii=False)[:160] if off else "")
        if off:
            check("离线时 ok = false", off["ok"] is False)
            check("离线原因有人话", bool(off.get("message")), str(off.get("message")))

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
