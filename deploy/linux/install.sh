#!/usr/bin/env bash
#
# 中控服务版 —— Linux 安装 / 升级脚本（Ubuntu / Debian，systemd）
#
# 用法：
#   bash deploy/linux/build-linux.sh                          # 先构建（产出 dist/linux-x64）
#   sudo bash deploy/linux/install.sh [发布目录] [--data-dir <目录>] [--port <端口>]
#
#   [发布目录]    默认自动探测 publish/linux-x64 或 dist/linux-x64
#   --data-dir    数据目录，默认 /var/lib/controlserver
#   --port        HTTP 端口，默认 8899（<1024 需放开 unit 里的 AmbientCapabilities）
#
# 卸载：
#   sudo systemctl disable --now controlserver
#   sudo rm -f /etc/systemd/system/controlserver.service && sudo systemctl daemon-reload
#   sudo rm -rf /opt/controlserver            # 程序目录
#   sudo rm -rf /var/lib/controlserver        # 数据（含授权/数据库/配置）——请谨慎
#
set -euo pipefail

INSTALL_DIR="/opt/controlserver"
DATA_DIR="/var/lib/controlserver"
PORT="8899"
SERVICE_USER="controlserver"
SERVICE_NAME="controlserver"
SRC=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --data-dir) DATA_DIR="${2:?--data-dir 需要一个目录}"; shift 2 ;;
    --port)     PORT="${2:?--port 需要一个端口}"; shift 2 ;;
    -h|--help)  sed -n '2,18p' "${BASH_SOURCE[0]}"; exit 0 ;;
    -*)         echo "未知参数：$1（-h 看用法）" >&2; exit 1 ;;
    *)          SRC="$1"; shift ;;
  esac
done

if [[ -z "$SRC" ]]; then
  for candidate in publish/linux-x64 dist/linux-x64; do
    [[ -f "$candidate/ControlServer.Server" ]] && { SRC="$candidate"; break; }
  done
fi

if [[ -z "$SRC" || ! -f "$SRC/ControlServer.Server" ]]; then
  echo "[fail] 未找到发布产物。请先构建：" >&2
  echo "       bash deploy/linux/build-linux.sh" >&2
  exit 1
fi

if [[ $EUID -ne 0 ]]; then
  echo "[fail] 需要 root 权限：sudo bash deploy/linux/install.sh $SRC" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> 1/6 创建服务账户 $SERVICE_USER"
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

echo "==> 2/6 停止旧服务（若在运行）"
systemctl stop "$SERVICE_NAME" 2>/dev/null || true

echo "==> 3/6 复制程序到 $INSTALL_DIR"
rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
cp -r "$SRC"/. "$INSTALL_DIR"/
chmod +x "$INSTALL_DIR/ControlServer.Server"

echo "==> 4/6 预建数据目录 $DATA_DIR"
mkdir -p "$DATA_DIR"/config "$DATA_DIR"/logs "$DATA_DIR"/files "$DATA_DIR"/images
chown -R "$SERVICE_USER":"$SERVICE_USER" "$DATA_DIR"

# M6.4（对应审计清单 #40）：把"其他用户"关掉。
# 默认 mkdir 是 755 ⇒ 本机任何一个账号都能读 https/ 里的私钥与 control.db 里的口令哈希。
# 目录 750 + 文件 640 就够：服务账号是 owner，读写照旧。
# ⚠️ 收紧之后，备份/恢复/巡检都得 root 或该服务账号来跑（普通用户连目录都进不去）。
#    只改 mode、不动 owner —— chown 已在上一行做完。
find "$DATA_DIR" -type d -exec chmod 750 {} +
find "$DATA_DIR" -type f -exec chmod 640 {} +
chmod 750 "$DATA_DIR"
echo "    权限：目录 750 / 文件 640（仅 $SERVICE_USER 与 root 可访问）"

echo "==> 5/6 安装 systemd unit（端口 $PORT / 数据目录 $DATA_DIR）"
UNIT_TMP="$(mktemp)"
sed -e "s#^Environment=Server__Http__Urls=.*#Environment=Server__Http__Urls=http://0.0.0.0:$PORT#" \
    -e "s#^Environment=Server__Storage__DataDir=.*#Environment=Server__Storage__DataDir=$DATA_DIR#" \
    "$SCRIPT_DIR/controlserver.service" > "$UNIT_TMP"
install -m 0644 "$UNIT_TMP" "/etc/systemd/system/$SERVICE_NAME.service"
rm -f "$UNIT_TMP"
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

# 程序目录交给服务账户（便于后续自更新；要最小权限可改回 root:root）
chown -R "$SERVICE_USER":"$SERVICE_USER" "$INSTALL_DIR"

echo "==> 6/6 启动服务"
systemctl restart "$SERVICE_NAME"
sleep 2
systemctl --no-pager --full --lines=0 status "$SERVICE_NAME" || true

cat <<EOF

==> 完成
    管理界面： http://<本机IP>:$PORT
    数据目录： $DATA_DIR
    服务日志： journalctl -u $SERVICE_NAME -f
    默认账号： admin / admin123（登录后建议尽快自行修改）

    授权：首次启动为 30 天试用；正式授权请在界面「授权」页复制机器码，
          由厂商用 LicenseTool 签发后导入。

    若本机启用防火墙（ufw / firewalld），记得放行：
      sudo ufw allow $PORT/tcp              # 管理界面
      sudo ufw allow 9999/udp               # 对接码监听（默认 9999/udp，可在界面改）
EOF
