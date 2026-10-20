#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""远控帧率实测（**按收帧增量算，不读滑窗均值**）。

本脚本回答三个问题：

  ① **这一档实际跑出多少帧** —— 用 `framesReceived` 的**增量 ÷ 实际耗时**。
     ⚠️ **不许用** `RemoteSessionStatsDto.Fps`：那是滑窗均值，切档后要好几秒才衰减完，
     切到 60 档立刻读会读到上一档的数字（M7.4 §3 那组作废数据的直接成因之一）。
  ② **画面到底在不在动** —— 平均帧大小 ＋ 每帧字节数。若 `KB/帧` 低到几百字节，
     说明桌面是静止的（DXGI 是增量投递，静止就没有新帧），这时测出来的帧率
     反映的是**退避后的轮询节奏**，不是上限。脚本会明确警告，不把这种数据当结论。
  ③ **被控端那三段耗时花在哪** —— 若本机就是被控端，顺带把托盘日志里的探针行摘出来
     （`[Stream] 探针 …` 均耗 抓屏 / 编码 / 写管道）。探针默认关闭，打开方式见
     `agents/ControlServer.Agent/Tasks/RemoteStreamProbe.cs`。

用法（被控端必须是**含着新构建**的进程，否则探针行不会出现）：

    # 造画面变化（另开一个窗口，DGXI 才有的抓）
    python deploy/smoke/remote-fps.py --quality smooth --seconds 20 --repeat 3

    # 三档一起对比
    python deploy/smoke/remote-fps.py --quality low,balanced,smooth --seconds 15

    # 顺带打印被控端探针（本机即被控端时才有意义）
    python deploy/smoke/remote-fps.py --quality smooth --show-probe

🔴 **它不造画面变化**。测上限时要另外保证桌面在变（例如播放视频、拖动窗口），
   否则得到的只是静止退避下的节奏。脚本会把 `KB/帧` 打出来，低于阈值时给出警告。
