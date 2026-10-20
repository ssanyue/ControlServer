#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · 管理面 API 冒烟（M2-G1 / G6 / G7）

补齐此前只靠 .tmp-*.py 临时脚本覆盖的三块新端点：

  G1 图片上传 / 删除   POST、DELETE /api/config/images
  G6 指令库            /api/commands（CRUD + 四类拒绝）
  G6 定时              /api/timers（触发点形状 + 手动触发 + 幂等闸门）
  G7 系统              /api/system/info、/api/system/timesync、/api/system/address
  G9 单条动作          /api/actions/execute 的 `actionId` 分支（只发一条，不多发）

与另两个冒烟脚本的分工：
  · pairing-smoke.py      对接码三级匹配（直落 SQLite + 真 UDP 收发）
  · web-console-smoke.py  配置导入导出往返（旧版 .controlbak 兼容）
  · admin-api-smoke.py    管理面端点（本文件）—— 含**真 UDP sink**，
                          所以「定时触发」不是只看返回值，而是确认字节真的出了网。

用法：
    python deploy/smoke/admin-api-smoke.py [baseUrl] [udpSinkPort]

    baseUrl      默认 http://127.0.0.1:18099
    udpSinkPort  默认 18097（本脚本自己 bind 这个端口收包）

约定：
  · 所有调用绕过本机 HTTP 代理（Git Bash 环境常配了代理，访问 127.0.0.1 会 502）。
  · 脚本会**改配置与指令库**，最后一步恢复出厂 + 删掉自己建的指令。
    请只对演示实例 / 临时实例跑，别对着正在用的生产实例跑。
