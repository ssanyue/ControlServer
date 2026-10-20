#!/usr/bin/env bash
# 中控服务版 —— 回滚（Linux / systemd）：停服 -> 恢复升级前备份 -> 装回旧版程序 -> 起服验收。
#
# 🔴 为什么不能"只把程序换回旧版"：
#    EF 的迁移**不回退**。旧版程序读一个已被新版升过结构的库，行为未定
#    （多出来的表/列、迁移历史对不上、改过名的列）。
#    回滚只有一条正确路径：用升级前的备份把数据退回那个版本该有的形状，再把程序换成那个版本。
#
# 用法：
#   sudo ./rollback.sh --backup-dir /var/backups/controlserver/pre-upgrade-20261017-223000 \
#                      --installer /root/ControlServer-0.1.0.tar.gz
#   ./rollback.sh --backup-dir ... --skip-installer --no-service   # 只恢复数据
#
# 🔥 恢复**必须先停服**。`--restore` 自己**不检查**服务是否在跑（只要求 --force 覆盖确认），
#    服务在跑的话内存里的旧数据会在恢复之后写回去 —— 症状是"恢复成功但数据还是旧的"。
#
# ⚠️ 本脚本**未在真实 Linux 主机上端到端验证过**（M6 的 Linux 侧一贯挂账）。

set -uo pipefail

BACKUP_DIR=""
INSTALLER=""
DATA_DIR="/var/lib/controlserver"
SERVICE="controlserver"
EXE="/opt/controlserver/ControlServer.Server"
HEALTH_PORT=8899
HEALTH_TIMEOUT=180
SKIP_INSTALLER=0
NO_SERVICE=0

FAILURES=()

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --backup-dir)   BACKUP_DIR="$2"; shift 2 ;;
        --installer)    INSTALLER="$2"; shift 2 ;;
        --data-dir)     DATA_DIR="$2"; shift 2 ;;
        --service)      SERVICE="$2"; shift 2 ;;
        --exe)          EXE="$2"; shift 2 ;;
        --port)         HEALTH_PORT="$2"; shift 2 ;;
        --skip-installer) SKIP_INSTALLER=1; shift ;;
        --no-service)   NO_SERVICE=1; shift ;;
        -h|--help)      usage 0 ;;
        *) echo "不认识的参数：$1" >&2; usage 1 ;;
    esac
done

if [ -z "$BACKUP_DIR" ]; then
    echo '缺少 --backup-dir' >&2
    usage 1
fi

step() { printf '\n[%s] %s\n' "$1" "$2"; printf '%s\n' '------------------------------------------------------------------------------'; }

ok() {
    local cond="$1" text="$2" detail="${3:-}"
    if [ "$cond" = "1" ]; then
        printf '  √ %s%s\n' "$text" "${detail:+：$detail}"
        return 0
    fi
    printf '  × %s%s\n' "$text" "${detail:+：$detail}"
    FAILURES+=("$text${detail:+：$detail}")
    return 1
}

run_exe() {
    Server__Storage__DataDir="$DATA_DIR" "$EXE" "$@" 2>&1
}

dbinfo_into() {
    local target="$1" out line key value
    out="$(run_exe --dbinfo)" || return 1
    eval "declare -gA $target=()"
    while IFS= read -r line; do
        case "$line" in
            [a-z]*=*)
                key="${line%%=*}"
                value="${line#*=}"
                printf -v "$target[$key]" '%s' "$value"
                ;;
        esac
    done <<< "$out"
    return 0
}

wait_health() {
    local i=0
    while [ "$i" -lt "$HEALTH_TIMEOUT" ]; do
        if curl --noproxy '*' -fsS -o /dev/null --max-time 3 \
                "http://127.0.0.1:${HEALTH_PORT}/healthz" 2>/dev/null; then
            return 0
        fi
        if (exec 3<>"/dev/tcp/127.0.0.1/${HEALTH_PORT}") 2>/dev/null; then
            exec 3>&- 2>/dev/null
            return 0
        fi
        sleep 1
        i=$((i + 1))
    done
    return 1
}

stop_app() {
    if [ "$NO_SERVICE" = "1" ]; then
        echo '  --no-service：不碰 systemd（假定服务已由外部停好）'
        return 0
    fi
    echo "  停止服务 ${SERVICE} …"
    systemctl stop "$SERVICE" || return 1
    local i=0
    while [ "$i" -lt 60 ]; do
        systemctl is-active --quiet "$SERVICE" || break
        sleep 1
        i=$((i + 1))
    done
    # 服务 Stopped 不等于"库没人用了"：进程退出、文件句柄释放还要一点时间。
    sleep 1
    return 0
}

start_app() {
    if [ "$NO_SERVICE" = "1" ]; then
        echo '  --no-service：不碰 systemd'
        return 0
    fi
    echo "  启动服务 ${SERVICE} …"
    systemctl start "$SERVICE"
}

running_version() {
    local f
    f="$(ls -1t "$DATA_DIR"/logs/server-*.log 2>/dev/null | head -1)"
    [ -n "$f" ] || return 1
    grep -o '程序版本：.*' "$f" 2>/dev/null | tail -1 | sed 's/^程序版本：//'
}

echo '=============================================================================='
echo 'ControlServer 回滚'
echo '=============================================================================='
echo "  备份目录  : ${BACKUP_DIR}"
echo "  数据目录  : ${DATA_DIR}"
echo "  旧版安装包: ${INSTALLER:-(未提供)}"

# ── 0. 前置检查 ────────────────────────────────────────────────────────────
step 0 '前置检查'

if [ "$NO_SERVICE" != "1" ] && [ "$(id -u)" != "0" ]; then
    echo '  × 需要 root'
    exit 1
fi

