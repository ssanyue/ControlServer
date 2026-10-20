# REF-M0 · 工程约定与常用命令（改代码 / 跑命令前必读）

> 本文件收纳**常用命令、测试约定、前端构建与冒烟**的完整版。
> `MEMORY.md` 只留铁律级结论 + 本文件的指针（MEMORY 曾因过大被注入截断，故细节外移）。
> 环境类坑位另见 `REF-M2-模块细节.md` **§34**（权威详版）。

## 1. 常用命令（完整）

```bash
export DOTNET_ROOT="/c/Users/IYLONG/.workbuddy/binaries/dotnet8"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build ControlServer.sln -c Debug
dotnet test  ControlServer.sln -c Debug          # 集成测试较慢（~2.5 min），推荐后台跑
dotnet run --project src/ControlServer.Server    # 控制台形态，默认 :8080
run.cmd                                          # 双击一键启动；run.cmd 9000 换端口
                                                 # 已用 start /D 把工作目录钉在 src\ControlServer.Server
                                                 # ⇒ ContentRoot 命中磁盘 wwwroot，改前端**不再需要重编后端**（§31）

# 冒烟总入口：每个脚本自动开新临时目录 + 新端口起 exe、跑完收摊（退出码可直接进 CI）
python deploy/smoke/run-all.py                 # 默认打 dist/win-x64 的打包 exe
python deploy/smoke/run-all.py --only rbac     # web / pairing / admin / license / rbac
python deploy/smoke/run-all.py <exe路径>       # 也可指向 bin/Debug 的开发产物
# ⚠️ 五个场景对初始状态假设互斥，**别塞进同一个实例跑**
# ⚠️ rbac 保留强制改密闸门（默认开）并把 admin 密码改掉；其余场景由 run-all 显式传
#    --Server:Auth:EnforcePasswordChange=false —— 否则出厂 admin 未改密时全线 403（与被测内容无关的噪音）
# ⚠️ license 场景额外传 --Server:License:Enforcement=Enforce --Server:License:AutoTrial=false
#    （默认 Warn + 首启自动试用 ⇒ 不抬不关的话"一上来就是可用"，闸门根本没机会被观察到）

cd web && npm run typecheck && npm run build              # vue-tsc + 产物 → Server/wwwroot
dotnet build ControlServer.sln -c Debug                    # ⚠️ 改前端后必须补这一步（§17）
# 绿色版单文件：**只走官方脚本**。手敲 dotnet publish 会漏 native/DebugType/压缩参数
#   ⇒ pdb + 原生 dll 散落、单文件模式被破坏、exe 105MB（脚本产物 ~49MB / 目录仅 1 文件，详见 §10）
#   脚本现在产出两套：dist\win-x64（绿色版·严格 1 文件）+ dist\win-x64-service（服务版·2 文件）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-portable-exe.ps1 [-SkipWebBuild] [-SkipServiceOutput]
# 签发工具（厂商侧，**绝不可随产品交付**）：dist\win-x64-license-tool\ControlServer.LicenseTool.exe
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-license-tool.ps1 [-NoCompression]
# Windows 安装包 → dist\installer\ControlServer-Setup-<版本>.exe（§33）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/build-installer.ps1 [-SkipWebBuild] [-SkipServiceOutput]
# 目标机自检 / 取证（只读、免管理员）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/verify-install.ps1
# 服务版安装 / 卸载（**管理员**；含 sc sdset 授权 + HKCU 托盘助手启动项，详见 §25）
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/install-service.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/windows/uninstall-service.ps1
dotnet publish src/ControlServer.Server -c Release -r linux-x64 --self-contained true   # Linux 交付（未做）
```

- `PublishTrimmed` 保持 **false**（SignalR / EF Core 反射多，裁剪易踩坑）。
- **Bash 陷阱**：`A=1 && B=2 && exe … &` 会把**整条链**放进后台子 shell，父 shell 里 `$A`/`$B` 为空。
  变量用 `;` 或换行单独赋值，**只把启动命令放后台**。
- 真服务冒烟（跑构建产物）**必须先 `export DOTNET_ROOT`**（否则 apphost 回退 Program Files 报 "You must install or update .NET"）；
  改端口用 `Server__Http__Urls=http://0.0.0.0:18099`（`Server__Http__Port` **无效**）。
- **配置优先级（实测）**：`命令行 > 环境变量 > 端口持久化文件 > exe 同目录 appsettings > cwd appsettings`。

## 2. 测试约定（完整）

- 集成测试用 `WebApplicationFactory<Program>`，**必须**在构造阶段用**环境变量**注入配置
  （`Server__Storage__DataDir`、`Server__Listener__PairingPort`）—— `Program` 在 `CreateBuilder` 后立刻读配置，
  `ConfigureAppConfiguration` 太晚。
- 每个测试独占一个 `ControlServerFactory`（唯一临时数据目录 + 唯一空闲 UDP 端口）；
  程序集级 `DisableTestParallelization = true`；换服务用 `ConfigureTestServices`（`ReplaceServices`）。
