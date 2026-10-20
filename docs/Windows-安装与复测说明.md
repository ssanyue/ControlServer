# Windows 安装、自检与复测说明（中控服务版）

> 面向：**在目标机上装/验/卸这台服务的人**，以及做发布复测的人。
> 相关设计细节见 `.workbuddy/memory/REF-M2-模块细节.md` **§25 / §33**；本文只讲"怎么用、怎么看结果"。

---

## 1. 三个产物，别混

| 产物 | 生成者 | 定位 | 关键特征 |
|---|---|---|---|
| `dist\win-x64\ControlServer.Server.exe` | `build-portable-exe.ps1` | **绿色版**：单文件双击即用 | **严格 1 个文件**；托盘内建在 server 里 |
| `dist\win-x64-service\`（2 个 exe） | 同上 | **服务版原始产物** = 安装包的 payload | `ControlServer.Server.exe` + `ControlServer.TrayAgent.exe` |
| `dist\installer\ControlServer-Setup-<版本>.exe` | `build-installer.ps1` | **安装包**：双击向导装完即跑 | 内含上面 2 个 exe + 4 个运维脚本 |

目标机**不需要**预装 .NET —— 程序本体是 self-contained。

### 构建

```powershell
# 一次产出全部三个产物（前端 → 发布 → 打包 tar/Setup）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1

# 复测时若只是想重新封包（payload 没变），可跳过重新发布
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1 -SkipPayloadBuild
```

> ⚠️ **改动过 C# 代码后不要用 `-SkipPayloadBuild`** —— 那会拿旧 exe 去封包。
> 想让脚本强制重发：先删掉 `dist\win-x64` 与 `dist\win-x64-service`。

---

## 2. 安装

### 图形安装（现场推荐）

右键 `ControlServer-Setup-1.0.0.exe` → **以管理员身份运行** → 一路下一步。

### 静默安装（自动化 / 复测用）

```powershell
ControlServer-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="C:\temp\setup.log"
```

**退出码即判据**：

| 退出码 | 含义 |
|---|---|
| `0` | 程序文件 + 服务注册 + 启动 + 自检**全部通过** |
| `1` | **程序文件装好了，但服务步骤失败** —— 看弹窗/Finish 页给出的具体原因 |

安装包做的 7 件事（都复用 `install-service.ps1`，不在 `.iss` 里重写）：

1. 复制程序到 `C:\Program Files\ControlServer`
2. 建数据目录 `%ProgramData%\ControlServer`
3. 注册 Windows 服务（开机自启 + 崩溃自愈 5s/10s/30s）
4. `sc sdset` 授权交互用户启停服务（托盘助手要用）
5. 放行防火墙 `TCP/8899`、`UDP/9999`
6. 启动服务 + 部署托盘助手（HKCU 启动项）
7. **收尾自检**：服务真的 `Running` 且端口真的在听，否则报错并指出日志路径

> ⚠️ **同一个安装包可以反复运行**：重装前会先跑一遍 `uninstall-service.ps1 -SkipDeleteFiles`
> 把旧服务停掉并注销，否则 `ControlServer.Server.exe` 被占用、复制必然失败。

> ℹ️ **出厂不再强制改密**（2026-10-09 起）：出厂账号 `admin / admin123` 登录后即可直接使用，
> 不会被要求改密。仍建议尽快在「修改密码」里换掉默认口令。
> （闸门机制保留，可显式开启：`Server__Auth__EnforcePasswordChange=true`。）

---

## 3. 装完自检 —— `verify-install.ps1`

随包带到 `C:\Program Files\ControlServer\tools\`，**只读、不需要管理员**：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\ControlServer\tools\verify-install.ps1"
```

逐项核对：程序文件 / 服务已注册 / 开机自启 / `Running` / 端口监听 / **HTTP 真能打开** /
防火墙 / 托盘 / 数据目录。退出码 `0` = 全过，`1` = 有硬失败。

- 输出符号含义：`[OK]` 通过、`[!!]` 硬失败、`[--]` 软性提示（不计失败）。
- HTTP 探活**强制直连**（`Proxy = $null`）—— 目标机装了 Clash / 公司代理也不会误报 502。
- 硬判据是**纯 socket 端口探测**，HTTP 只作旁证：端口通就算服务活着。

---

## 4. 复测（第四轮）：`retest-install.ps1`

把「安装 → 自检 → **重装** → 自检 → 卸载 → 清理核对」一次跑完，输出判定矩阵 + 报告文件。

### 为什么需要"重装"这一步

上一轮真机复盘（见 REF §33.11）定位到的根因是：PowerShell 5.1 往原生程序传参时会把
**内层引号整个吞掉、只在空格处断成新 argv** ⇒

```
sc.exe create X binPath= "C:\Program Files\...\ControlServer.Server.exe" --flag
→ binPath= 只拿到 "C:\Program"  ⇒  sc.exe 打印帮助 + 退出码 1639
```

