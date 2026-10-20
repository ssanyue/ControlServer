#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
中控服务版 · RBAC 与账号生命周期冒烟（M3）

⚠️ 本脚本用**默认配置**（`Server:Auth:EnforcePasswordChange` 保持默认关闭）。
   2026-10-09 起产品已取消"首次使用强制改密"：出厂管理员、新建账号、管理员重置密码
   都不再被标记，因此 A 段验证的是"出厂态开箱即用"，而不再是"必须先改密"。
   它会把 admin 的密码从 `admin123` 改成 `Admin@2026!` —— 请只对**临时实例**跑。

覆盖：
  A. 出厂态                 默认管理员登录 → 不带强制改密标记 → 业务接口直接可用
  B. 账号管理端点           /api/users 与 /api/users/roles（仅管理员可读）
  C. 三角色权限矩阵         Viewer 只读 / Operator 可操作但不可管理账号与导入导出 / Admin 全权限
  D. 服务端守卫             不能停用/删除自己、不能动最后一个启用管理员、用户名大小写不敏感唯一
  E. 会话即时生效           停用后**已签发的会话在下一次请求即失效**（不必等重新登录）
  F. 登录锁定               连续失败达上限即锁定；锁定期间继续输错**不延长**锁定期
  G. 口令材料不出接口       /api/users 响应里绝不出现 passwordHash / salt

与另两个冒烟脚本的分工见 run-all.py。本脚本自带**多会话**（每个角色一个独立 Cookie jar），
所以"降级/停用是否当场生效"这类跨会话断言能真跑出来。

用法：
    python deploy/smoke/rbac-smoke.py [baseUrl]

    baseUrl  默认 http://127.0.0.1:18100

