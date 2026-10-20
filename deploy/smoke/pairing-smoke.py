"""M2-C 真服务冒烟：直接往 SQLite 里落一份配置，再发真实 UDP 对接码，观测动作是否真的出网。

用法：python smoke-m2c.py <db文件> <监听端口> <按钮目标端口> <子按钮目标端口>
"""
import json
import socket
import sqlite3
import sys
import time

DB, LISTEN_PORT, DIRECT_SINK, SUB_SINK = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])


def action(name, port, content, data_type="UTF-8", pairing_code="", pairing_code_type="String"):
    return {
        "id": f"act-{name}",
        "name": name,
        "targetIp": "127.0.0.1",
        "targetPort": port,
        "protocol": "UDP",
        "dataType": data_type,
        "dataContent": content,
        "delayMs": 0,
        "pairingCode": pairing_code,
        "pairingCodeType": pairing_code_type,
    }


BUTTON_DEFAULTS = {
    "buttonColor": "#512DA8",
    "textColor": "#FFFFFF",
    "nameFontSize": 13,
    "buttonImage": "",
    "width": 140,
    "height": 80,
    "layoutX": 0,
    "layoutY": 0,
    "timerEnabled": False,
    "timerMode": "Daily",
    "timerTime": "08:00",
    "timerTimes": ["08:00"],
    "timerDays": [1, 2, 3, 4, 5],
    "monthlyPlans": [],
}

# ═══════════ 1. 落一份配置（camelCase，与旧版 control_config.json 同构）═══════════

CONFIG = {
    "appName": "中控",
    "backgroundImagePath": "",
    "listenPort": 9999,
    "defaultButtonImage": "",
    "freeLayoutEnabled": False,
    "buttons": [
        {
            **BUTTON_DEFAULTS,
            "id": "smoke-button-1",
            "order": 0,
            "label": "冒烟按钮",
            "pairingCode": "对接码-开门",          # 按钮级 + UTF-8 中文（M1 的 ASCII 解码下必然失败）
            "pairingCodeType": "UTF-8",
            "actions": [action("发冒烟数据", DIRECT_SINK, "SMOKE-OK")],
        },
        {
            **BUTTON_DEFAULTS,
            "id": "smoke-button-2",
            "order": 1,
            "label": "Hex按钮",
            "pairingCode": "",                    # 无按钮级对接码：只能靠动作级命中
            "pairingCodeType": "String",
            "actions": [
                action("Hex动作", DIRECT_SINK, "41 42", data_type="Hex", pairing_code="4142", pairing_code_type="Hex"),
                action("不该发的动作", DIRECT_SINK, "MISS"),
            ],
        },
        {
            **BUTTON_DEFAULTS,
            "id": "smoke-button-3",
            "order": 2,
            "label": "组合成员",
            "pairingCode": "",
            "pairingCodeType": "String",
            "actions": [action("组合成员数据", DIRECT_SINK, "COMBO-A")],
        },
    ],
    # 🔥 组合按钮（2026-10-15，需求⑤）：收到 "COMBO-开门" 就按顺序执行两个成员，
    #    段间等 150ms。成员**只引用主按钮的 id**（不是动作副本）。
    "comboButtons": [
        {
            "id": "smoke-combo-1",
            "order": 3,
            "label": "冒烟组合",
            "pairingCode": "COMBO-开门",
            "pairingCodeType": "UTF-8",
            "buttonColor": "#512DA8",
            "textColor": "#FFFFFF",
            "nameFontSize": 13,
            "buttonImage": "",
            "width": 140,
            "height": 80,
            "layoutX": 0,
            "layoutY": 0,
            "steps": [
                {"buttonId": "smoke-button-3", "delayMs": 150},
                {"buttonId": "smoke-button-1", "delayMs": 0},
            ],
        }
    ],
    "subButtonSets": [
        {
            "id": "smoke-set-1",
            "order": 4,
            "label": "冒烟集合",
            "buttonColor": "#00695C",
            "textColor": "#FFFFFF",
            "nameFontSize": 13,
            "buttonImage": "",
            "width": 140,
            "height": 80,
            "layoutX": 0,
            "layoutY": 0,
            "targetIp": "127.0.0.1",
            "targetPort": SUB_SINK,               # 集合统一端口
            "subButtons": [
                {
                    "id": "smoke-sub-1",
                    "label": "冒烟子按钮",
                    "buttonColor": "#00897B",
                    "textColor": "#FFFFFF",
                    "nameFontSize": 12,
                    "buttonImage": "",
                    "width": 90,
                    "height": 90,
                    "pairingCode": "SUB-开门",
                    "pairingCodeType": "UTF-8",
                    "actions": [
                        # 动作自身端口故意写 1：若集合 override 没生效，这条会发错地方
                        action("子按钮数据", 1, "SUB-OK"),
                    ],
                }
            ],
        }
    ],
}

