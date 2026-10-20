#!/usr/bin/env python3
"""
中控服务版 · Web 控制台端到端冒烟（M2-F + M2-G）

不依赖浏览器，直接用 HTTP 走一遍界面背后真正会发生的调用序列：

  1. 未登录访问受保护接口 → 401
  2. 登录 → 拿到 Cookie
  3. GET /api/config → 空配置
  4. POST /api/config/import（旧版 .controlbak 明文 JSON，含 base64 图片）
  5. GET /api/config/images/<文件名> → 图片字节与导入时一致
  6. GET / → 首页引用的是**本次构建**的前端 bundle
  7. POST /api/actions/execute {buttonId} → 按已保存配置执行
  8. GET /api/traffic/recent → 收发日志里出现这条发送记录
  9. GET /api/config/export?format=base64 → 再导入一次（往返）
 10. POST /api/config/reset → 清空

用法：
    python deploy/smoke/web-console-smoke.py <baseUrl> <pairingPort>

约定：所有 curl/urllib 调用都要绕过本机 HTTP 代理（Git Bash 环境常配了代理，
访问 127.0.0.1 会返回 502）。
"""

import base64
import http.cookiejar
import json
import sys
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18099"
PAIRING_PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 18098

# 一张 1x1 PNG（真实可渲染）+ 一段任意字节，用于验证图片端点原样转发。
PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="
)

_jar = http.cookiejar.CookieJar()
_opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({}),           # 绕过代理
    urllib.request.HTTPCookieProcessor(_jar),  # 保持登录 Cookie
)

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {name}" + (f" —— {detail}" if detail else ""))


def request(method, path, body=None, raw=False):
    """返回 (status, payload)。payload 是解析后的 JSON，或原始 bytes（raw=True）。"""
    url = BASE + path
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with _opener.open(req, timeout=30) as resp:
            content = resp.read()
            status = resp.status
    except urllib.error.HTTPError as e:
        content = e.read()
        status = e.code

    if raw:
        return status, content
    try:
        return status, json.loads(content.decode("utf-8"))
    except Exception:
        return status, content.decode("utf-8", errors="replace")


def desktop_controlbak(button_count=2, image=True):
    """构造一份与旧版「设置 → 导出配置」同构的 .controlbak 内容（明文 JSON）。"""
    buttons = []
    for i in range(button_count):
        buttons.append(
            {
                "id": f"b{i}",
                "order": i,
                "label": f"冒烟按钮{i + 1}",
                "pairingCode": f"SMOKE-{i + 1}",
                "pairingCodeType": "String",
                "buttonColor": "#512DA8",
                "textColor": "#FFFFFF",
                "nameFontSize": 13,
                "buttonImage": "img0.png" if image else "",
                "width": 140,
                "height": 80,
                "actions": [
                    {
                        "id": f"a{i}",
                        "name": f"发 UDP {i + 1}",
                        "protocol": "UDP",
                        "targetIp": "127.0.0.1",
                        "targetPort": PAIRING_PORT,
                        "dataType": "UTF-8",
                        "dataContent": f"SMOKE-PAYLOAD-{i + 1}",
                        "delayMs": 0,
                    }
                ],
            }
        )

    settings = {
        "appName": "冒烟中控",
        "listenPort": 9999,
        # 单机版专有字段：服务版应"忽略但明确告知"，不是无声吞掉。
        "windowWidth": 1280,
        "lockWindow": True,
        "userPasswordHash": "DEADBEEF",
        "defaultButtonImage": "img0.png" if image else "",
        "buttons": buttons,
        "subButtonSets": [
            {
                "id": "s1",
                "order": button_count,
                "label": "冒烟集合",
                "targetIp": "127.0.0.1",
                "targetPort": PAIRING_PORT,
                "subButtons": [
                    {
                        "id": "sub1",
                        "label": "子按钮1",
                        "buttonColor": "#00897B",
                        "nameFontSize": 12,
                        "actions": [
                            {
                                "id": "sa1",
                                "name": "子动作",
                                "protocol": "UDP",
                                "dataType": "UTF-8",
                                "dataContent": "SMOKE-SUB",
                                "delayMs": 0,
                            }
                        ],
                    }
                ],
            }
        ],
    }

    backup = {"version": "1.0", "settings": settings, "images": {}}
    if image:
        backup["images"]["img0.png"] = base64.b64encode(PNG_BYTES).decode("ascii")

    return json.dumps(backup, ensure_ascii=False, indent=2)