ok "$([ -f "${BACKUP_DIR}/manifest.json" ] && echo 1 || echo 0)" \
   '备份目录完整（有 manifest.json）' "$BACKUP_DIR" || {
    echo '回滚中止：这不是一份可用的备份目录'
    exit 1
}

if [ "$SKIP_INSTALLER" != "1" ]; then
    if [ -z "$INSTALLER" ]; then
        echo ''
        echo '  × 没有 --installer：只能恢复数据，恢复不了程序。'
        echo '    库会在下次启动时被当前（新版）程序重新 Migrate 上去 —— 那等于没回滚。'
        echo '    要么给 --installer，要么明确加 --skip-installer 表示"我知道程序没换回去"。'
        exit 1
    fi
    ok "$([ -f "$INSTALLER" ] && echo 1 || echo 0)" '旧版安装包存在' "$INSTALLER" || {
        echo '回滚中止：找不到旧版安装包'
        exit 1
    }
else
    echo '  ！--skip-installer：只恢复数据，不换程序（演练模式）'
fi

echo '  备份清单摘要：'
grep -o '"createdAt"[^,]*' "${BACKUP_DIR}/manifest.json" 2>/dev/null | sed 's/^/    /' || true

# ── 1. 停服 ────────────────────────────────────────────────────────────────
step 1 '停服（恢复之前必须停，否则内存旧数据会被写回去）'

ok "$(stop_app >&2 && echo 1 || echo 0)" '服务已停止' || {
    echo '回滚中止：停服失败'
    exit 1
}

declare -A BEFORE=()
dbinfo_into BEFORE || echo '  ！（--dbinfo 读不出来，跳过"恢复前"的读数）'

# ── 2. 恢复数据 ────────────────────────────────────────────────────────────
step 2 '恢复数据（--restore --force）'

RESTORE_START="$(date +%s)"
RESTORE_OUT="$(run_exe --restore "$BACKUP_DIR" --force)"
RESTORE_CODE=$?
RESTORE_END="$(date +%s)"
printf '%s\n' "$RESTORE_OUT" | sed 's/^/    | /'

ok "$([ "$RESTORE_CODE" -eq 0 ] && echo 1 || echo 0)" '数据恢复成功' \
   "耗时 $((RESTORE_END - RESTORE_START))s" || {
    echo '回滚中止：恢复失败'
    exit 2
}

# ── 3. 装回旧版程序 ────────────────────────────────────────────────────────
step 3 '装回旧版程序'

if [ "$SKIP_INSTALLER" = "1" ]; then
    echo '  ！跳过（--skip-installer）。程序仍是当前版本。'
    echo '    ！注意：库已经退回旧结构，这个版本的程序下次启动会把它重新迁移上去。'
else
    echo "  运行 ${INSTALLER}"
    if ! "$INSTALLER" >>/dev/null 2>&1; then
        echo '  × 旧版安装返回失败'
        exit 2
    fi
    echo '  √ 旧版安装返回成功'
fi

# ── 4. 起服 ────────────────────────────────────────────────────────────────
step 4 '启动服务并等它可用'

if [ "$NO_SERVICE" = "1" ]; then
    # --no-service 的语义是"服务由外部负责"，所以这里既不起服也不验 /healthz。
    # 🔴 必须明说"没验证"，否则这一屏看起来像"起来了"——那是最坏的假证据。
    echo '  ！--no-service：没有起服，也没有验证 /healthz。'
    echo '    "服务能不能用"这一项**未验证**，请自己启动后打开页面确认。'
else
    start_app || true
    T0="$(date +%s)"
    if ! wait_health; then
        ok 0 '/healthz 返回 200' "超时 ${HEALTH_TIMEOUT}s"
        echo '回滚失败：服务没起来'
        exit 2
    fi
    ok 1 '/healthz 返回 200' "$(( $(date +%s) - T0 ))s"
fi

# ── 5. 验收 ────────────────────────────────────────────────────────────────
step 5 '验收'

declare -A AFTER=()
if ! dbinfo_into AFTER; then
    ok 0 '--dbinfo 可读'
    exit 2
fi

# 恢复成功后库的形状应当回到备份那一天。迁移条数是最直接的证据。
if [ -n "${BEFORE[migrations]:-}" ]; then
    ok "$([ "${AFTER[migrations]:-x}" = "${BEFORE[migrations]}" ] && echo 1 || echo 0)" \
       '迁移条数回到恢复前那一份' "${BEFORE[migrations]} -> ${AFTER[migrations]:-?}" || true
fi
echo "    现在库里的迁移：${AFTER[migrations]:-?} 条（最新 ${AFTER[migrations.latest]:-?}）"

VERSION_NOW="$(running_version || true)"
echo "    当前跑的版本：${VERSION_NOW:-(日志里没有版本行)}"
ok "$([ -n "$VERSION_NOW" ] && echo 1 || echo 0)" '启动自述里有版本行' || true

# ── 6. 结论 ────────────────────────────────────────────────────────────────
step 6 '结论'

if [ "${#FAILURES[@]}" -eq 0 ]; then
    echo '  √ 回滚完成'
    echo ''
    echo '  ！回滚的代价（这是设计如此，不是缺陷）：'
    echo '    升级之后、回滚之前产生的所有业务数据都没有了 ——'
    echo '    配置改动、审计记录、遥测明细、上传的文件。'
    echo '    备份只包含升级前那一刻的状态，恢复就是回到那一刻。'
    echo ''
    echo '  ！如果这段时间里有人改过配置，需要重新改一遍。'
    exit 0
fi

echo "  × ${#FAILURES[@]} 项不通过："
for f in "${FAILURES[@]}"; do echo "    - $f"; done
echo ''
echo "  备份仍在 ${BACKUP_DIR} —— 可以再试一次，或人工检查库。"
exit 2
