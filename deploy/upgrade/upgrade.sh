#!/usr/bin/env bash
# 中控服务版 —— 升级（Linux / systemd），带"自动备份 + 验收 + 失败自动回滚"。
#
# 与 deploy/upgrade/upgrade.ps1 是同一套步骤与判定，只是窗口换成 systemd。
# 步骤：前置检查 -> 停服 -> 基线(--dbinfo) -> 备份 -> 覆盖安装 -> 起服 -> 验收 -> (失败则回滚)
#
# 🔥 前提是 M6.1 的备份可用：没有备份就没有回滚，备份失败会直接中止。
# 🔥 备份是**全量**的：文件仓库多大，备份就要多大。磁盘余量检查就是为它。
#
# 用法：
#   sudo ./upgrade.sh --installer /tmp/ControlServer-0.2.0.tar.gz
#   sudo ./upgrade.sh --installer ... --previous-installer /root/old.tar.gz
#   ./upgrade.sh --skip-installer --no-service     # 演练：不装、不碰 systemd
#
# ⚠️ 本脚本**未在真实 Linux 主机上端到端验证过**（M6 的 Linux 侧一贯挂账）。
#    数据链路的验证在 deploy/upgrade/rehearse-upgrade.py（那是跑通过的）。

set -uo pipefail

INSTALLER=""
PREVIOUS_INSTALLER=""
BACKUP_ROOT="/var/backups/controlserver"
DATA_DIR="/var/lib/controlserver"
SERVICE="controlserver"
EXE="/opt/controlserver/ControlServer.Server"
HEALTH_PORT=8899
HEALTH_TIMEOUT=180
SKIP_INSTALLER=0
NO_SERVICE=0
NO_ROLLBACK=0

FAILURES=()
BACKUP_DIR=""
VERSION_BEFORE=""

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --installer)           INSTALLER="$2"; shift 2 ;;
        --previous-installer)  PREVIOUS_INSTALLER="$2"; shift 2 ;;
        --backup-root)         BACKUP_ROOT="$2"; shift 2 ;;
        --data-dir)            DATA_DIR="$2"; shift 2 ;;
        --service)             SERVICE="$2"; shift 2 ;;
        --exe)                 EXE="$2"; shift 2 ;;
        --port)                HEALTH_PORT="$2"; shift 2 ;;
        --skip-installer)      SKIP_INSTALLER=1; shift ;;
        --no-service)          NO_SERVICE=1; shift ;;
        --no-rollback)         NO_ROLLBACK=1; shift ;;
        -h|--help)             usage 0 ;;
        *) echo "不认识的参数：$1" >&2; usage 1 ;;
    esac
done

step() { printf '\n[%s] %s\n' "$1" "$2"; printf '%s\n' '------------------------------------------------------------------------------'; }

# 判定并记账。返回值就是判定结果，调用方可以 if 它。
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

# 把 --dbinfo 的 ASCII 键值读进调用方指定的关联数组。
# 只认小写开头的行（db / migrations / table.xxx / files.count），中文行乱码也不影响。
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
        # Git Bash 下 curl 对 127.0.0.1 有时返回 000，用 /dev/tcp 兜底
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
    sleep 1
    return 0
}

start_app() {
    if [ "$NO_SERVICE" = "1" ]; then
        echo '  --no-service：不碰 systemd（服务状态由外部负责）'
        return 0
    fi
    echo "  启动服务 ${SERVICE} …"
    systemctl start "$SERVICE"
}

# 从日志读"程序版本："那一行。三段版本号分不出包新旧，必须带构建标识。
running_version() {
    local f
    f="$(ls -1t "$DATA_DIR"/logs/server-*.log 2>/dev/null | head -1)"
    [ -n "$f" ] || return 1
    grep -o '程序版本：.*' "$f" 2>/dev/null | tail -1 | sed 's/^程序版本：//'
}