print("════════ Web 控制台冒烟 ════════")

# ── 1. 未登录 ──
status, _ = request("GET", "/api/config")
check("未登录访问 /api/config 返回 401", status == 401, f"实得 {status}")

# ── 2. 登录 ──
status, payload = request("POST", "/api/auth/login", {"userName": "admin", "password": "admin123"})
check("默认管理员登录成功", status == 200, f"实得 {status} {payload}")

# ── 3. 空配置 ──
status, config = request("GET", "/api/config")
check("全新库配置为空", status == 200 and config.get("isEmpty") is True, f"实得 {status} isEmpty={config.get('isEmpty')}")
check("空配置版本为 0", config.get("version") == 0, f"实得 {config.get('version')}")

# ── 4. 导入旧版备份 ──
bak = desktop_controlbak()
status, result = request("POST", "/api/config/import", {"content": bak, "expectedVersion": 0})
check("导入旧版 .controlbak 成功", status == 200 and result.get("ok") is True, f"实得 {status} {result.get('message') if isinstance(result, dict) else result}")
counts = result.get("counts", {}) if isinstance(result, dict) else {}
check("导入后按钮数为 2", counts.get("buttons") == 2, f"实得 {counts}")
check("导入后子按钮数为 1", counts.get("subButtons") == 1, f"实得 {counts}")
check("还原了 1 张内嵌图片", result.get("imageCount") == 1, f"实得 {result.get('imageCount')}")
ignored = set(result.get("ignoredFields", [])) if isinstance(result, dict) else set()
check(
    "列出被忽略的单机版专有字段",
    {"windowWidth", "lockWindow", "userPasswordHash"} <= ignored,
    f"实得 {sorted(ignored)}",
)

# ── 5. 图片端点 ──
status, image_bytes = request("GET", "/api/config/images/img0.png", raw=True)
check("导入的图片可通过端点取回", status == 200 and image_bytes == PNG_BYTES, f"实得 {status}，{len(image_bytes)} 字节")

status, _ = request("GET", "/api/config/images/appsettings.json", raw=True)
check("图片端点拒绝非图片目录的文件", status == 404, f"实得 {status}")

status, _ = request("GET", "/api/config/images/..%5C..%5Cappsettings.json", raw=True)
check("图片端点挡住目录穿越", status == 404, f"实得 {status}")

# ── 6. 首页 bundle ──
status, html = request("GET", "/", raw=True)
html_text = html.decode("utf-8", errors="replace") if isinstance(html, bytes) else str(html)
check("首页返回 200", status == 200, f"实得 {status}")
check("首页不再是 M1 骨架（引用了带哈希的 bundle）", "/assets/index-" in html_text, html_text[:120])

# ── 7. 按已保存按钮执行 ──
status, config = request("GET", "/api/config")
buttons = config.get("settings", {}).get("buttons", [])
check("配置里能读到刚导入的按钮", len(buttons) == 2 and buttons[0]["label"] == "冒烟按钮1", f"实得 {[b.get('label') for b in buttons]}")

status, exec_result = request("POST", "/api/actions/execute", {"buttonId": buttons[0]["id"], "label": buttons[0]["label"]})
check("按 buttonId 执行成功", status == 200 and exec_result.get("ok") is True, f"实得 {status} {exec_result.get('summary') if isinstance(exec_result, dict) else exec_result}")
check("执行了 1 条动作", len(exec_result.get("steps", [])) == 1, f"实得 {len(exec_result.get('steps', []))} 条")
if exec_result.get("steps"):
    step = exec_result["steps"][0]
    check("动作实际发到对接码端口", step.get("targetPort") == PAIRING_PORT, f"实得 {step.get('targetPort')}")
    check("动作字节数等于发出的 UTF-8 长度", step.get("bytesSent") == len("SMOKE-PAYLOAD-1"), f"实得 {step.get('bytesSent')}")

