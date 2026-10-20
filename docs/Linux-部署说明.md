# 中控服务版 —— Linux 部署说明

Linux 上**没有**绿色版/托盘那一套（那是 Windows 专属）。Linux 交付只有「一份自包含的程序目录」，
由 **systemd** 或 **Docker** 负责"开机自启 / 崩溃重启 / 日志收集"。

| 方式 | 适用场景 | 谁来托管 |
|---|---|---|
| **systemd**（推荐） | 物理机 / 虚拟机（Ubuntu、Debian…） | `install.sh` + systemd |
| **Docker** | 容器化环境 | `Dockerfile` + `docker run` |
| **直接运行** | 临时试用 | 手动 `./ControlServer.Server` |

三种方式跑的是**同一个自包含可执行文件**，**都不需要**系统预装 .NET。

---

## 1. 与 Windows 版的差异（先看这张表）

| 能力 | Windows | Linux |
|---|---|---|
| 宿主形态 | Windows 服务 / 绿色版双击 | systemd（按 `INVOCATION_ID` 自动识别） |
| 托盘图标 | 有 | **无**（Linux 侧为 no-op，服务无人值守运行） |
| 自动开浏览器 | 绿色版会 | **不会**（服务模式一律不弹） |
| 单实例守卫 | 有（命名互斥体） | **无**（由 systemd 保证只有一个实例） |
| 数据目录 | `%ProgramData%\ControlServer` | `/var/lib/controlserver` |
| 需要装 .NET | 否 | 否 |

---

## 2. 构建交付包

在**任意平台**（Windows Git Bash / macOS / Linux）交叉编译：

```bash
bash deploy/linux/build-linux.sh
```

| 产物 | 说明 |
|---|---|
| `dist/linux-x64/` | 发布目录（自包含，解包即用） |
| `dist/controlserver-linux-x64-<日期>.tar.gz` | 交付包（已剔除 pdb） |

参数：`--skip-web` 跳过前端构建；`--out <目录>` 指定发布目录。

> ⚠️ 前端产物是**嵌入 exe** 的，改了界面必须重跑前端构建再打包（脚本默认会做）。

---

## 3. 方式一：systemd（推荐）

### 3.1 安装 / 升级

```bash
# 交付包传到目标机并解包
tar -xzf controlserver-linux-x64-*.tar.gz -C /tmp/controlserver

# 安装（需要 root）
sudo bash deploy/linux/install.sh /tmp/controlserver
```

`install.sh` 做六件事：建服务账户 → 停旧服务 → 复制到 `/opt/controlserver` → 建数据目录 →
安装 systemd unit → 启动服务。

| 参数 | 默认 | 说明 |
|---|---|---|
| `[发布目录]` | 自动探测 `publish/linux-x64` 或 `dist/linux-x64` | 打包后的目录 |
| `--data-dir <目录>` | `/var/lib/controlserver` | 数据目录（数据库/授权/图片/配置/日志） |
| `--port <端口>` | `8899` | HTTP 端口（<1024 需放开 unit 里的 `AmbientCapabilities`） |

**升级** = 用新交付包重跑同一条命令（会先停旧服务、覆盖程序、重启；**数据目录不动**）。

### 3.2 目录与端口

| 项 | 值 |
|---|---|
| 程序 | `/opt/controlserver` |
| 数据 | `/var/lib/controlserver`（`control.db`、`license.lic`、`config/`、`images/`、`files/`、`logs/`） |
| 服务账户 | `controlserver`（系统账户，无登录 shell） |
| HTTP | `8899/tcp` |
| 对接码监听 | `9999/udp` |

⚠️ **对接码监听走 UDP** —— 只放行 TCP 没有用。防火墙/NAT 必须放行 `udp/9999`，
否则被控端发的对接码服务收不到（表现为"发了码但没动作"）。

### 3.3 运维命令

```bash
systemctl status controlserver
systemctl restart controlserver
journalctl -u controlserver -f
journalctl -u controlserver --since "10 min ago"
```

### 3.4 卸载

```bash
sudo systemctl disable --now controlserver
sudo rm -f /etc/systemd/system/controlserver.service && sudo systemctl daemon-reload
sudo rm -rf /opt/controlserver          # 程序目录
sudo rm -rf /var/lib/controlserver      # 数据（含授权）——谨慎
```

---

## 4. 方式二：Docker

```bash
# 构建（在仓库根执行）
docker build -f deploy/linux/Dockerfile -t controlserver:latest .

# 运行
docker run -d --name controlserver \
  -p 8899:8899 \
  -p 9999:9999/udp \
  -v controlserver-data:/var/lib/controlserver \
  --restart unless-stopped \
  controlserver:latest
```

等价的 compose：

```yaml
services:
  controlserver:
    build:
      context: .
      dockerfile: deploy/linux/Dockerfile
    image: controlserver:latest
    ports:
      - "8899:8899"
      - "9999:9999/udp"
    volumes:
      - controlserver-data:/var/lib/controlserver
    restart: unless-stopped
volumes:
  controlserver-data:
```

**四条必须**：