约定：所有调用绕过本机 HTTP 代理（Git Bash 常配代理，访问 127.0.0.1 会 502）。
"""

import http.cookiejar
import json
import sys
import urllib.error
import urllib.request

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18100").rstrip("/")

SEED_ADMIN = "admin"
SEED_PASSWORD = "admin123"
ADMIN_PASSWORD = "Admin@2026!"
OP_PASSWORD = "Operator@2026!"
VIEWER_PASSWORD = "Viewer@2026!"

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" —— {detail}" if detail else ""))


class Session:
    """一个独立的浏览器会话（独立 Cookie jar）—— 用来同时扮演多个角色。"""

    def __init__(self, label):
        self.label = label
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),                  # 绕过本机代理
            urllib.request.HTTPCookieProcessor(self.jar),     # 记住会话 Cookie
        )

    def call(self, method, path, body=None):
        """返回 (status, 解析后的 JSON 或纯文本)。"""
        data, headers = None, {}
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=60) as resp:
                status, content = resp.status, resp.read()
        except urllib.error.HTTPError as e:
            status, content = e.code, e.read()

        try:
            return status, json.loads(content.decode("utf-8"))
        except Exception:
            return status, content.decode("utf-8", errors="replace")

    def login(self, user, password):
        return self.call("POST", "/api/auth/login", {"userName": user, "password": password})

    def login_ok(self, user, password):
        """登录并返回 (是否成功, 响应体)。"""
        st, body = self.login(user, password)
        return st == 200, body

    def change_password(self, current, new):
        return self.call("POST", "/api/auth/change-password",
                         {"currentPassword": current, "newPassword": new})


def users_of(body):
    return (body or {}).get("users") or []


def find_user(body, name):
    return next((u for u in users_of(body) if (u.get("userName") or "").lower() == name.lower()), None)


def status_of(body):
    """
    结果状态，容错取。

    授权失败（403）由中间件直接返回**空响应体**，此时 body 是纯文本而不是 dict ——
    直接 `.get()` 会 AttributeError 把整个脚本崩掉，掩盖后面真正要报的断言。
    """
    return body.get("status") if isinstance(body, dict) else f"<{type(body).__name__}>"


# ═══════════════════════════════════════════════════════════════
admin = Session("admin")
anon = Session("anon")

print("════════ RBAC 与账号生命周期冒烟（M3）════════")
print(f"      BASE = {BASE}")
print("      [!] 会把 admin 密码改为 " + ADMIN_PASSWORD + "（须为临时实例）")

# ═══════════════════════════════════════════════════════════════
# A. 出厂态（2026-10-09 起不再强制改密）
# ═══════════════════════════════════════════════════════════════

print("\n[A] 出厂默认管理员与非强制登录")

st, _ = anon.call("GET", "/api/config")
check("未登录访问受保护接口 → 401", st == 401, f"实得 {st}")

ok, body = admin.login_ok(SEED_ADMIN, SEED_PASSWORD)
check("出厂默认管理员 admin/admin123 登录成功", ok, f"实得 {body}")
check(
    "登录响应不带 mustChangePassword（出厂不再强制改密）",
    isinstance(body, dict) and body.get("mustChangePassword") is False,
    f"实得 {body.get('mustChangePassword') if isinstance(body, dict) else body}",
)

st, me = admin.call("GET", "/api/auth/me")
check(
    "/api/auth/me 同样为 false（刷新页面也不会被拦）",
    st == 200 and isinstance(me, dict) and me.get("mustChangePassword") is False,
    f"实得 {st} {me}",
)

# 未改密即可直接使用受保护接口 —— 这正是"取消首次强制改密"的效果。
for path in ("/api/system/info", "/api/config", "/api/users", "/api/timers"):
    st, gate = admin.call("GET", path)
    check(
        f"出厂态直接访问 {path} → 200（不再被闸门拦）",
        st == 200,
        f"实得 {st} {gate if st != 200 else ''}",
    )

# 改密端点仍然可用（任何已登录用户都能改自己的密码）。
st, _ = admin.call("POST", "/api/auth/change-password",
                   {"currentPassword": "这不是我的密码", "newPassword": ADMIN_PASSWORD})
check("改密时当前密码错误 → 400", st == 400, f"实得 {st}")

st, bad = admin.call("POST", "/api/auth/change-password",
                     {"currentPassword": SEED_PASSWORD, "newPassword": "short"})
check("改密时新密码过弱 → 400", st == 400, f"实得 {st} {bad}")

st, _ = admin.change_password(SEED_PASSWORD, ADMIN_PASSWORD)
check("主动改密成功 → 200", st == 200, f"实得 {st}")

st, me2 = admin.call("GET", "/api/auth/me")
check(
    "改密后 mustChangePassword 仍为 false",
    st == 200 and isinstance(me2, dict) and me2.get("mustChangePassword") is False,
    f"实得 {me2}",
)

st, _ = admin.call("GET", "/api/system/info")
check("改密后同一会话访问受保护接口 → 200", st == 200, f"实得 {st}")

st, body = anon.login(SEED_ADMIN, SEED_PASSWORD)
check("旧密码 admin123 已作废 → 401", st == 401, f"实得 {st}")

# ═══════════════════════════════════════════════════════════════
# B. 账号管理端点
# ═══════════════════════════════════════════════════════════════

print("\n[B] 账号列表与角色目录")

st, lst = admin.call("GET", "/api/users")
check("GET /api/users → 200", st == 200, f"实得 {st}")
check("至少含出厂管理员", isinstance(lst, list) and len(lst) >= 1, f"实得 {type(lst).__name__}")
ADMIN_ID = (find_user({"users": lst} if isinstance(lst, list) else {}, SEED_ADMIN) or {}).get("id")
check("能定位到 admin 的 Id", isinstance(ADMIN_ID, int), f"实得 {ADMIN_ID}")

FORBIDDEN_KEYS = {"passwordhash", "hashedpassword", "salt", "pwdhash"}
leaked = []
for u in lst if isinstance(lst, list) else []:
    if isinstance(u, dict):
        for k in u:
            low = k.lower()
            # ⚠️ 不能简单地用 "password" in k：`mustChangePassword` 是**布尔状态标记**，
            #    不是口令材料。判据是"像不像承载口令本体"，而不是"名字里有没有 password"。
            if low in FORBIDDEN_KEYS or (("password" in low or "salt" in low)
                                         and low not in ("mustchangepassword", "changepassword")):
                leaked.append(k)
check("账号列表绝不出现口令材料（passwordHash / salt）", not leaked, f"实得多余字段 {sorted(set(leaked))}")

# 时间口径：全应用统一北京时间 UTC+8（前端 fmtDateTime 直接截字符串，不做时区换算）
check(
    "账号时间戳带 +08:00 偏移（北京时间口径，不是 UTC）",
    isinstance(lst, list)
    and all(str(u.get("createdAt", "")).endswith("+08:00") for u in lst)
    and all(u.get("lastLoginAt") is None or str(u["lastLoginAt"]).endswith("+08:00") for u in lst),
    f"实得 createdAt={[u.get('createdAt') for u in (lst if isinstance(lst, list) else [])]}",
)

check(
    "账号字段齐全",
    isinstance(lst, list) and lst and {
        "id", "userName", "role", "isEnabled", "mustChangePassword", "isLocked",
        "createdAt", "lastLoginAt", "lockedUntil",
    } <= set(lst[0].keys()),
    f"实得 {sorted(lst[0].keys()) if isinstance(lst, list) and lst else lst}",
)

st, roles = admin.call("GET", "/api/users/roles")
check("GET /api/users/roles → 200", st == 200, f"实得 {st}")
check(
    "角色目录为三角色，且带中文显示名（前端不必硬编码）",
    isinstance(roles, list)
    and {r.get("name") for r in roles} == {"Admin", "Operator", "Viewer"}
    and all(r.get("displayName") for r in roles),
    f"实得 {roles}",
)

# ── 新建：三条拒绝 + 一条成功 ──
st, dup_seed = admin.call("POST", "/api/users",
                          {"userName": SEED_ADMIN.upper(), "password": OP_PASSWORD, "role": "Operator"})
check(
    "新建重名（大小写不同）→ 400 / DuplicateName",
    st == 400 and isinstance(dup_seed, dict) and dup_seed.get("status") == "DuplicateName",
    f"实得 {st} {dup_seed.get('status') if isinstance(dup_seed, dict) else dup_seed}",
)

st, weak = admin.call("POST", "/api/users", {"userName": "weakling", "password": "123", "role": "Viewer"})
check(
    "新建弱密码 → 400 / Invalid",
    st == 400 and isinstance(weak, dict) and weak.get("status") == "Invalid",
    f"实得 {st} {weak}",
)

st, badrole = admin.call("POST", "/api/users",
                         {"userName": "badrole", "password": OP_PASSWORD, "role": "Superuser"})
check(
    "新建非法角色 → 400 / Invalid",
    st == 400 and isinstance(badrole, dict) and badrole.get("status") == "Invalid",
    f"实得 {st} {badrole}",
)

st, sameasname = admin.call("POST", "/api/users",
                            {"userName": "alice", "password": "alice", "role": "Viewer"})
check("新建密码等于用户名 → 400 / Invalid", st == 400, f"实得 {st}")

st, created_op = admin.call("POST", "/api/users",
                            {"userName": "operator1", "password": OP_PASSWORD, "role": "Operator"})
check("新建操作员 → 200 / Ok", st == 200 and created_op.get("ok") is True, f"实得 {st} {created_op}")
OP_ID = (created_op.get("user") or {}).get("id")
check("新建账号不再被标记 mustChangePassword（2026-10-09 起）",
      (created_op.get("user") or {}).get("mustChangePassword") is False,
      f"实得 {(created_op.get('user') or {}).get('mustChangePassword')}")
check("新建账号默认启用", (created_op.get("user") or {}).get("isEnabled") is True,
      f"实得 {(created_op.get('user') or {}).get('isEnabled')}")

st, created_vw = admin.call("POST", "/api/users",
                            {"userName": "viewer1", "password": VIEWER_PASSWORD, "role": "Viewer"})
check("新建查看者 → 200 / Ok", st == 200 and created_vw.get("ok") is True, f"实得 {st} {created_vw}")
VW_ID = (created_vw.get("user") or {}).get("id")

st, nf = admin.call("PUT", "/api/users/999999", {"role": "Viewer"})
check("改不存在的账号 → 404 / NotFound", st == 404 and status_of(nf) == "NotFound", f"实得 {st} {nf}")
check("404 也带回全量列表（失败时前端可立即刷新）", isinstance(nf, dict) and isinstance(nf.get("users"), list), f"实得 {type(nf).__name__}")

# ═══════════════════════════════════════════════════════════════
# C. 三角色权限矩阵
# ═══════════════════════════════════════════════════════════════

print("\n[C] 三角色权限矩阵")

op = Session("operator1")
viewer = Session("viewer1")

ok, body = op.login_ok("operator1", OP_PASSWORD)
check("操作员登录成功", ok, f"实得 {body}")
st, _ = op.call("GET", "/api/config")
check("操作员直接访问 /api/config → 200（不再被闸门拦）", st == 200, f"实得 {st}")

ok, _ = viewer.login_ok("viewer1", VIEWER_PASSWORD)
check("查看者登录成功", ok)

# 改密端点仍可用（主动改密）；改后原口令作废，后续断言沿用新口令。
st, _ = op.change_password(OP_PASSWORD, "Operator@2026B!")
check("操作员主动改密 → 200", st == 200, f"实得 {st}")
st, _ = viewer.change_password(VIEWER_PASSWORD, "Viewer@2026B!")
check("查看者主动改密 → 200", st == 200, f"实得 {st}")

st, _ = op.call("GET", "/api/system/info")
check("操作员 GET /api/system/info → 200", st == 200, f"实得 {st}")

# 当前配置版本（写配置需要回传）
st, cfg = op.call("GET", "/api/config")
VER = cfg.get("version") if isinstance(cfg, dict) else None
check("操作员 GET /api/config → 200（读配置允许）", st == 200, f"实得 {st}")

st, saved = op.call("PUT", "/api/config", {
    "settings": {"appName": "RBAC 冒烟", "buttons": [], "subButtonSets": []},
    "expectedVersion": VER,
    "updatedBy": "operator1",
})
check("操作员 PUT /api/config（改业务配置）→ 200", st == 200, f"实得 {st} {saved if st != 200 else ''}")

for path, why in (
    ("/api/users", "账号管理属系统级操作"),
    ("/api/config/export", "导出等于把整套现场配置带走"),
    ("/api/system/machine-code", "机器码是授权信息"),
):
    st, _ = op.call("GET", path)
    check(f"操作员 GET {path} → 403（{why}）", st == 403, f"实得 {st}")

st, _ = op.call("POST", "/api/config/reset", {"expectedVersion": None, "updatedBy": "operator1"})
check("操作员 POST /api/config/reset → 403", st == 403, f"实得 {st}")

st, _ = op.call("POST", "/api/config/import", {"content": "{}", "expectedVersion": None})
check("操作员 POST /api/config/import → 403（本轮收紧为 AdminOnly）", st == 403, f"实得 {st}")

# ── 查看者：只读 ──
for path in ("/api/status", "/api/config", "/api/timers", "/api/commands", "/api/logs/recent"):
    st, _ = viewer.call("GET", path)
    check(f"查看者 GET {path} → 200（只读可见）", st == 200, f"实得 {st}")

st, _ = viewer.call("PUT", "/api/config", {
    "settings": {"appName": "x", "buttons": [], "subButtonSets": []},
    "expectedVersion": VER,
})
check("查看者 PUT /api/config → 403（无操作权）", st == 403, f"实得 {st}")

st, _ = viewer.call("POST", "/api/actions/execute", {"label": "x", "actions": []})
check("查看者 POST /api/actions/execute → 403", st == 403, f"实得 {st}")

st, _ = viewer.call("GET", "/api/users")
check("查看者 GET /api/users → 403", st == 403, f"实得 {st}")

st, _ = viewer.call("DELETE", "/api/commands/whatever")
check("查看者 DELETE /api/commands/... → 403", st == 403, f"实得 {st}")

# ═══════════════════════════════════════════════════════════════
# D. 服务端守卫
# ═══════════════════════════════════════════════════════════════

print("\n[D] 服务端守卫")

st, r = admin.call("PUT", f"/api/users/{ADMIN_ID}", {"isEnabled": False})
check("停用自己 → 409 / SelfTarget", st == 409 and status_of(r) == "SelfTarget", f"实得 {st} {status_of(r)}")

st, r = admin.call("DELETE", f"/api/users/{ADMIN_ID}")
check("删除自己 → 409 / SelfTarget", st == 409 and status_of(r) == "SelfTarget", f"实得 {st} {status_of(r)}")

st, r = admin.call("PUT", f"/api/users/{ADMIN_ID}", {"role": "Viewer"})
check("唯一管理员自我降级 → 409 / LastAdmin", st == 409 and status_of(r) == "LastAdmin", f"实得 {st} {status_of(r)}")

st, r = admin.call("PUT", f"/api/users/{VW_ID}", {"role": "Admin"})
check("把查看者提升为管理员 → 200（此后有两个管理员）", st == 200, f"实得 {st}")

st, r = admin.call("PUT", f"/api/users/{VW_ID}", {"role": "Viewer"})
check("有同伴时把对方降回查看者 → 200", st == 200, f"实得 {st}")

st, r = admin.call("PUT", f"/api/users/{OP_ID}", {"userName": "operator-one"})
check(
    "改名 → 200",
    st == 200 and isinstance(r, dict) and (r.get("user") or {}).get("userName") == "operator-one",
    f"实得 {st} {r if st != 200 else (r.get('user') if isinstance(r, dict) else r)}",
)

st, r = admin.call("PUT", f"/api/users/{OP_ID}", {"userName": "VIEWER1"})
check(
    "改名撞上**别人**的名字（大小写不敏感）→ 400 / DuplicateName",
    st == 400 and isinstance(r, dict) and r.get("status") == "DuplicateName",
    f"实得 {st} {status_of(r)} {r.get('message') if isinstance(r, dict) else ''}",
)

st, r = admin.call("PUT", f"/api/users/{OP_ID}", {"userName": "OPERATOR-one"})
check(
    "改自己名字的大小写（不与他人冲突）→ 200（不是重名）",
    st == 200 and isinstance(r, dict) and (r.get("user") or {}).get("userName") == "OPERATOR-one",
    f"实得 {st} {(r.get('user') or {}).get('userName') if isinstance(r, dict) else r}",
)

# ── 自我降级：允许，但降完就**回不来了**（这是 RBAC 的正常语义，不是缺陷）──
st, _ = admin.call("PUT", f"/api/users/{VW_ID}", {"role": "Admin"})
check("再次把 viewer1 提为管理员 → 200（制造第二个管理员）", st == 200, f"实得 {st}")

st, r = admin.call("PUT", f"/api/users/{ADMIN_ID}", {"role": "Operator"})
check(
    "有同伴管理员时**允许**给自己降级（交接后退出是正常诉求）→ 200",
    st == 200 and isinstance(r, dict) and r.get("status") == "Ok",
    f"实得 {st} {r.get('status') if isinstance(r, dict) else r} {r.get('message') if isinstance(r, dict) else r}",
)

st, _ = admin.call("PUT", f"/api/users/{ADMIN_ID}", {"role": "Admin"})
check(
    "降级后**无法自我恢复** → 403（已不是管理员，只能请另一位管理员操作）",
    st == 403,
    f"实得 {st}",
)

st, _ = admin.call("GET", "/api/users")
check("降级后连账号列表都看不到了 → 403（角色当场生效）", st == 403, f"实得 {st}")

st, _ = admin.call("GET", "/api/config")
check("降级成操作员后仍可读配置 → 200", st == 200, f"实得 {st}")

promoter = Session("viewer1-as-admin")
ok, _ = promoter.login_ok("viewer1", "Viewer@2026B!")
st, r = promoter.call("PUT", f"/api/users/{ADMIN_ID}", {"role": "Admin"})
check(
    "请另一位管理员把 admin 提回来 → 200",
    ok and st == 200 and isinstance(r, dict) and r.get("status") == "Ok",
    f"实得 {st} {r.get('status') if isinstance(r, dict) else r}",
)

st, r = admin.call("GET", "/api/users")
check("admin 恢复管理员身份 → 200", st == 200, f"实得 {st}")

st, _ = admin.call("PUT", f"/api/users/{VW_ID}", {"role": "Viewer"})
check("把 viewer1 降回查看者 → 200", st == 200, f"实得 {st}")

# ═══════════════════════════════════════════════════════════════
# E. 会话即时生效
# ═══════════════════════════════════════════════════════════════

print("\n[E] 停用 / 降级对已签发会话当场生效")

st, _ = viewer.call("GET", "/api/config")
check("停用前：查看者会话可用", st == 200, f"实得 {st}")

st, _ = admin.call("PUT", f"/api/users/{VW_ID}", {"isEnabled": False})
check("停用查看者 → 200", st == 200, f"实得 {st}")

st, _ = viewer.call("GET", "/api/config")
check(
    "停用后：**同一个已签发的会话**在下一次请求即被作废 → 401（不必等 Cookie 过期）",
    st == 401,
    f"实得 {st}",
)

st, body = viewer.login("viewer1", "Viewer@2026B!")
check("被停用的账号无法重新登录 → 401", st == 401, f"实得 {st} {body}")

st, _ = admin.call("PUT", f"/api/users/{VW_ID}", {"isEnabled": True})
check("重新启用查看者 → 200", st == 200, f"实得 {st}")

ok, _ = viewer.login_ok("viewer1", "Viewer@2026B!")
check("重新启用后可以登录（密码未被重置，仍是改密后的那个）", ok)

# 降级当场生效：把 operator 降到 Viewer，其会话应立刻失去写权限
st, _ = admin.call("PUT", f"/api/users/{OP_ID}", {"role": "Viewer"})
check("把操作员降级为查看者 → 200", st == 200, f"实得 {st}")

st, cfg2 = op.call("GET", "/api/config")
check("降级后操作员仍可读配置 → 200", st == 200, f"实得 {st}")

st, _ = op.call("PUT", "/api/config", {
    "settings": {"appName": "RBAC 冒烟", "buttons": [], "subButtonSets": []},
    "expectedVersion": cfg2.get("version") if isinstance(cfg2, dict) else None,
})
check(
    "降级后操作员的写请求当场被拒 → 403（会话里的旧角色已在下一次请求被复核）",
    st == 403,
    f"实得 {st}",
)

st, _ = admin.call("PUT", f"/api/users/{OP_ID}", {"role": "Operator"})

# ═══════════════════════════════════════════════════════════════
# F. 登录锁定
# ═══════════════════════════════════════════════════════════════

print("\n[F] 连续登录失败锁定")

st, created_lock = admin.call("POST", "/api/users",
                              {"userName": "lockme", "password": "Lock@2026!", "role": "Viewer"})
LOCK_ID = (created_lock.get("user") or {}).get("id")
check("新建用于锁定测试的账号 → 200", st == 200 and isinstance(LOCK_ID, int), f"实得 {st} {LOCK_ID}")

victim = Session("lockme")
locked_at = None
for i in range(1, 6):
    st, body = victim.login("lockme", "definitely-wrong")
    if st == 401 and isinstance(body, dict) and "锁" in str(body.get("title", "")):
        locked_at = i
check("第 5 次连续失败即触发锁定，并在响应里说明", locked_at == 5, f"实得第 {locked_at} 次提示锁定")

st, body = victim.login("lockme", "Lock@2026!")
check(
    "锁定期间即使密码正确也拒绝登录 → 401",
    st == 401 and isinstance(body, dict) and "锁" in str(body.get("title", "")),
    f"实得 {st} {body}",
)

st, lst = admin.call("GET", "/api/users")
locked_user = find_user({"users": lst}, "lockme")
check("锁定状态对管理员可见（isLocked = true）", (locked_user or {}).get("isLocked") is True,
      f"实得 {(locked_user or {}).get('isLocked')}")
FIRST_UNTIL = (locked_user or {}).get("lockedUntil")

# 锁定期间继续输错 —— 锁定期**不得**被推后（否则锁定期可被无限延长，账号能被锁死）
for _ in range(6):
    victim.login("lockme", "definitely-wrong")

st, lst = admin.call("GET", "/api/users")
again_until = (find_user({"users": lst}, "lockme") or {}).get("lockedUntil")
check(
    "锁定期间继续输错不会延长锁定期（否则账号可被永久锁死）",
    FIRST_UNTIL == again_until,
    f"前 {FIRST_UNTIL} / 后 {again_until}",
)

st, r = admin.call("POST", f"/api/users/{LOCK_ID}/password", {"newPassword": "Lock@2026B!"})
check("管理员重置被锁定账号的密码 → 200（同时解锁）", st == 200, f"实得 {st}")

st, lst = admin.call("GET", "/api/users")
reset_user = find_user({"users": lst}, "lockme")
check("重置后锁定被解除", (reset_user or {}).get("isLocked") is False, f"实得 {(reset_user or {}).get('isLocked')}")
check("重置后 mustChangePassword 仍为 false（不强制改密）",
      (reset_user or {}).get("mustChangePassword") is False,
      f"实得 {(reset_user or {}).get('mustChangePassword')}")

# ═══════════════════════════════════════════════════════════════
# G. 删除与清理
# ═══════════════════════════════════════════════════════════════

print("\n[G] 删除账号")

st, r = admin.call("DELETE", f"/api/users/{LOCK_ID}")
check("删除账号 → 200 / Ok", st == 200 and status_of(r) == "Ok", f"实得 {st} {status_of(r)}")
check("删除后全量列表里不再有它", find_user(r, "lockme") is None, f"实得 {[u.get('userName') for u in users_of(r)]}")

st, _ = Session("ghost").login("lockme", "Lock@2026B!")
check("已删除的账号不能登录 → 401", st == 401, f"实得 {st}")

st, r = admin.call("DELETE", f"/api/users/{LOCK_ID}")
check("再删同一个 → 404 / NotFound", st == 404 and status_of(r) == "NotFound", f"实得 {st} {status_of(r)}")

st, r = admin.call("DELETE", f"/api/users/{OP_ID}")
check("清理操作员账号 → 200", st == 200, f"实得 {st}")

st, r = admin.call("DELETE", f"/api/users/{VW_ID}")
check("清理查看者账号 → 200", st == 200, f"实得 {st}")

st, lst = admin.call("GET", "/api/users")
check("只剩出厂管理员", len(users_of({"users": lst})) == 1, f"实得 {[u.get('userName') for u in users_of({'users': lst})]}")

print()
print(f"════════ 结果：{len(PASS)} PASS / {len(FAIL)} FAIL ════════")
if FAIL:
    for name in FAIL:
        print(f"  [X] {name}")
    sys.exit(1)
sys.exit(0)