startup_error_count() {
    local f
    f="$(ls -1t "$DATA_DIR"/logs/server-*.log 2>/dev/null | head -1)"
    [ -n "$f" ] || { echo 0; return; }
    awk '/启动中/{n=NR} {a[NR]=$0} END{for(i=n;i<=NR;i++) if (a[i] ~ /\[(ERR|FTL)\]/) c++; print c+0}' "$f"
}

dir_size_mb() {
    [ -d "$1" ] || { echo 0; return; }
    du -sm "$1" 2>/dev/null | cut -f1
}

echo '=============================================================================='
echo 'ControlServer 升级（覆盖安装）'
echo '=============================================================================='
echo "  安装包    : ${INSTALLER:-(未提供)}"
echo "  数据目录  : ${DATA_DIR}"
echo "  备份到    : ${BACKUP_ROOT}"
echo "  服务名    : ${SERVICE}"

# ── 0. 前置检查 ────────────────────────────────────────────────────────────
step 0 '前置检查'

if [ "$NO_SERVICE" != "1" ] && [ "$(id -u)" != "0" ]; then
    echo '  × 需要 root（要停/起 systemd 服务、写 /opt 与 /var/lib）'
    exit 1
fi

if [ "$SKIP_INSTALLER" != "1" ]; then
    ok "$([ -f "$INSTALLER" ] && echo 1 || echo 0)" '安装包存在' "$INSTALLER" || {
        echo '升级中止：找不到安装包'
        exit 1
    }
else
    echo '  ！--skip-installer：跳过安装步骤（演练模式）'
fi

ok "$([ -f "$DATA_DIR/control.db" ] && echo 1 || echo 0)" '数据目录里有 control.db' "$DATA_DIR" || {
    echo '升级中止：这不是一份装好的现场，换 --data-dir'
    exit 1
}

ok "$([ -x "$EXE" ] && echo 1 || echo 0)" '找到可执行的服务端程序' "$EXE" || {
    echo '升级中止：用 --exe 指定'
    exit 1
}

RESOLVED_BACKUP="$(readlink -f "$BACKUP_ROOT" 2>/dev/null || echo "$BACKUP_ROOT")"
RESOLVED_DATA="$(readlink -f "$DATA_DIR" 2>/dev/null || echo "$DATA_DIR")"
ok "$(case "$RESOLVED_BACKUP/" in "$RESOLVED_DATA"/*) echo 0 ;; *) echo 1 ;; esac)" \
   '备份目录不在数据目录里面' "$RESOLVED_BACKUP" || {
    echo '升级中止：备份目录不能放在数据目录里'
    exit 1
}

SIZE_MB="$(dir_size_mb "$DATA_DIR")"
NEED_MB=$(awk -v s="$SIZE_MB" 'BEGIN{printf "%d", s*1.2+1}')
FREE_MB=$(df -Pm "$(dirname "$BACKUP_ROOT")" 2>/dev/null | awk 'NR==2{print $4}')
FREE_MB="${FREE_MB:-0}"
echo "  数据目录 ${SIZE_MB} MB；备份预计需要 ${NEED_MB} MB；备份盘剩余 ${FREE_MB} MB"
ok "$([ "$FREE_MB" -ge "$NEED_MB" ] && echo 1 || echo 0)" '备份盘余量够' \
   "需要 ${NEED_MB} MB，剩 ${FREE_MB} MB" || {
    echo '升级中止：磁盘余量不够放一份完整备份'
    exit 1
}

# ── 1. 停服 + 基线 ─────────────────────────────────────────────────────────
step 1 '停服并记录升级前基线'

ok "$(stop_app >&2 && echo 1 || echo 0)" '服务已停止' || {
    echo '升级中止：停服失败'
    exit 1
}

if ! dbinfo_into BEFORE; then
    ok 0 '升级前基线可读（--dbinfo）'
    echo '升级中止：--dbinfo 读不出来，无法做升级后比对'
    exit 1