"""
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

# 低于这个平均帧大小就基本可以断定"桌面是静止的"：静止退避下中心也会收到零星帧，
# 但那是极小的 P 帧（几百字节）。真在动的画面，即使是纯色块也有几 KB。
STATIC_FRAME_KB = 2.0


class Api:
    def __init__(self, base: str) -> None:
        self._base = base.rstrip("/")
        self._opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
        )

    def call(self, method: str, path: str, body: dict | None = None) -> object:
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = urllib.request.Request(
            self._base + path,
            data=data,
            headers={"Content-Type": "application/json"},
            method=method,
        )
        with self._opener.open(request, timeout=10) as response:
            raw = response.read()
        return json.loads(raw) if raw else None

    def login(self, user: str, password: str) -> None:
        self.call("POST", "/api/auth/login", {"userName": user, "password": password})


def pick_agent(api: Api, wanted: str | None) -> dict:
    # ⚠️ `/api/agents` 返回的是分页信封（`{"items": [...]}`），不是裸数组；
    #    机器状态字段叫 `state`（`online`/`offline`…），显示名叫 `displayName`。
    payload = api.call("GET", "/api/agents")
    agents = payload.get("items", []) if isinstance(payload, dict) else (payload or [])
    if not agents:
        raise SystemExit("中心没有任何 Agent 记录。")

    online = [a for a in agents if str(a.get("state", "")).lower() == "online"]
    pool = online or agents

    if wanted:
        pool = [
            a
            for a in agents
            if wanted == str(a.get("id"))
            or wanted.lower() in str(a.get("displayName") or "").lower()
            or wanted.lower() in str(a.get("hostName") or "").lower()
        ]
        if not pool:
            raise SystemExit(f"找不到匹配 {wanted!r} 的机器。")

    return pool[0]


def open_session(api: Api, agent_id: int, max_fps: int | None, quality: str) -> str:
    body: dict = {"agentId": agent_id, "quality": quality}
    if max_fps:
        body["maxFps"] = max_fps

    session = api.call("POST", "/api/remote/sessions", body)
    return str(session["sessionId"])


def apply_viewport(api: Api, session_id: str, viewport: str) -> None:
    """
    把这一路**只编左上角这一块**（M5.5 的 ROI）。

    🔥 **它是"在 2160×1440 的屏上量 1080p"的唯一办法**：抓屏器的区域编码是 1:1 的
    （`IRegionSurfaceEncoder`），所以给 1920×1080 得到的就是一块 **1920×1080 的编码** ——
    与"那台机器本来就是 1080p 屏"在编码器看来是同一件事。
    靠"改系统分辨率"去量会动到正在用这台机器的人的桌面（所有窗口重排），不是可重复的做法。
    """
    width, height = (int(v) for v in viewport.lower().split("x"))
    api.call(
        "POST",
        f"/api/remote/sessions/{session_id}/viewport",
        {"x": 0, "y": 0, "width": width, "height": height},
    )


def sample(api: Api, session_id: str, seconds: float) -> dict:
    """在一段**干净的窗口**里量：先取起点，等够时间，再取终点。"""
    before = api.call("GET", f"/api/remote/sessions/{session_id}")
    started = time.monotonic()
    frames0 = int((before.get("stats") or {}).get("framesReceived") or 0)
    bytes0 = int((before.get("stats") or {}).get("bytesReceived") or 0)

    time.sleep(seconds)

    after = api.call("GET", f"/api/remote/sessions/{session_id}")
    elapsed = time.monotonic() - started
    frames1 = int((after.get("stats") or {}).get("framesReceived") or 0)
    bytes1 = int((after.get("stats") or {}).get("bytesReceived") or 0)

    delta_frames = frames1 - frames0
    delta_bytes = bytes1 - bytes0

    return {
        "seconds": elapsed,
        "frames": delta_frames,
        "fps": delta_frames / elapsed if elapsed > 0 else 0.0,
        "kb_per_frame": (delta_bytes / 1024.0 / delta_frames) if delta_frames > 0 else 0.0,
        "mbps": (delta_bytes * 8 / elapsed / 1e6) if elapsed > 0 else 0.0,
        "dto_fps": (after.get("stats") or {}).get("fps"),
        "codec": after.get("codec"),
        "target_fps": after.get("targetFps"),
        "dropped": (after.get("stats") or {}).get("droppedFrames"),
        "agent_fps": (after.get("agentStats") or {}).get("fps"),
        "state": after.get("state"),
    }


def probe_lines(log_path: Path, since: float) -> list[str]:
    """摘出托盘日志里 `started` 之后写的探针行。"""
    try:
        text = log_path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []

    return [line for line in text.splitlines() if "[Stream] 探针" in line][-6:]


def main() -> int:
    parser = argparse.ArgumentParser(description="远控帧率实测（按收帧增量）")
    parser.add_argument("--base", default="http://127.0.0.1:8899", help="中心地址")
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="admin123")
    parser.add_argument("--agent", default=None, help="机器 id 或名称片段（默认第一台在线）")
    parser.add_argument("--quality", default="smooth", help="low,balanced,high,smooth 可逗号分隔")
    parser.add_argument("--max-fps", type=int, default=None, help="覆盖档位帧率（可选）")
    parser.add_argument("--warmup", type=float, default=8.0, help="切档后先等这么久再采样")
    parser.add_argument("--seconds", type=float, default=15.0, help="每档采样时长")
    parser.add_argument("--repeat", type=int, default=1, help="每档重复几轮（取中位数）")
    parser.add_argument("--show-probe", action="store_true", help="打印托盘日志里的探针行")
    parser.add_argument(
        "--viewport",
        default=None,
        help="只编左上角这一块（如 1920x1080）—— 用它在一台更大的屏上量 1080p 的帧率",
    )
    parser.add_argument("--keep", action="store_true", help="结束时保留会话（默认关掉）")
    args = parser.parse_args()

    api = Api(args.base)
    try:
        api.login(args.user, args.password)
    except urllib.error.HTTPError as error:
        print(f"登录失败：HTTP {error.code}", file=sys.stderr)
        return 2

    agent = pick_agent(api, args.agent)
    print(f"被控机：#{agent.get('id')} {agent.get('displayName')}（{agent.get('state')}，v{agent.get('agentVersion')}）")

    tray_log = Path(os.environ.get("TEMP", "/tmp")) / "ControlServer.Agent.Tray.log"
    if args.show_probe:
        print(f"托盘日志：{tray_log}（存在={tray_log.exists()}）")

    print()
    print(f"{'档位':<10}{'目标':>4}{'实测 fps':>10}{'KB/帧':>9}{'Mbps':>8}{'codec':>8}  备注")
    print("-" * 68)

    session_id: str | None = None

    try:
        for quality in [q.strip() for q in args.quality.split(",") if q.strip()]:
            # 🔥 **每档都新开一路会话**，不用 `POST /quality` 原地切换。
            #    2026-10-19 实测：原地切档之后被控端日志里**没有任何**帧率变更记录，
            #    而实测帧率仍等于开局那一档（12 档切 30 档，还是 5.5 fps）。
            #    中心的 DTO 却已经把 targetFps 显示成 30 —— 典型的"看着生效了"。
            #    在那一层修好之前，用"新开会话"测才拿得到真数字。
            if session_id is not None:
                api.call("DELETE", f"/api/remote/sessions/{session_id}")

            session_id = open_session(api, int(agent["id"]), args.max_fps, quality)
            print(f"  （{quality} 会话：{session_id}）")

            if args.viewport:
                # ⚠️ 要在**采样之前**设：ROI 会重建编码器（≈0.3 s），
                #    放在 warmup 之后会把那段空窗算进帧率。
                apply_viewport(api, session_id, args.viewport)
                print(f"  （只编 {args.viewport}）")

            time.sleep(args.warmup)

            runs = [sample(api, session_id, args.seconds) for _ in range(max(1, args.repeat))]
            runs.sort(key=lambda r: r["fps"])
            middle = runs[len(runs) // 2]

            note = ""
            if middle["kb_per_frame"] < STATIC_FRAME_KB:
                note = "画面像静止（增量投递下无新帧），此值不是上限"

            print(
                f"{quality:<10}{middle['target_fps'] if middle['target_fps'] is not None else '-':>4}"
                f"{middle['fps']:>10.1f}{middle['kb_per_frame']:>9.1f}{middle['mbps']:>8.1f}"
                f"{str(middle['codec']):>8}  {note}"
            )

            if args.repeat > 1:
                spread = f"{runs[0]['fps']:.1f}~{runs[-1]['fps']:.1f}"
                print(f"{'':<10}{'':>4}{'（' + str(args.repeat) + ' 轮 ' + spread + '）':>10}")

        if args.show_probe:
            lines = probe_lines(tray_log, time.time() - 300)
            print()
            if lines:
                print("被控端探针（最近 6 行）：")
                for line in lines:
                    print("  " + line)
            else:
                print("托盘日志里没有探针行 —— 说明被控端进程不是含探针的构建，或探针没打开。")
    finally:
        if not args.keep:
            try:
                api.call("DELETE", f"/api/remote/sessions/{session_id}")
            except Exception as error:  # noqa: BLE001 - 收尾失败不该盖住上面的测量结果
                print(f"（结束会话失败：{error}）", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
