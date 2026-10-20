#!/usr/bin/env bash
#
# M6.1 数据备份 —— 薄壳。真正的活在 ControlServer.Server --backup 里。
#
# 为什么只是一层薄壳：备份的核心是"数据库要拿到一致快照"（WAL 模式下必须 VACUUM INTO），
# 那件事只有服务自己的可执行文件做得对。脚本再实现一遍 SQLite 逻辑，就会有两个会分叉的版本。
# 所以这里只做三件事：找程序、拼一个带时间戳的目录、透传退出码。
#
# ⚠️ 备份**不需要**停服务 —— 这是它和恢复最重要的区别。
#
# 用法：
#   sudo deploy/backup/backup.sh /backup
#   sudo deploy/backup/backup.sh /backup --include-logs
#
# 🔴 必须 root（M6.4）：数据目录的 ACL 收紧后只留 root:controlserver，
#    普通用户连读都读不了 —— 不提前拦住的话，症状是程序跑到一半报 Permission denied。
#
set -euo pipefail

if [ "$(id -u)" != "0" ]; then
    echo '【需要 root】数据目录权限收紧后只有 root 与服务账号能访问。' >&2
    echo '  请用 sudo 执行本脚本。' >&2
    exit 1
fi

TARGET="${1:-}"
shift || true
EXTRA=("$@")

if [ -z "$TARGET" ]; then
    echo "用法: backup.sh <目标父目录> [--include-logs]" >&2
    echo "  例: sudo backup.sh /backup" >&2
    exit 1
fi

INSTALL_DIR="${CONTROLSERVER_INSTALL_DIR:-/opt/controlserver}"
EXE="$INSTALL_DIR/ControlServer.Server"

if [ ! -x "$EXE" ]; then
    echo "【找不到程序】$EXE" >&2
    echo "  用环境变量 CONTROLSERVER_INSTALL_DIR 指定安装目录。" >&2
    exit 2
fi

# 数据目录与程序口径一致：环境变量优先，其次 Linux 默认根。
DATA_ROOT="${Server__Storage__DataDir:-/var/lib/controlserver}"

# ── 备份目录不能落在数据目录里面 ──
case "$(readlink -f "$TARGET")/" in
    "$(readlink -f "$DATA_ROOT")"/*)
        echo "【拒绝】备份目录不能放在数据目录里面：$TARGET" >&2
        echo "  放在一起的话，那块盘坏的时候两样一起没 —— 那不叫备份。" >&2
        exit 2
        ;;
esac

# ── 同一块盘：只警告，不阻止（有的现场只有一块盘，流程是"先本地再拷走"）──
target_dev="$(stat -c '%d' "$TARGET" 2>/dev/null || true)"
data_dev="$(stat -c '%d' "$DATA_ROOT" 2>/dev/null || true)"
if [ -n "$target_dev" ] && [ "$target_dev" = "$data_dev" ]; then
    echo "注意：备份目录和数据目录在同一块设备上。"
    echo "      备份完成后请把它拷到另一块盘 / 另一台机器，否则盘坏时两样一起没。"
    echo
fi

DEST="$TARGET/ControlServer-$(date +%Y%m%d-%H%M)"

echo "数据目录：$DATA_ROOT"
echo "备份到：  $DEST"
echo

# 直接调程序并把它的输出原样交给运维 —— 不在脚本里重新包装一遍文字，
# 否则"程序说的"和"脚本说的"迟早不一样。
set +e
"$EXE" --backup "$DEST" ${EXTRA[@]+"${EXTRA[@]}"}
code=$?
set -e

if [ $code -ne 0 ]; then
    echo
    echo "【备份失败】退出码 $code（见上面的输出）" >&2
    exit $code
fi

echo
echo "下一步：把 $DEST 整个目录拷到另一块盘或另一台机器。"
echo "恢复：  $EXE --restore <那个目录> --force    （先 systemctl stop controlserver）"