fi
echo "    迁移条数 ${BEFORE[migrations]:-?}（最新 ${BEFORE[migrations.latest]:-?}）"
for k in $(printf '%s\n' "${!BEFORE[@]}" | sort); do
    case "$k" in
        table.*|files.*) printf '    %-34s %s\n' "$k" "${BEFORE[$k]}" ;;
    esac
done

VERSION_BEFORE="$(running_version || true)"
echo "    升级前跑的版本：${VERSION_BEFORE:-(日志里没有版本行)}"

# ── 2. 备份（fail-closed）──────────────────────────────────────────────────
step 2 '升级前备份（没有这一份就没有回滚）'

STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_DIR="${BACKUP_ROOT}/pre-upgrade-${STAMP}"
mkdir -p "$BACKUP_ROOT"

BACKUP_START="$(date +%s)"
BACKUP_OUT="$(run_exe --backup "$BACKUP_DIR")"
BACKUP_CODE=$?
BACKUP_END="$(date +%s)"
printf '%s\n' "$BACKUP_OUT" | sed 's/^/    | /'

if [ "$BACKUP_CODE" -ne 0 ] || [ ! -f "${BACKUP_DIR}/manifest.json" ]; then
    printf '  × 备份成功：退出码 %s\n' "$BACKUP_CODE"
    echo ''
    echo '  ！备份失败 —— 升级中止，正在把旧服务起回来。'
    echo '    真实升级里这里必须中止：在"没有回滚路径"的情况下动现场库，是不可接受的风险。'
    start_app || true
    exit 1
fi
echo "  √ 备份成功：耗时 $((BACKUP_END - BACKUP_START))s"

# ── 3. 安装 ────────────────────────────────────────────────────────────────
step 3 '覆盖安装'

if [ "$SKIP_INSTALLER" = "1" ]; then
    echo '  ！--skip-installer：跳过。下面直接验收"当前装着的这套"能不能起来。'
else
    echo "  运行 ${INSTALLER}"
    if ! "$INSTALLER" >>/dev/null 2>&1; then
        echo '  × 安装脚本返回失败'
        exit 2
    fi
    echo '  √ 安装脚本返回成功'
fi

# ── 4. 起服 ────────────────────────────────────────────────────────────────
step 4 '启动服务并等它可用'

start_app || true
T0="$(date +%s)"
if ! wait_health; then
    ok 0 '/healthz 返回 200' "超时 ${HEALTH_TIMEOUT}s"
    echo '升级失败：服务没起来'
    [ "$NO_ROLLBACK" = "1" ] && exit 2
    if [ -n "$PREVIOUS_INSTALLER" ] && [ -f "$PREVIOUS_INSTALLER" ]; then
        "$(dirname "$0")/rollback.sh" --backup-dir "$BACKUP_DIR" --installer "$PREVIOUS_INSTALLER" \
            --data-dir "$DATA_DIR" --service "$SERVICE" --exe "$EXE" --port "$HEALTH_PORT"
    else
        echo '  ！没有 --previous-installer：自动回滚只能恢复数据，换不回程序。'
        "$(dirname "$0")/rollback.sh" --backup-dir "$BACKUP_DIR" --skip-installer \
            --data-dir "$DATA_DIR" --service "$SERVICE" --exe "$EXE" --port "$HEALTH_PORT"
    fi
    exit $?
fi
ok 1 '/healthz 返回 200' "$(( $(date +%s) - T0 ))s"

# ── 5. 验收 ────────────────────────────────────────────────────────────────
step 5 '验收'

VERSION_AFTER="$(running_version || true)"
echo "    升级后跑的版本：${VERSION_AFTER:-(日志里没有版本行)}"
if [ -n "$VERSION_AFTER" ] && [ -n "$VERSION_BEFORE" ]; then
    if [ "$VERSION_AFTER" = "$VERSION_BEFORE" ]; then
        echo '    ！版本号与升级前相同 —— 装的可能是同一个包（或没换包）。不是失败，但请确认。'
    else
        echo "    √ 版本号已变化：${VERSION_BEFORE} -> ${VERSION_AFTER}"
    fi