- `HostLifecycleTests` 是回归保护：反复"启动 → 停机 → 重启"，守端口重绑与 DB 复用。
- **⚠️⚠️ FluentAssertions `Equal` params 陷阱（已踩 4 次）**：`Should().Equal("a", "说明")` 会把 `"说明"` 当成
  **第 2 个期望元素**，报错是**「contains N item(s) less」**——看起来像业务逻辑少了一条数据，极难定位。
  原因：两参优先绑 `Equal(params T[])`。**规则：带说明就写 `Equal(new[] { "a" }, "说明")`**。
  同陷阱适用于 `StartWith` 等 params 重载。
- **辅助工厂别用"传空就补默认值"**：`xs.Length > 0 ? xs : new[]{"默认"}` 让"显式传空"与"没传"无法区分。改用**重载**。
- **测试宿主没有真实 Kestrel socket**（内存 `TestServer`）⇒ 换端口、真 UDP 只能靠**真机 / Core 单测**覆盖。
- **集成测试里 SQLite 表名是 snake_case**（`audit_logs`）、列名是属性名原样 —— 裸 SQL 写 `AuditLogs` 会 `no such table`。
- **授权相关**：测试宿主默认 `AutoTrial=false` + `NtpTimeoutMs=100`（避免真联网）；
  `LicenseTestFactory` 用 `ConfigureTestServices` 替换验签公钥（厂商私钥不在测试环境）。详见 `REF-M3-授权.md`。

## 3. 前端构建与 UI 验证

- **前端产物是编译期嵌进程序集的** ⇒ 改前端后**必须** `npm run build` **再** `dotnet build`（详见 REF §17）。
- **`npm run typecheck` = `vue-tsc --noEmit`**（已装进 devDependencies）：`tsc --noEmit` 看不了 `.vue`，
  等于 SFC 里的类型错与 `emit` 签名错**完全没检查**（2026-10-08 开启后立刻抓到 4 处**既有**缺陷：
  `emit('warn', …)` 事件名写成提示级别 ⇒ 提示静默不显示）。装它用 `npm install --ignore-scripts vue-tsc`。
- **⚠️ 前端 UI 验证（无头 Edge + CDP）的铁律**：**点击辅助函数必须返回布尔并单独断言**。实测
  `exact('button','保存')` 会**静默空点**（EditPanel 里的真实文案是「保存配置」），空点无异常无提示，
  结果表现为"选好的值没落库"——**看起来像功能坏了，其实只是没点到**。同类坑：按下标取 `select` 会取错控件
  （动作卡片顺序是 协议/数据类型/对接码比较方式）；`.tile-row` 的 textContent 是拼接串，只能 `includes`。
  ⚠️ 另：全新数据目录的 admin 会被**前端改密弹窗**挡住整个界面，而
  `Server:Auth:EnforcePasswordChange=false` **只关服务端闸门、不关弹窗**。详见 REF §27.5。
- 相关脚本：`.tools/s5-ui-check/`（授权前端 30 断言）、REF §26（并发执行 / 无头浏览器）与 §27（CDP 五坑）。

## 4. 脚本与编码约定

- **PowerShell 禁止直接跑 `.ps1`**：需 `powershell -NoProfile -ExecutionPolicy Bypass -File xxx.ps1`。
- **`.ps1` 必须 UTF-8 with BOM**（PS 5.1 无 BOM 按 ANSI 解析 → 中文注释 `ParserError`）；
  **`.cmd` 反之必须存 GBK**（宿主 CP936）。用 Python `open(p,"w",encoding="gbk",newline="\r\n")` 生成 `.cmd`。
- **`deploy\**\*.ps1` 里函数必须写在调用点之前** —— PowerShell **不支持前向引用**，而
  `$ErrorActionPreference='Continue'` 会把 `CommandNotFoundException` 当非终止错误**静默吞掉**
  ⇒ 脚本"成功退出"却少做了事（**最危险的失败形态**）。
- **不用 `$ErrorActionPreference='Stop'`**（npm/vite 警告走 stderr 被当致命错误），改判 `$LASTEXITCODE` + 显式 `exit 0/1`。
- ⚠️ **不要把 stderr 折进管道后再看退出码**：`... -File xxx.ps1 2>&1 | Select-Object -Last 40` 在 PS 5.1 下会把
  stderr 行包成 `ErrorRecord`，**脚本明明成功却报退出码 1**。判断成败看输出里的 `✓ …`，或用 `*> $null` 收掉输出（REF §22.3）。
