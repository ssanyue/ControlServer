#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
测试结果校验：<b>「失败 0」不等于「过」，要「总计 == 基线」</b>。

为什么需要这个脚本
    本仓历史上出现过一种最难察觉的假绿：**回归跑到一半中止了，但输出仍然打印"已通过"**。
    只 grep "失败: 0" 会把它当成绿的 —— 因为它确实没报失败，它只是没跑完。
    唯一可靠的判据是"这一次真的跑了 N 条，而 N 等于上一次全绿时的 N"。
    少一条就是有东西没跑（或被静默跳过），多一条说明基线该更新了。

用法
    python deploy/verify/check-tests.py                 # 跑全部并比对
    python deploy/verify/check-tests.py --only core     # 只跑一组
    python deploy/verify/check-tests.py --update        # 全绿后把当前数字写进基线
    python deploy/verify/check-tests.py --no-run        # 只解析上一次留在 .build/trx 的结果

输出契约（与 --health 一致）
    一行一项 键=值，键名全 ASCII，退出码 0=全绿 / 1=有失败或总计不符 / 2=跑不起来。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASELINE = Path(__file__).resolve().parent / "baseline.json"
TRX_DIR = ROOT / ".build" / "trx"

# 名字 → 项目文件。名字进基线文件，改了会让"总计对不上"变成"项目找不到"，所以别随手改。
PROJECTS = {
    "core": ("ControlServer.Core.Tests", "tests/ControlServer.Core.Tests/ControlServer.Core.Tests.csproj"),
    "agent": ("ControlServer.Agent.Tests", "tests/ControlServer.Agent.Tests/ControlServer.Agent.Tests.csproj"),
    "integration": (
        "ControlServer.Server.IntegrationTests",
        "tests/ControlServer.Server.IntegrationTests/ControlServer.Server.IntegrationTests.csproj",
    ),
}

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def parse_trx(path: Path) -> dict:
    """从 TRX 里取计数。取不到就抛 —— 缺一份结果文件等同于"没跑"。"""
    root = ET.parse(path).getroot()
    counters = root.find(".//t:ResultSummary/t:Counters", TRX_NS)

    if counters is None:
        raise RuntimeError(f"{path.name} 里没有 ResultSummary/Counters —— 这份结果不完整")

    def num(name: str) -> int:
        return int(counters.get(name) or 0)

    failed_names = []
    for result in root.findall(".//t:Results/t:UnitTestResult", TRX_NS):
        if (result.get("outcome") or "").lower() == "failed":
            failed_names.append(result.get("testName") or "(未命名用例)")

    return {
        "total": num("total"),
        "passed": num("passed"),
        "failed": num("failed"),
        "skipped": num("notExecuted"),
        "failedNames": failed_names,
    }


def run_project(key: str, dotnet: str) -> dict:
    _name, project = PROJECTS[key]

    TRX_DIR.mkdir(parents=True, exist_ok=True)

    trx = TRX_DIR / f"{key}.trx"

    if trx.exists():
        trx.unlink()

    cmd = [
        dotnet, "test", str(ROOT / project),
        "-c", "Debug",
        "--logger", f"trx;LogFileName={trx.name}",
        "--results-directory", str(TRX_DIR),
        "--nologo",
    ]

    print(f"run.{key}=starting", flush=True)

    proc = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace")

    # ⚠️ **不按退出码判**：dotnet test 在"有测试失败"与"编译失败"时都给非零，
    #    而我们要分开报。真正的判据是 TRX 里的计数。
    if not trx.exists():
        tail = (proc.stdout or "")[-1500:] + (proc.stderr or "")[-800:]
        raise RuntimeError(f"{key} 没有产出 TRX（多半是编译失败）。输出末尾：\n{tail}")

    return parse_trx(trx)