conn = sqlite3.connect(DB)
conn.execute(
    "INSERT OR REPLACE INTO config_documents (Key, Value, Version, UpdatedAt, UpdatedBy) VALUES (?, ?, ?, ?, ?)",
    ("settings", json.dumps(CONFIG, ensure_ascii=False), 1, "2026-10-07T14:40:00+08:00", "smoke"),
)
conn.commit()
conn.close()
print("[seed] 配置已写入 config_documents（version=1）")


# ═══════════ 2. 场景 ═══════════

def fmt(data):
    if data is None:
        return "None"
    pretty = " ".join(f"{b:02X}" for b in data)
    text = "".join(chr(b) if 32 <= b < 127 else "?" for b in data)
    return f"{list(data)}  HEX={pretty}  TEXT={text}"


def scenario(name, payload, sink_port, expect_count, expect_first, idle=1.2):
    """发一包对接码，收集 sink 上收到的**所有**数据报（含一段空闲期）。"""
    sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sink.bind(("127.0.0.1", sink_port))

    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sender.sendto(payload, ("127.0.0.1", LISTEN_PORT))

    # 先等第一条（最多 10s），再收集剩余的到连续空闲为止。
    received = []
    sink.settimeout(10)
    try:
        while True:
            data, _ = sink.recvfrom(65535)
            received.append(list(data))
            sink.settimeout(idle)
    except socket.timeout:
        pass

    sink.close()
    sender.close()

    ok = len(received) == expect_count and (expect_count == 0 or received[0] == expect_first)
    print(f"[{'PASS' if ok else 'FAIL'}] {name}")
    print(f"        发送 : {payload!r}  →  UDP 127.0.0.1:{LISTEN_PORT}")
    print(f"        期望 : {expect_count} 条，首条 {expect_first}")
    print(f"        实际 : {len(received)} 条，首条 {fmt(received[0]) if received else None}")
    return ok


results = []

results.append(scenario(
    "① 按钮级命中（UTF-8 中文对接码 → 执行该按钮全部动作，恰好 1 条）",
    "对接码-开门".encode("utf-8"), DIRECT_SINK, 1, list(b"SMOKE-OK")))

results.append(scenario(
    "② 动作级命中（Hex 对接码「4142」+ 发原始字节 0x41 0x42 → 只发命中的那一条）",
    bytes([0x41, 0x42]), DIRECT_SINK, 1, [0x41, 0x42]))

results.append(scenario(
    "③ 子按钮级命中（集合 override 把动作端口 1 覆盖成集合端口）",
    "SUB-开门".encode("utf-8"), SUB_SINK, 1, list(b"SUB-OK")))

def scenario_combo(name, payload, sink_port, delay_ms=150, idle=1.2):
    """
    组合按钮级命中：按成员顺序收包，并**量一下段间延迟真的等了没有**。

    ⚠️ 与其它场景不同，这里必须自己计时：只断言"收到 2 条"是分不出
    "按顺序带延迟执行"与"两条几乎同时发出"的 —— 而后者正是这个功能最容易漏实现的一半。
    """
    sink = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sink.bind(("127.0.0.1", sink_port))

    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    started = time.monotonic()
    sender.sendto(payload, ("127.0.0.1", LISTEN_PORT))

    received = []
    first_at = None
    second_at = None

    sink.settimeout(10)
    try:
        while True:
            data, _ = sink.recvfrom(65535)

            if not received:
                first_at = time.monotonic()
            elif second_at is None:
                second_at = time.monotonic()

            received.append(list(data))
            sink.settimeout(idle)
    except socket.timeout:
        pass

    sink.close()
    sender.close()

    gap_ms = round((second_at - first_at) * 1000) if (first_at and second_at) else None

    order_ok = received == [list(b"COMBO-A"), list(b"SMOKE-OK")]
    gap_ok = gap_ms is not None and gap_ms >= 100   # 配置 150ms，留 50ms 调度余量

    print(f"[{'PASS' if order_ok and gap_ok else 'FAIL'}] {name}")
    print(f"        发送 : {payload!r}  →  UDP 127.0.0.1:{LISTEN_PORT}")
    print(f"        期望 : 2 条，先 COMBO-A 后 SMOKE-OK，且两条之间 ≥100ms")
    print(f"        实际 : {len(received)} 条，间隔 {gap_ms}ms")

    return order_ok and gap_ok


results.append(scenario_combo(
    "⑤ 组合按钮级命中（按成员顺序执行两个按钮，段间延迟真的生效）",
    "COMBO-开门".encode("utf-8"), DIRECT_SINK))

results.append(scenario(
    "④ 未命中（不应发出任何数据）",
    "NO-SUCH-CODE".encode("utf-8"), DIRECT_SINK, 0, None, idle=2.0))

print()
print(f"结果：{sum(results)}/{len(results)} 通过")
sys.exit(0 if all(results) else 1)