已改 **`New-Service`（cmdlet 参数绑定，不走命令行拼接）**，并加了 ImagePath 回读兜底。
这个 bug **只在「装到含空格的路径（默认 Program Files）」时才现形**，且**重装才会暴露竞态** ——
静态检查、编译、单测全都拦不住，只能在真机上跑一次。

### 用法

```powershell
# ① 先只读看一眼现状（不需要管理员，拿它确认脚本能跑）
powershell -NoProfile -ExecutionPolicy Bypass -File retest-install.ps1 -Check

# ② 完整复测（**必须管理员**）
powershell -NoProfile -ExecutionPolicy Bypass -File retest-install.ps1
```

安装包路径会自动找：**先看脚本同目录**，再看 `dist\installer`。
所以最省事的做法是把 `retest-install.ps1` 与 `ControlServer-Setup-1.0.0.exe`
**拷到目标机的同一个文件夹**再跑。

常用开关：

| 开关 | 作用 |
|---|---|
| `-Check` | 只读模式，不装不卸 |
| `-SetupExe <路径>` | 显式指定安装包 |
| `-Rounds 3` | 安装轮数（默认 2；第 2 轮即"重装"场景） |
| `-SkipUninstall` | 跑完不卸载（现场想留着用） |
| `-SkipVerifyScript` | 不额外调用随包的 `verify-install.ps1` |
| `-ReportPath <路径>` | 报告文件位置（默认写 `<数据目录>\logs\`） |

### 每轮都查这 7 件事

1. 安装器退出码 = 0
2. 服务已注册 / 开机自启（`StartMode=Auto`）/ `Running`
3. **注册表 ImagePath 以引号开头且指向正确的 exe** ← 1639 与 unquoted-path 两道坑的判据
4. **本轮 `install-service.log` 不再出现 `1639`**、且出现 `ImagePath 校验通过`
5. 端口 8899 在监听（纯 socket 探测）
6. HTTP 真能打开
7. 卸载后：服务 / 程序目录 / 防火墙规则 / 托盘启动项都清掉，**数据目录保留**

### 怎么读结果

- 退出码 `0` = 全部通过；`1` = 有硬失败。
- 每题一行：`[OK]` 通过、`[!!]` 硬失败、`[--]` 软性提示。
- 失败时把**整份报告文件**发回即可（里面含 ImagePath、日志新增行、verify-install 输出）。

> ⚠️ **本驱动的验证边界**：`-Check` 模式与"非管理员拒绝执行"的守门已在开发机实测；
> **真正的安装/重装/卸载只能在目标机跑** —— 开发机的会话不是管理员，
> 且沙箱把 `sc.exe` 列入硬黑名单。

---

## 5. 卸载

- **图形**：开始菜单 → 卸载中控服务；或「设置 → 应用」。
- **静默**：

```powershell
"C:\Program Files\ControlServer\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

卸载会做：停服务 → 删服务 → 杀托盘助手 → 删 HKCU 启动项 → 删防火墙规则 → 删程序目录。

> **数据目录 `%ProgramData%\ControlServer` 默认保留**（宁可留，不可误删现场配置）。
> 图形卸载会问你要不要一起删；静默卸载一律保留。

---

## 6. 排错速查

| 现象 | 首查 |
|---|---|
| 安装报"服务注册或启动自检没有通过" | 看 `%ProgramData%\ControlServer\logs\install-service.log` 与 `install-diagnostics.log` |
| 看到 **1639** | 服务注册参数被拆烂 —— 确认用的是 `New-Service` 版本的脚本（第三轮修复） |
| 看到 **1072** | 服务停在"待删除"、句柄没释放 —— **重启电脑**再装 |
| 服务 `Running` 但浏览器打不开 | 端口被占：`netstat -ano \| findstr :8899`；或防火墙/代理 |
| 局域网其它机器连不上 | 防火墙规则没建成（装了 360/火绒常发生）：手工放行 `TCP 8899` |
| 托盘图标不见了 | 托盘是**用户会话**进程；服务本身不受影响，可手工跑 `ControlServer.TrayAgent.exe` |
| 页面能开但一点按钮就 403 | 授权闸门未放行（见「授权」页）；若手工开过强制改密闸门，也可能是账号被标记 |
| 一条现场证据都不够用 | 跑 `tools\collect-diagnostics.ps1`（只读、免管理员），产物在 `logs\diagnostics-<时间戳>.txt` |

---

## 7. 已知限制（诚实项）

- 完整「安装 → 重装 → 卸载」**必须在目标机实测**；开发机无管理员权限且 `sc.exe` 被沙箱禁用。
- 安装包用的是 **Inno Setup**（非 MSI）。原生 MSI 尚未提供。
- `assets\app.ico` 是**占位图标**（格式合规：16→256 共 7 档 32bpp），换正式图标只需覆盖该文件后重编。