def main() -> int:
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--only", default="", help="逗号分隔：core,agent,integration")
    ap.add_argument("--update", action="store_true", help="全绿后把当前总计写进基线")
    ap.add_argument(
        "--allow-shrink",
        action="store_true",
        help="配合 --update：确信用例总数变少是自己删的，不是没跑",
    )
    ap.add_argument("--no-run", action="store_true", help="只解析已有 TRX，不重新跑")
    args = ap.parse_args()

    if sys.version_info < (3, 8):
        print("error=python_too_old")
        print(f"hint=需要 Python 3.8+；当前 {sys.version.split()[0]}（{sys.executable}）")
        return 2

    dotnet = os.environ.get("DOTNET") or "dotnet"

    keys = [k.strip() for k in args.only.split(",") if k.strip()] or list(PROJECTS)

    unknown = [k for k in keys if k not in PROJECTS]
    if unknown:
        print(f"error=unknown_project:{','.join(unknown)}")
        print(f"hint=可用：{','.join(PROJECTS)}")
        return 2

    baseline = {}
    if BASELINE.exists():
        baseline = json.loads(BASELINE.read_text(encoding="utf-8")).get("projects", {})

    results = {}
    failures = []
    mismatches = []

    for key in keys:
        try:
            results[key] = parse_trx(TRX_DIR / f"{key}.trx") if args.no_run else run_project(key, dotnet)
        except Exception as ex:  # noqa: BLE001 —— 这里就是要接住一切并转成可读结论
            print(f"error.{key}=not_run")
            print(f"detail.{key}={str(ex).splitlines()[0]}")
            failures.append(f"{key}: 没跑起来")
            continue

        name, _ = PROJECTS[key]
        r = results[key]

        print(f"tests.{key}.total={r['total']}")
        print(f"tests.{key}.passed={r['passed']}")
        print(f"tests.{key}.failed={r['failed']}")
        print(f"tests.{key}.skipped={r['skipped']}")

        if r["failed"]:
            failures.append(f"{key}: {r['failed']} 条失败")
            for n in r["failedNames"][:5]:
                print(f"failed.{key}={n}")

        expected = baseline.get(name)

        if expected is not None and r["total"] != expected:
            # 🔥 这条才是本脚本存在的理由：失败 0 但总数变少 = 有东西没跑。
            delta = r["total"] - expected
            mismatches.append((key, r["total"], expected, delta))
            print(f"mismatch.{key}=total:{r['total']} baseline:{expected} delta:{delta:+d}")

            if delta < 0:
                print(f"hint.{key}=少跑了用例：检查是否整类被跳过、[Collection] 是否吞掉了并行、或测试项目没编进去")

    if args.update:
        # 🔥 只在"真的跑完了并且没红"的时候允许写基线。
        #    反过来的做法（不管三七二十一写进去）会把"少跑了一批用例"固化成基线，
        #    从此以后每次检查都是绿的 —— 那比没有这个脚本更坏。
        if failures:
            print("update=refused")
            print("hint=有失败或没跑起来的项目，拒绝更新基线")
            return 1

        shrinking = [m for m in mismatches if m[3] < 0]

        if shrinking and not args.allow_shrink:
            print("update=refused")
            for key, total, expected, delta in shrinking:
                print(f"shrink.{key}=total:{total} baseline:{expected} delta:{delta:+d}")
            print("hint=用例总数变少了。确认这是自己删的，再补 --allow-shrink；否则先查为什么少跑")
            return 1

        for key, r in results.items():
            baseline[PROJECTS[key][0]] = r["total"]

        BASELINE.write_text(
            json.dumps({"generatedAt": "auto", "projects": baseline}, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

        for key, total, expected, delta in mismatches:
            print(f"baseline.{key}={expected}->{total} ({delta:+d})")

        print("baseline=updated")
        return 0

    if not failures and not mismatches:
        print("result=ok")
        print(f"hint.baseline={BASELINE}")
        return 0

    print(f"result=failed count={len(failures) + len(mismatches)}")
    for f in failures:
        print(f"reason={f}")
    for key, total, expected, delta in mismatches:
        print(f"reason={key}: 总计 {total} != 基线 {expected}（{delta:+d}）")

    return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(2)
