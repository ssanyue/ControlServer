# REF-M3 · Linux 跨平台交付（改部署脚本前必读）

> 配套：`docs/Linux-部署说明.md`（面向使用者）；本文件面向**改脚本/改宿主的人**。
> 范围：M3「服务化与跨平台」的 **Linux 半边**（Windows 侧见 `REF-M2-模块细节.md` §25 / §33）。

## 1. 文件地图

| 要做的事 | 改哪 |
|---|---|
| 一键构建（前端 → 发布 → 打包） | `deploy/linux/build-linux.sh` |
| 安装 / 升级（systemd） | `deploy/linux/install.sh` |
| systemd unit 模板 | `deploy/linux/controlserver.service`（`install.sh` 会 sed 改写「监听地址 / 数据目录」两行） |
| 容器镜像 | `deploy/linux/Dockerfile` ＋ 仓库根 `.dockerignore` |
| 用户文档 | `docs/Linux-部署说明.md` |
| 换行符纪律 | `.gitattributes`（`*.sh` / `*.service` / `Dockerfile` / `.dockerignore` 强制 LF） |
| 宿主形态判定 | `src/ControlServer.Server/Program.cs`（`IsWindowsService()` → `INVOCATION_ID` → 控制台） |
| 默认数据根 | `src/ControlServer.Core/Services/DataDirectoryDefaults.cs`（Linux = `/var/lib/controlserver`） |
| Linux 机器码 | `src/ControlServer.Platform.Linux/LinuxMachineCodeProvider.cs`（`/etc/machine-id` + DMI `product_uuid` → SHA256） |
| 托盘 no-op | `src/ControlServer.Platform.Linux/NoopTrayIconHost.cs` |

## 2. 与 Windows 的差异（改代码时别踩）

- **单实例守卫只在 Windows 生效**（`OperatingSystem.IsWindows() && !IsWindowsService()`）。
  Linux 上第二个实例会因端口占用直接退出 —— 单实例由 systemd 保证，别去加跨平台互斥体。
- **托盘 = no-op**：Linux 侧无通知区域，`NoopTrayIconHost` 占位。
- **服务模式不弹浏览器**：`isServiceHost` 含 `INVOCATION_ID`（systemd 注入），`BrowserLauncher.TryOpen` 因此不发动作。
- **`appsettings.json` 不随发布输出**（csproj `CopyToPublishDirectory=Never`）⇒ Linux 上配置全靠
  「代码默认值 + 环境变量（`Server__X__Y`）」。unit 与 Dockerfile 已把**数据目录 / 监听地址 / AutoOpenBrowser** 显式钉住。
- **前端产物是嵌入 exe 的**：改界面必须重跑前端构建再 publish（`build-linux.sh` 默认会做）。

## 3. ⚠️ 踩过的坑（每条都真实发生过）

1. **`command -v dotnet` 可能命中「只有运行时、没有 SDK」的 dotnet**（本机正是如此，且 `global.json` 要求 8.0.100）
   ⇒ `publish` 报 `The application 'publish' does not exist`。
   **判据必须是「`--list-sdks` 非空」**，不能只看命令是否存在。`build-linux.sh` 已按此逐个候选验证。
2. **Windows 上打 tar 会丢可执行位**：NTFS 无 exec 概念，MSYS 的 `chmod` 也写不进扩展属性
   （实测 `chmod +x` 后 `ls -la` 仍是 `-rw-r--r--`）⇒ tar 里全是 `0644`，Linux 解包后
   `./ControlServer.Server` 报 **Permission denied**（目录丢 x 则连进都进不去）。
   **修法：`tar ... --mode='u+rwx,go+rx'`**。`install.sh` 另有 `chmod +x` 兜底、Docker 走 Linux 构建容器，均不受影响。
3. **`.sh` / `.service` 必须是 LF**：CRLF 会让 Linux 报 `bad interpreter: ...^M`。
   `.gitattributes` 已强制；用编辑器生成后应复核字节（本项目实测为 LF + noBOM）。
4. **Git Bash 的 `/tmp` 是 MSYS 虚拟路径**：Windows 原生 python / dotnet 读不到（`FileNotFoundError`）。
   临时文件放仓库内相对路径。
5. **`Server__Http__Port` 无效**（`Urls` 优先）：改端口必须设 `Server__Http__Urls`。unit 里钉的就是它。
6. **容器机器码会漂移**：`/etc/machine-id` 常继承自镜像、`product_uuid` 来自宿主机 ⇒ 换宿主机即机器码变化、授权失效。
   稳妥做法是给容器挂一个固定 `machine-id`（见 `docs/Linux-部署说明.md` §4.1）。

## 4. 验证现状（诚实声明）

**已做（本机可验证的部分）**：
- 交叉发布 `-r linux-x64 --self-contained` 成功；产物 `ControlServer.Server` 为 **ELF**（`7f 45 4c 46`），
  `libcoreclr.so` / `libe_sqlite3.so` / `libSystem.Security.Cryptography.Native.OpenSsl.so` 齐备；前端已嵌入（dll 内 `wwwroot` 命中）。
- `build-linux.sh` **端到端实跑**通过：dotnet 探测（8.0.425）→ publish → 剔除 pdb → 打包 **46 MB** tar.gz；
  包内权限 **0755**（含 `.` 目录、apphost 可执行）；tar 内 **无 pdb**。
- `bash -n` 语法检查通过；`install.sh` 的 sed 改写（`--port 9090` / `--data-dir /srv/cs`）实测生效。
- `appsettings.json` 未随包输出（**有意**）；`app.ico` 不参与 Linux 交付。

**未做（本机不可为）**：
- **未在真实 Linux 上运行过**：本机 `wsl.exe` 被安全策略列入程序黑名单（不可绕过）、未安装 Docker
  ⇒ systemd 启动、Docker 构建、真实 UDP 对接码收发**均未实测**。首次上线以 `journalctl` 为准。
- 未产出 RPM / deb / 原生包管理器包。