- 用户可见输出**禁用 `✓`/`✗`/`⚠`**（不在 GBK，控制台显示成 `?`；Python 脚本里更会 `UnicodeEncodeError`）。
- 🔥 **删除拦截会"先删掉、再报失败"**（2026-10-14 实测，**不是仓库里的代码**，是本机外部拦截）：
  对 `Remove-Item -Recurse -Force` 报 `[safe-delete][SAFE_DELETE_FAIL_CLOSED]`（走 genie-trash，"Some operations were aborted"）——
  症状是**目录真的消失了，脚本却收到失败** ⇒ 构建中止在**"准备输出目录"**这一步。
  `build-portable-exe.ps1` / `build-agent.ps1` / `build-agent-installer.ps1` / `build-installer.ps1` 四个全中。
  绕法：**先让目标目录不存在**（脚本内部 `if (Test-Path) { Remove-Item }` 整段跳过），即"缺什么补什么"重跑：
  单独跑 `build-portable-exe.ps1` 补 `dist\win-x64`＋`dist\win-x64-service`；`dist\win-x64-service` 已存在时
  `build-installer.ps1` 会走"payload 已存在"分支（不去删它）。
  ⚠️ **绝不在 `dist\agent` 已存在时重跑 `build-agent.ps1`**：它会先删掉再中止，**产物直接消失**。
  ⚠️ 该拦截**不受沙箱开关影响**（`dangerouslyDisableSandbox` 下照旧触发）。

### 4.1 起进程 / 取输出 / 判停服 / 退出码（10-18 实测，`deploy/upgrade/AppControl.ps1`）

「脚本逻辑」这一类缺陷的共同点：**只在真跑时才现形**，而且专挑最要紧的那一步。

- 🔴 **`Start-Process` 在本机必崩**：`ArgumentException: 已添加项。字典中的关键字:“http_proxy”所添加的关键字:“HTTP_PROXY”`。
  PS 5.1 为子进程建**大小写敏感**的环境字典，机器上同时有 `http_proxy` 与 `HTTP_PROXY` 就抛。
  原脚本用它做①装包②起服 ⇒ 恰好崩在"装包"和"起服务"两步。改用 `System.Diagnostics.ProcessStartInfo` ＋ `Process.Start`。
- 🔴 **`ProcessStartInfo.EnvironmentVariables` 在 PS 5.1 / .NET Framework 4.8 下是 `null` 且只读**（探针实测）。
  所以子进程环境块只能靠"临时改父进程 `$env:` → `Process.Start`（`EnvironmentVariables` 为 null 时子进程继承）→ 立刻还原"
  （`Push-ProcessEnvironment` / `Pop-ProcessEnvironment`）。副作用：顺带绕开了上面那个重复键字典。
- 🔴 **`& $exe @args 2>&1` 静默返回空**：同一 exe 在 bash 里跑得好好的，在 PS 里既没输出也没 `$LASTEXITCODE`
  ⇒ `upgrade.ps1` 第 1 步「升级前基线可读（--dbinfo）」判失败。所有 `--dbinfo/--backup/--restore` 一律走 `Invoke-Process`。
- 🔴 **包装层传不出退出码**：`-Command "& script.ps1 *> log"` **恒为 1**（探针 `exit 7` → 1）；`& script.ps1` 在外层留下空
  `$LASTEXITCODE`。只有 **`-File` ＋ 直接读 `Process.ExitCode`** 能传真实退出码。
- 🔴 **孙进程继承管道写端 ⇒ EOF 永不到**：子脚本起了服务实例后退出，匿名管道句柄是可继承的（`CreateProcess` 带重定向即
  `bInheritHandles=TRUE`），`ReadToEndAsync().Result` 永远不完成。症状：**演练卡十分钟，而那个场景其实 12 秒就成功了**。
  解法：子进程一退出就**立刻停掉沙盒实例**让管道关闭；`Invoke-Process` 内再加一层有界 `Wait(8000)`。
- 🔴 **"服务停了" ≠ "库释放了"**：杀进程＋端口已空之后 `--restore` 仍报「数据库正被占用」⇒ 自动回滚在最要紧的时刻失败。
  停服的最后一步必须是 `Test-DatabaseReleased`（**独占打开** `control.db`，15 秒重试）。
- 🔴 **回滚成功会让 `upgrade.ps1` 退出 0**（`rollback.ps1` 成功也是 0，脚本 `exit $LASTEXITCODE` 原样透传）⇒
  "升级失败、已回退"在 CI/RMM 眼里是**成功**。退出码契约：**0 成功 / 1 中止（未动备份）/ 2 需人介入 / 3 升级失败但已干净回滚**。
- 🔴 **`--backup` 副本的字节哈希不是"数据回来了"的尺子**：`--restore` 是逐字节铺回去，而基线侧是新的 `VACUUM INTO` 重写 ——
  同逻辑内容、不同页布局 ⇒ SHA256 必然不同（实测 7/7 `--dbinfo` 项相等、哈希不等）。判据用 **`--dbinfo` 全量键对比**。
- **PID 文件按端口命名**，不是全局：一台机器上两个沙盒否则会互相停掉。
- **上一轮的残留实例会让后续判断以最迷惑的方式失败**：手工跑剩的 `ControlServer.Server.exe` 占着沙盒库 ⇒ 守卫报"停了但库还锁着"。
  演练因此**先要求能删掉旧沙盒**，删不掉就 `exit 2` 并直接给出处置命令，别往下跑。
- 演练脚本：`deploy/upgrade/rehearse-scripts.ps1`（进程模式、免提权，13 项判据，52.6 s）。与 `.py` 那份分工：
  `.py` 验数据链路，`.ps1` 验脚本逻辑（分支/退出码/回滚）。