1. **`-p 9999:9999/udp`** —— 漏了 UDP 映射，对接码监听等于没接；
2. **挂卷 `/var/lib/controlserver`** —— 否则容器一删，授权与配置全丢；
3. 配置用**环境变量**覆盖（镜像里不带 `appsettings.json`），例如 `-e Server__Http__Urls=http://0.0.0.0:9090`；
4. **固定容器机器码**（见下），否则换宿主机后授权失效。

日志：`docker logs -f controlserver`。

### 4.1 ⚠️ 容器里的机器码会漂移

Linux 机器码 = `/etc/machine-id` + DMI `product_uuid` → SHA256。容器场景下：

- `/etc/machine-id` 往往**继承自镜像**（同一镜像的所有容器相同）；
- `/sys/class/dmi/id/product_uuid` 通常来自**宿主机**（换宿主机就变）。

⇒ 两者都可用时，**换宿主机 / 换镜像 ⇒ 机器码变化 ⇒ 已签发的授权失效**。
（两者都取不到时才会回退到数据目录里的持久化标识——挂卷后反而稳定。）

稳妥做法：给容器固定一个 machine-id，例如

```bash
echo 0123456789abcdef0123456789abcdef > ./machine-id     # 只做一次，长期保存
docker run -d --name controlserver \
  -v "$PWD/machine-id:/etc/machine-id:ro" \
  -p 8899:8899 -p 9999:9999/udp \
  -v controlserver-data:/var/lib/controlserver \
  controlserver:latest
```

---

## 5. 配置（环境变量覆盖）

`appsettings.json` **不随发布输出**；所有配置都有代码默认值。要改哪项，
就用环境变量（层级用 `__` 分隔）覆盖，或把一份 `appsettings.json` 放到发布目录根（= exe 同目录）。

| 配置键 | 默认 | 说明 |
|---|---|---|
| `Server__Http__Urls` | `http://0.0.0.0:8899` | 监听地址。**改端口要用它**；`Server__Http__Port` 会被 Urls 覆盖而失效 |
| `Server__Listener__PairingPort` | `9999` | 对接码 UDP 端口 |
| `Server__Storage__DataDir` | `/var/lib/controlserver` | 数据目录 |
| `Server__Startup__AutoOpenBrowser` | `true` | 服务模式无效（不会弹浏览器） |
| `Server__Auth__EnforcePasswordChange` | `false` | 强制改密闸门（2026-10-09 起默认**关闭**；置 `true` 后仅拦截被手工标记 `MustChangePassword` 的账号） |
| `Server__License__Enforcement` | `Warn` | 授权闸门档位（`Off` / `Warn` / `Enforce`） |
| `ASPNETCORE_ENVIRONMENT` | `Production`（unit/镜像已设） | 运行环境 |

---

## 6. 授权（Linux）

流程与 Windows 完全一致，只是机器码的**算源**不同：

1. 首次启动自动获得 **30 天试用**；
2. 管理员登录 →「授权」页 → **复制机器码**；
3. 厂商用 `ControlServer.LicenseTool gen --bind <机器码> …` 签发 `.lic`；
4. 在「授权」页导入 —— **导入后无需重启**，监听自动恢复。

Linux 机器码由 `Platform.Linux/LinuxMachineCodeProvider` 计算（`/etc/machine-id` + DMI UUID），
与 Windows 算法不同 ⇒ **同一台机器的 Windows 授权与 Linux 授权不能互换**。

---

## 7. 排错

| 现象 | 原因 / 处理 |
|---|---|
| `bad interpreter: /usr/bin/env bash^M` | 脚本被存成 CRLF。用 `dos2unix` 转换，或从仓库重取（`.gitattributes` 已强制 LF） |
| `systemctl status` 一直 `activating` | unit 是 `Type=notify`；看 `journalctl`，多半是端口绑定失败（被占用 / <1024 且无权限） |
| 页面打不开但进程在 | 防火墙没放行 TCP 端口；或改端口只改了 `Server__Http__Port`（无效，要用 `Server__Http__Urls`） |
| 对接码发出去没反应 | 防火墙/NAT 未放行 `udp/9999`；或对接码端口被改过 |
| 手动启动第二个实例无反应/立即退出 | Linux **无**单实例守卫，第二实例因端口占用退出。请用 systemd，别手动重复启动 |
| 换宿主机后提示未授权 | 容器机器码漂移（§4.1）。重新取机器码并签发，或固定 machine-id |
| 授权页显示未授权 | 试用已过且未导入授权 |

---

## 8. 已知限制（诚实声明）

- Linux 侧**无托盘图标**（服务无人值守；`Platform.Linux` 提供 no-op 实现）。
- **本轮未在真实 Linux 上实测运行**：已完成交叉发布（产物为 ELF、`libe_sqlite3.so` 等齐备）、
  脚本 `bash -n` 语法检查、systemd unit 自洽性验证；但本机没有可用 Linux 环境
  （WSL 被安全策略禁用、未安装 Docker）⇒ **首次上线请按 §3.3 看日志确认**。
- 未提供 RPM / deb / 原生包管理器包；当前交付形态为 tar.gz + systemd + Docker 三种。