status, exec_sub = request("POST", "/api/actions/execute", {"subButtonId": "sub1"})
check("按 subButtonId 执行成功", status == 200 and exec_sub.get("ok") is True, f"实得 {status}")
if exec_sub.get("steps"):
    step = exec_sub["steps"][0]
    check(
        "子按钮 IP/端口由所属集合提供（服务端强制）",
        step.get("targetIp") == "127.0.0.1" and step.get("targetPort") == PAIRING_PORT,
        f"实得 {step.get('targetIp')}:{step.get('targetPort')}",
    )

# ── 8. 收发日志 ──
status, traffic = request("GET", "/api/traffic/recent")
check("收发日志有记录", status == 200 and len(traffic) >= 2, f"实得 {status}，{len(traffic) if isinstance(traffic, list) else traffic} 条")

sends = [t for t in traffic if t.get("direction") == "发送"] if isinstance(traffic, list) else []
check("存在「发送」方向的记录", len(sends) >= 2, f"实得 {len(sends)} 条")
check(
    "日志记录了按钮名与动作名",
    any(t.get("buttonName") == "冒烟按钮1" and t.get("actionName") == "发 UDP 1" for t in sends),
    f"实得 {[(t.get('buttonName'), t.get('actionName')) for t in sends][:3]}",
)
check(
    "日志记录了发出的数据内容",
    any(t.get("data") == "SMOKE-PAYLOAD-1" for t in sends),
    f"实得 {[t.get('data') for t in sends][:3]}",
)

status, count = request("GET", "/api/traffic/count")
check("日志计数接口可用", status == 200 and count.get("count") >= 2, f"实得 {status} {count}")

# ── 9. 导出 → 再导入（往返）──
status, export = request("GET", "/api/config/export?format=base64")
check("导出 base64 成功", status == 200 and export.get("encoding") == "base64", f"实得 {status}")
check("导出文件名是 .controlbak", str(export.get("fileName", "")).endswith(".controlbak"), f"实得 {export.get('fileName')}")
check("导出内嵌了 1 张图片", export.get("imageCount") == 1, f"实得 {export.get('imageCount')}")

status, reimport = request(
    "POST",
    "/api/config/import",
    {"content": export.get("content"), "expectedVersion": export.get("version")},
)
check("导出的内容可原样再导入", status == 200 and reimport.get("ok") is True, f"实得 {status}")

# ── 10. 错误路径 ──
status, bad = request("POST", "/api/config/import", {"content": "这不是配置", "expectedVersion": None})
check("导入非法内容被拒绝（400）", status == 400 and bad.get("ok") is False, f"实得 {status}")

status, bad2 = request("POST", "/api/config/import", {"content": '{"appName":"空","buttons":[],"subButtonSets":[]}', "expectedVersion": None})
check("导入空配置不覆盖现有配置（400）", status == 400, f"实得 {status}")
check("拒绝原因指向「恢复出厂」", "恢复出厂" in str(bad2.get("message", "")), f"实得 {bad2.get('message')}")

status, stale = request(
    "POST",
    "/api/config/import",
    {"content": desktop_controlbak(button_count=1), "expectedVersion": 0},
)
check("版本过旧的写入返回 409", status == 409, f"实得 {status}")
if isinstance(stale, dict):
    check("409 带回服务端当前配置", stale.get("current", {}).get("counts", {}).get("buttons") == 2, f"实得 {stale.get('current', {}).get('counts')}")

# ── 11. 恢复出厂 ──
status, config = request("GET", "/api/config")
status, after_reset = request("POST", "/api/config/reset", {"expectedVersion": config.get("version")})
check("恢复出厂成功", status == 200 and after_reset.get("isEmpty") is True, f"实得 {status}")

print()
print(f"════════ 结果：{len(PASS)} PASS / {len(FAIL)} FAIL ════════")
if FAIL:
    for name in FAIL:
        print(f"  [X] {name}")
    sys.exit(1)
sys.exit(0)