"""

import base64
import http.cookiejar
import json
import re
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18099").rstrip("/")
SINK_PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 18097

# 一张真实的 1x1 PNG —— 让「魔数嗅探」这道闸认它是位图。
PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="
)

_jarp = http.cookiejar.CookieJar()
_opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),             # 绕过代理
    urllib.request.HTTPCookieProcessor(_jarp),   # 保持登录 Cookie
)

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))


def raw_request(method, path, data=None, headers=None):
    """返回 (status, body_bytes)。不做任何解析 —— multipart / 204 都要用。"""
    req = urllib.request.Request(BASE + path, data=data, headers=headers or {}, method=method)
    try:
        with _opener.open(req, timeout=60) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def call(method, path, body=None):
    """JSON 调用的便利包装；返回 (status, 解析后的 JSON 或纯文本)。"""
    data, headers = None, {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"

    status, content = raw_request(method, path, data, headers)
    try:
        return status, json.loads(content.decode("utf-8"))
    except Exception:
        return status, content.decode("utf-8", errors="replace")


def upload(filename, content, content_type="image/png", field="file"):
    """按 multipart/form-data 上传一个文件（前端 api.uploadImage 的等价物）。"""
    boundary = "----cs-smoke-" + uuid.uuid4().hex
    body = b"".join(
        [
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="{field}"; filename="{filename}"\r\n'.encode(),
            f"Content-Type: {content_type}\r\n\r\n".encode(),
            content,
            f"\r\n--{boundary}--\r\n".encode(),
        ]
    )
    return raw_request(
        "POST",
        "/api/config/images",
        body,
        {
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "Content-Length": str(len(body)),
        },
    )


def as_json(content):
    try:
        return json.loads(content.decode("utf-8"))
    except Exception:
        return content.decode("utf-8", errors="replace")


def current_version():
    _, cfg = call("GET", "/api/config")
    return cfg.get("version")


def nid():
    return str(uuid.uuid4())


def new_action(name, *, data_type="UTF-8", data="", port=None, protocol="UDP"):
    return {
        "id": nid(),
        "name": name,
        "protocol": protocol,
        "targetIp": "127.0.0.1",
        "targetPort": SINK_PORT if port is None else port,
        "dataType": data_type,
        "dataContent": data,
        "delayMs": 0,
        "pairingCode": "",
        "pairingCodeType": "String",
    }


# ═══════════════════════════════════════════════════════════════
# 前置：登录（其余端点全部要求已认证）
# ═══════════════════════════════════════════════════════════════

print("════════ 管理面 API 冒烟（G1 / G6 / G7）════════")
print(f"      BASE = {BASE}    UDP sink = 127.0.0.1:{SINK_PORT}")

status, _ = call("GET", "/api/config")
check("未登录访问受保护接口 → 401", status == 401, f"实得 {status}")

status, body = call("POST", "/api/auth/login", {"userName": "admin", "password": "admin123"})
check("默认管理员登录成功", status == 200, f"实得 {status} {body}")

# ═══════════════════════════════════════════════════════════════
# G7 · 系统信息（先读，后面要用服务端时间构造月度计划）
# ═══════════════════════════════════════════════════════════════

print("\n[G7] 运行诊断与时间同步")

status, info = call("GET", "/api/system/info")
check("GET /api/system/info → 200", status == 200, f"实得 {status}")
REQUIRED = {
    "product", "version", "machineName", "osPlatform", "serverTime", "timeZone",
    "httpPort", "pairingPort", "listenerRunning", "uptimeSeconds", "recentLogCount",
    "dataDirectory", "timeSync", "timers",
}
missing = REQUIRED - set(info.keys()) if isinstance(info, dict) else REQUIRED
check("系统信息字段齐全", not missing, f"缺 {sorted(missing)}")
check(
    "刻意不含机器码（机器码只走 AdminOnly 的 machine-code）",
    "machineCode" not in info and "fingerprint" not in info,
    f"实得键 {[k for k in info if 'achine' in k or 'ingerprint' in k]}",
)
check(
    "serverTime 带 +08:00 偏移（北京时间口径）",
    bool(re.search(r"\+08:00$", str(info.get("serverTime", "")))),
    f"实得 {info.get('serverTime')}",
)
check(
    "内嵌时间同步摘要",
    isinstance(info.get("timeSync"), dict)
    and {
        "autoSync", "resyncIntervalSeconds", "synchronized", "lastSyncAt", "source",
        "offsetMs", "offsetText", "roundTripMs", "corrected", "failureCount",
        "attemptCount", "servers",
    }
    <= set(info["timeSync"].keys()),
    f"实得 {sorted(info.get('timeSync', {}).keys())}",
)
check(
    "内嵌定时摘要（只给计数）",
    isinstance(info.get("timers"), dict)
    and {"enabled", "tickIntervalSeconds", "firedCount", "missedCount", "triggerCount"}
    <= set(info["timers"].keys()),
    f"实得 {sorted(info.get('timers', {}).keys())}",
)

# 用服务端自己的日期构造月度计划，避免脚本与服务器跨日 / 跨时区不一致。
m = re.match(r"(\d{4})-(\d{2})-(\d{2})T", str(info.get("serverTime", "")))
check("能从 serverTime 解析出服务端日期", bool(m), f"实得 {info.get('serverTime')}")
TODAY_MONTH, TODAY_DAY = (int(m.group(2)), int(m.group(3))) if m else (1, 1)

status, ts = call("POST", "/api/system/timesync")
check("POST /api/system/timesync → 200（成败都在 body 的 ok 里）", status == 200, f"实得 {status}")
check(
    "校准结果字段齐全",
    isinstance(ts, dict)
    and {"ok", "offsetMs", "offsetText", "offsetJumpMs", "attempts", "serverTime"} <= set(ts.keys()),
    f"实得 {sorted(ts.keys()) if isinstance(ts, dict) else ts}",
)
check(
    "带回逐服务器尝试记录（排障用，至少试过一个）",
    isinstance(ts.get("attempts"), list) and len(ts["attempts"]) >= 1,
    f"ok={ts.get('ok')} attempts={ts.get('attempts')}",
)

status, addr = call("GET", "/api/system/address")
check("GET /api/system/address → 200", status == 200, f"实得 {status}")
check(
    "地址信息字段齐全",
    isinstance(addr, dict)
    and {"urls", "port", "overrideFile", "persisted"} <= set(addr.keys()),
    f"实得 {sorted(addr.keys()) if isinstance(addr, dict) else addr}",
)
check(
    "port 与实际监听地址自洽",
    isinstance(addr.get("urls"), list)
    and any(str(u).endswith(f":{addr.get('port')}") for u in addr["urls"]),
    f"urls={addr.get('urls')} port={addr.get('port')}",
)

# ═══════════════════════════════════════════════════════════════
# G1 · 图片上传 / 删除
# ═══════════════════════════════════════════════════════════════

print("\n[G1] 图片上传与删除")

st, content = upload("smoke.png", PNG_BYTES)
up = as_json(content)
check("上传合法 PNG → 200", st == 200, f"实得 {st} {up}")
FILE_NAME = up.get("fileName") if isinstance(up, dict) else None
check(
    "文件名由服务端生成（img{32hex}.png），不采信客户端名字",
    isinstance(FILE_NAME, str) and re.fullmatch(r"img[0-9a-f]{32}\.png", FILE_NAME) is not None,
    f"实得 {FILE_NAME}",
)
check("回传字节数等于上传内容", up.get("bytes") == len(PNG_BYTES), f"实得 {up.get('bytes')}")
check(
    "回传服务端绝对路径",
    isinstance(up.get("path"), str) and FILE_NAME and up["path"].replace("\\", "/").endswith(FILE_NAME),
    f"实得 {up.get('path')}",
)

st, content = upload("evil.svg", b"<svg xmlns='http://www.w3.org/2000/svg'/>", "image/svg+xml")
check("白名单外的 .svg → 400", st == 400, f"实得 {st} {as_json(content)}")

st, content = upload("fake.png", b"this is definitely not an image", "image/png")
check("扩展名是 .png 但魔数不符 → 400（只信扩展名等于允许任意字节进图片目录）", st == 400, f"实得 {st} {as_json(content)}")

st, content = upload("toobig.png", b"\x89PNG\r\n\x1a\n" + b"\x00" * (4 * 1024 * 1024 + 512 * 1024))
check("4.5 MB（超过 4 MB 上限）→ 413", st == 413, f"实得 {st} {as_json(content)}")

st, content = upload("huge.png", b"\x89PNG\r\n\x1a\n" + b"\x00" * (6 * 1024 * 1024))
check("6 MB（超过表单读取上限）→ 413（不是 500）", st == 413, f"实得 {st} {as_json(content)}")

st, content = raw_request("GET", f"/api/config/images/{FILE_NAME}")
check("取回上传的图片 → 200 且字节一致", st == 200 and content == PNG_BYTES, f"实得 {st}，{len(content)} 字节")

# 让配置引用它，验证「被引用则拒绝删除」
st, _ = call(
    "PUT",
    "/api/config",
    {
        "settings": {"appName": "图片引用冒烟", "defaultButtonImage": up["path"], "buttons": [], "subButtonSets": []},
        "expectedVersion": current_version(),
        "updatedBy": "smoke",
    },
)
check("配置引用该图片（PUT /api/config）→ 200", st == 200, f"实得 {st}")

st, refused = call("DELETE", f"/api/config/images/{FILE_NAME}")
check("删除仍被引用的图片 → 409", st == 409, f"实得 {st}")
check(
    "409 列出引用方（供用户决定，而不是静默让按钮退化）",
    isinstance(refused, dict) and "默认按钮图片" in (refused.get("referencedBy") or []),
    f"实得 {refused.get('referencedBy') if isinstance(refused, dict) else refused}",
)

st, _ = call("DELETE", f"/api/config/images/{FILE_NAME}?force=true")
check("?force=true 强删 → 204", st == 204, f"实得 {st}")

# 引用检查先于存在性检查 —— 此时文件已没了，但配置还引用着，所以仍是 409 而不是 404。
st, again = call("DELETE", f"/api/config/images/{FILE_NAME}")
check(
    "文件已删但配置仍引用 → 仍 409（引用检查先于存在性检查）",
    st == 409,
    f"实得 {st} {again.get('message') if isinstance(again, dict) else again}",
)

NEVER_EXISTED = "img" + "0" * 32 + ".png"
st, _ = call("DELETE", f"/api/config/images/{NEVER_EXISTED}")
check("删除一个合法命名但从未存在的图片 → 404", st == 404, f"实得 {st}")

st, _ = raw_request("DELETE", "/api/config/images/..%5C..%5Cappsettings.json")
check("非法文件名（含路径分隔）→ 404", st == 404, f"实得 {st}")

# ═══════════════════════════════════════════════════════════════
# G6 · 指令库 CRUD
# ═══════════════════════════════════════════════════════════════

print("\n[G6] 指令库")

st, lst = call("GET", "/api/commands")
check("GET /api/commands → 200", st == 200, f"实得 {st}")
check("空库 count = 0", lst.get("count") == 0, f"实得 {lst.get('count')}")
CMD_NAME = "烟雾指令"
CMD_DATA = "CMD-PAYLOAD"

st, created = call(
    "POST",
    "/api/commands",
    {"command": {"id": "", "name": CMD_NAME, "protocol": "UDP", "dataType": "Hex", "data": "4142"}},
)
check("新增指令 → 200 / Ok", st == 200 and created.get("ok") is True and created.get("status") == "Ok", f"实得 {st} {created.get('status')}")
CMD_ID = (created.get("command") or {}).get("id")
check("回传新指令 Id", bool(CMD_ID), f"实得 {CMD_ID}")
check("回传全量列表（失败时前端可直接刷新，不必再 GET）", len(created.get("commands") or []) == 1, f"实得 {len(created.get('commands') or [])}")

st, dup = call(
    "POST",
    "/api/commands",
    {"command": {"id": "", "name": "烟雾指令", "protocol": "TCP", "dataType": "Hex", "data": "AA"}},
)
check(
    "重名 → 400 / DuplicateName（大小写与全半角都归一后比较）",
    st == 400 and dup.get("status") == "DuplicateName",
    f"实得 {st} {dup.get('status')}",
)
check("重名回传冲突指令 Id", dup.get("conflictId") == CMD_ID, f"实得 {dup.get('conflictId')}")

st, bad = call("POST", "/api/commands", {"command": {"id": "", "name": "带 WoL 的指令", "protocol": "WoL", "dataType": "String", "data": "AA:BB:CC:DD:EE:FF"}})
check("协议 WoL → 400 / Invalid（指令库没有 MAC 语义）", st == 400 and bad.get("status") == "Invalid", f"实得 {st} {bad.get('status')} {bad.get('message')}")

st, bad = call("POST", "/api/commands", {"command": {"id": "", "name": "套娃指令", "protocol": "UDP", "dataType": "指令库", "data": "别的指令"}})
check("数据类型「指令库」→ 400 / Invalid（否则解析无限套娃）", st == 400 and bad.get("status") == "Invalid", f"实得 {st} {bad.get('status')} {bad.get('message')}")

st, bad = call("POST", "/api/commands", {"command": {"id": "", "name": "   ", "protocol": "UDP", "dataType": "String", "data": "x"}})
check("空名称 → 400 / Invalid", st == 400 and bad.get("status") == "Invalid", f"实得 {st} {bad.get('status')}")

st, updated = call(
    "PUT",
    f"/api/commands/{urllib.parse.quote(CMD_ID)}",
    {"command": {"id": "故意写错的-Id", "name": CMD_NAME, "protocol": "UDP", "dataType": "UTF-8", "data": CMD_DATA}},
)
check("改指令 → 200 / Ok", st == 200 and updated.get("ok") is True, f"实得 {st} {updated.get('status')}")
check(
    "Id 以路由为准（body 里的 Id 写错也不会「改一条留一条」）",
    len(updated.get("commands") or []) == 1 and (updated.get("command") or {}).get("id") == CMD_ID,
    f"实得 {len(updated.get('commands') or [])} 条，Id={((updated.get('command') or {}).get('id'))}",
)
check(
    "改动已落库（Hex「4142」→ String）—— 提交的 UTF-8 已并入 String（2026-10-08 别名合并）",
    (updated.get("command") or {}).get("dataType") == "String"
    and (updated.get("command") or {}).get("data") == CMD_DATA,
    f"实得 {((updated.get('command') or {}).get('dataType'))} / {((updated.get('command') or {}).get('data'))}",
)

st, missing = call(
    "PUT",
    "/api/commands/" + urllib.parse.quote("不存在的-id"),
    {"command": {"id": "", "name": "x", "protocol": "UDP", "dataType": "String", "data": "x"}},
)
check("改不存在的指令 → 404 / NotFound", st == 404 and missing.get("status") == "NotFound", f"实得 {st} {missing.get('status')}")

# ═══════════════════════════════════════════════════════════════
# G6 · 定时：先落一份带定时与月度计划的配置
# ═══════════════════════════════════════════════════════════════

print("\n[G6] 定时调度与手动触发")

TIMER_BUTTON_ID = nid()
PLAN_ID = nid()
CMD_BUTTON_ID = nid()
MULTI_BUTTON_ID = nid()

# ── 「只发一条」用的独立 UDP 收口 ──
# 不复用 SINK_PORT：它在下面才被绑定，在绑之前往那儿发包，部分系统会回 ICMP 端口不可达，
# 那些残留会污染后面「定时真的出网了吗」的断言。绑一个临时端口最干净。
own_sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
own_sink.bind(("127.0.0.1", 0))
OWN_PORT = own_sink.getsockname()[1]

settings = {
    "appName": "定时冒烟",
    "buttons": [
        {
            "id": TIMER_BUTTON_ID,
            "order": 0,
            "label": "定时按钮",
            "actions": [new_action("定时出网", data="TIMER-OK")],
            "timerEnabled": True,
            "timerMode": "Daily",
            # 故意写成 HH:mm —— 服务端必须归一化成 HH:mm:ss，否则定时器按精确字符串匹配永远不触发
            "timerTimes": ["08:00"],
            "timerDays": [1, 2, 3, 4, 5],
            "monthlyPlans": [
                {
                    "id": PLAN_ID,
                    "name": "月度计划",
                    "enabled": True,
                    "times": ["09:00"],
                    "monthDays": [{"month": TODAY_MONTH, "days": [TODAY_DAY]}],
                }
            ],
        },
        {
            "id": CMD_BUTTON_ID,
            "order": 1,
            "label": "指令按钮",
            # dataType = 指令库 时，dataContent 是**指令名**（不是内容）
            # 协议用 UDP：发 TCP 到一个没人监听的端口会被拒（0 字节 + 失败），
            # 那是"目标不可达"而不是"指令解析失败"，混在一起就看不出本项到底在测什么。
            "actions": [new_action("发指令", data_type="指令库", data=CMD_NAME)],
        },
        {
            # 两条动作都指向 OWN_PORT、载荷不同 —— 这样"只发一条"可以用**收到的字节**证伪：
            # 若第 1 条也被执行，收口里会先出现 ONE。
            "id": MULTI_BUTTON_ID,
            "order": 2,
            "label": "双动作按钮",
            "actions": [
                new_action("第一包", data="ONE", port=OWN_PORT),
                new_action("第二包", data="TWO", port=OWN_PORT),
            ],
        },
    ],
    "subButtonSets": [],
}

st, saved = call("PUT", "/api/config", {"settings": settings, "expectedVersion": current_version(), "updatedBy": "smoke"})
check("写入含定时 / 月度计划 / 指令引用的配置 → 200", st == 200, f"实得 {st} {saved if st != 200 else ''}")

b0 = ((saved.get("settings") or {}).get("buttons") or [{}])[0] if st == 200 else {}
check(
    "timerTimes 被归一化为 HH:mm:ss（否则静态字符串匹配永远不触发）",
    b0.get("timerTimes") == ["08:00:00"],
    f"实得 {b0.get('timerTimes')}",
)
check("月度计划原样保留", len(b0.get("monthlyPlans") or []) == 1, f"实得 {len(b0.get('monthlyPlans') or [])}")

multi_button = next(
    (b for b in ((saved.get("settings") or {}).get("buttons") or []) if b.get("id") == MULTI_BUTTON_ID),
    {},
)

st, tl = call("GET", "/api/timers")
check("GET /api/timers → 200", st == 200, f"实得 {st}")
triggers = tl.get("triggers") or []
check("触发点共 2 个（每日 1 + 月度 1）", len(triggers) == 2, f"实得 {len(triggers)}: {[(t.get('kind'), t.get('time')) for t in triggers]}")

daily = next((t for t in triggers if t.get("kind") == "Daily"), None)
monthly = next((t for t in triggers if t.get("kind") == "MonthlyPlan"), None)
check("存在 Daily 触发点且时间为 08:00:00", bool(daily) and daily.get("time") == "08:00:00", f"实得 {daily and daily.get('time')}")
check("存在 MonthlyPlan 触发点", bool(monthly), f"实得 {[t.get('kind') for t in triggers]}")
check(
    "月度计划 refId 形如 <buttonId>:<planId>（含冒号，前端须 URL 编码）",
    bool(monthly) and monthly.get("refId") == f"{TIMER_BUTTON_ID}:{PLAN_ID}",
    f"实得 {monthly and monthly.get('refId')}",
)
check("月度计划今天适用（按服务端日期构造）", bool(monthly) and monthly.get("appliesToday") is True, f"实得 {monthly and monthly.get('appliesToday')}")
check("未触发时 firedToday = false", all(t.get("firedToday") is False for t in triggers), f"实得 {[t.get('firedToday') for t in triggers]}")

# ── 动作按名称解析指令库 ──
st, exec_cmd = call("POST", "/api/actions/execute", {"buttonId": CMD_BUTTON_ID})
check("执行引用指令的动作 → 200 / ok", st == 200 and exec_cmd.get("ok") is True, f"实得 {st} {exec_cmd.get('summary')}")
step = (exec_cmd.get("steps") or [{}])[0]
check(
    "动作按名称解析到指令库（resolvedFromCommand）",
    step.get("resolvedFromCommand") == CMD_NAME,
    f"实得 {step.get('resolvedFromCommand')}",
)
check(
    "发出的字节来自指令内容",
    step.get("bytesSent") == len(CMD_DATA),
    f"实得 {step.get('bytesSent')}，期望 {len(CMD_DATA)}",
)

# ═══════════════════════════════════════════════════════════════
# G9 · 单独执行一条动作（编辑页每条动作右侧「执行」按钮的服务端语义）
# ═══════════════════════════════════════════════════════════════

print("\n[G9] 单独执行一条动作（actionId）")

multi_actions = multi_button.get("actions") or []
check("双动作按钮落库后有 2 条动作", len(multi_actions) == 2, f"实得 {len(multi_actions)}")

SECOND_ACTION_ID = (multi_actions[1] or {}).get("id") if len(multi_actions) > 1 else None

st, one = call(
    "POST",
    "/api/actions/execute",
    {"buttonId": MULTI_BUTTON_ID, "actionId": SECOND_ACTION_ID},
)
check("按 actionId 单独执行 → 200 / ok", st == 200 and one.get("ok") is True, f"实得 {st} {one.get('summary')}")

one_steps = one.get("steps") or []
check(
    "只跑了指定那一条（steps 恰好 1 条）",
    len(one_steps) == 1,
    f"实得 {len(one_steps)} 条：{[s.get('actionName') for s in one_steps]}",
)
check("发的是第 2 条动作", bool(one_steps) and one_steps[0].get("actionName") == "第二包", f"实得 {one_steps and one_steps[0].get('actionName')}")
check(
    "展示名带动作名（收发日志里单条与整组只能靠名字区分）",
    one.get("buttonName") == "双动作按钮 · 第二包",
    f"实得 {one.get('buttonName')}",
)

own_sink.settimeout(4)
try:
    payload, _ = own_sink.recvfrom(65535)
    got_one = bytes(payload)
except socket.timeout:
    got_one = None
check("真的发出去了（收到第 2 条的字节）", got_one == b"TWO", f"实得 {got_one!r}")

# 再等一会儿：若第 1 条也被执行，这里会收到 ONE —— 这是"没多发"的直接证据。
own_sink.settimeout(1.0)
try:
    extra, _ = own_sink.recvfrom(65535)
except socket.timeout:
    extra = None
check("没有多余的第 2 个包（第 1 条动作确实没被执行）", extra is None, f"实得多余包 {extra!r}")

st, _orphan = call("POST", "/api/actions/execute", {"actionId": SECOND_ACTION_ID})
check("孤立 actionId（没带宿主按钮）→ 400", st == 400, f"实得 {st}")

st, _bad = call("POST", "/api/actions/execute", {"buttonId": MULTI_BUTTON_ID, "actionId": "不存在的动作"})
check("按钮在、动作不在 → 404（不能退化成「发整组」）", st == 404, f"实得 {st}")

own_sink.close()

# ── 手动触发 + 真 UDP 收包 ──
sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sink.bind(("127.0.0.1", SINK_PORT))

st, fired = call("POST", f"/api/timers/{urllib.parse.quote(TIMER_BUTTON_ID)}/fire")
check("手动触发 → 200 / Fired", st == 200 and fired.get("status") == "Fired", f"实得 {st} {fired.get('status')} {fired.get('message')}")
check("触发结果带回执行明细", isinstance(fired.get("execution"), dict), f"实得 {type(fired.get('execution')).__name__}")

sink.settimeout(6)
try:
    data, _ = sink.recvfrom(65535)
    got = bytes(data)
except socket.timeout:
    got = None
check(
    "触发真的把字节发到了目标端口（真 UDP 收包，不只是看返回值）",
    got == b"TIMER-OK",
    f"实得 {got!r}",
)

st, tl2 = call("GET", "/api/timers")
after = next((t for t in (tl2.get("triggers") or []) if t.get("refId") == TIMER_BUTTON_ID and t.get("kind") == "Daily"), None)
check(
    "手动触发写在「我点的那一刻」，不占用触发点本身的时刻 —— 所以 firedToday 仍为 false",
    bool(after) and after.get("firedToday") is False and after.get("firedAt") is None,
    f"实得 firedToday={after and after.get('firedToday')} firedAt={after and after.get('firedAt')}",
)

# ── 幂等闸门：同秒连点只有第一次生效 ──
# ⚠️ 必须确认「同一次配对里第 1 次是 200、第 2 次是 409」。
#    只断言"出现过 409"会误判：上一对里的第 2 次与下一对的第 1 次常落在同一秒，
#    于是第 1 次也返回 409（连续两组 409/409），看起来像通过、其实没验证到闸门的两面。
same_second = False
detail = ""
for attempt in range(8):
    st1, r1 = call("POST", f"/api/timers/{urllib.parse.quote(TIMER_BUTTON_ID)}/fire")
    st2, r2 = call("POST", f"/api/timers/{urllib.parse.quote(TIMER_BUTTON_ID)}/fire")
    if st1 == 200 and st2 == 409:
        same_second = True
        detail = f"第 1 次 {st1}/{r1.get('status')}，第 2 次 {st2}/{r2.get('status')}"
        check("409 时 execution 为 null（被闸门挡下，没执行）", r2.get("execution") is None, f"实得 {r2.get('execution')}")
        break
    detail = f"第 {attempt + 1} 对：{st1}/{r1.get('status')} + {st2}/{r2.get('status')}"
    time.sleep(0.25)  # 让开，换一秒再来
check("同一秒内连点：第 1 次 200、第 2 次被幂等闸门挡下 → 409 / AlreadyFired", same_second, detail)

st, nf = call("POST", "/api/timers/" + urllib.parse.quote("不存在的触发点") + "/fire")
check("触发不存在的触发点 → 404 / NotFound", st == 404 and nf.get("status") == "NotFound", f"实得 {st} {nf.get('status')}")

sink.close()

# ═══════════════════════════════════════════════════════════════
# 收尾：删掉自建指令 + 恢复出厂
# ═══════════════════════════════════════════════════════════════

print("\n[清理]")

st, deleted = call("DELETE", f"/api/commands/{urllib.parse.quote(CMD_ID)}")
check("删除指令 → 200 / Ok", st == 200 and deleted.get("ok") is True, f"实得 {st} {deleted.get('status')}")
check("删除后列表为空", len(deleted.get("commands") or []) == 0, f"实得 {len(deleted.get('commands') or [])}")

st, gone = call("DELETE", f"/api/commands/{urllib.parse.quote(CMD_ID)}")
check("再删同一指令 → 404 / NotFound", st == 404 and gone.get("status") == "NotFound", f"实得 {st} {gone.get('status')}")

st, after_reset = call("POST", "/api/config/reset", {"expectedVersion": current_version(), "updatedBy": "smoke"})
check("恢复出厂 → 200 且 isEmpty", st == 200 and after_reset.get("isEmpty") is True, f"实得 {st}")

print()
print(f"════════ 结果：{len(PASS)} PASS / {len(FAIL)} FAIL ════════")
if FAIL:
    for name in FAIL:
        print(f"  [X] {name}")
    sys.exit(1)
sys.exit(0)