fi
ok "$([ -n "$VERSION_AFTER" ] && echo 1 || echo 0)" '启动自述里有版本行（能确认跑的是哪个包）' || true

if ! dbinfo_into AFTER; then
    ok 0 '升级后 --dbinfo 可读'
    echo ''
    echo "  × ${#FAILURES[@]} 项验收不通过"
    exit 2
fi

ok "$([ "${AFTER[migrations]:-0}" -ge "${BEFORE[migrations]:-0}" ] && echo 1 || echo 0)" \
   '迁移条数没有减少' "${BEFORE[migrations]:-?} -> ${AFTER[migrations]:-?}" || true

# 只对"配置类"表要求完全相等。
# 🔴 不能要求所有表都只增不减：agent_metrics 有 30 天保留期，升级后第一次启动就可能
#    裁掉旧行 —— 那会让一个完全正常的升级被判成"数据丢了"（假 FAIL）。
for t in users config_documents; do
    k="table.${t}"
    if [ -n "${BEFORE[$k]:-}" ] && [ -n "${AFTER[$k]:-}" ]; then
        ok "$([ "${AFTER[$k]}" = "${BEFORE[$k]}" ] && echo 1 || echo 0)" "${t} 没变" \
           "${BEFORE[$k]} -> ${AFTER[$k]}" || true
    fi
done
ok "$([ "${AFTER[files.count]:-x}" = "${BEFORE[files.count]:-y}" ] && echo 1 || echo 0)" \
   '文件仓库文件数没变' "${BEFORE[files.count]:-?} -> ${AFTER[files.count]:-?}" || true

ERRS="$(startup_error_count)"
ok "$([ "$ERRS" -eq 0 ] && echo 1 || echo 0)" '本次启动日志没有 ERR/FTL' "${ERRS} 条" || true

# ── 6. 结论 ────────────────────────────────────────────────────────────────
step 6 '结论'

if [ "${#FAILURES[@]}" -eq 0 ]; then
    echo '  √ 升级成功'
    echo "    升级前版本：${VERSION_BEFORE:-(未知)}"
    echo "    升级后版本：${VERSION_AFTER}"
    echo "    升级前备份：${BACKUP_DIR}"
    echo '    ！这份备份先留着，至少留到"升级后跑满一个班次"为止。'
    echo '      确认没问题之前删掉它，就等于把回滚能力删掉了。'
    exit 0
fi

echo "  × ${#FAILURES[@]} 项验收不通过："
for f in "${FAILURES[@]}"; do echo "    - $f"; done

if [ "$NO_ROLLBACK" = "1" ]; then
    echo ''
    echo '  ！--no-rollback：不回滚。现场保持现状供排查。'
    echo "    （升级前备份仍在 ${BACKUP_DIR}，随时可以手工回滚）"
    exit 2
fi

echo ''
echo '开始自动回滚 ——'
if [ -n "$PREVIOUS_INSTALLER" ] && [ -f "$PREVIOUS_INSTALLER" ]; then
    "$(dirname "$0")/rollback.sh" --backup-dir "$BACKUP_DIR" --installer "$PREVIOUS_INSTALLER" \
        --data-dir "$DATA_DIR" --service "$SERVICE" --exe "$EXE" --port "$HEALTH_PORT"
else
    echo '  ！没有 --previous-installer：自动回滚只能恢复**数据**，换不回**程序**。'
    echo '    库退回旧结构之后，下次用当前版本启动会把它重新迁移上去 —— 那等于没回滚。'
    "$(dirname "$0")/rollback.sh" --backup-dir "$BACKUP_DIR" --skip-installer \
        --data-dir "$DATA_DIR" --service "$SERVICE" --exe "$EXE" --port "$HEALTH_PORT"
fi
exit $?
