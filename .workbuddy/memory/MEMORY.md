# 项目长期记忆 — 中控 Web 服务版（ControlServer）

> 与 `ControlApp` 不联动。⚠️超 3000＝截断⇒只放结论，细节进 `REF-*` 与 `docs/`。

## 0. 索引

- 工程/构建→`REF-M0`＋`REF-M2`｜RBAC/授权/Linux→`REF-M3-*`｜**M4**→`REF-M4-Agent`｜**M5**→`REF-M5-远程桌面`
- 文档：`M6.3压测`｜`M6.4升级回滚`｜`运维手册`｜`M7.1对账`｜`M7.3Linux音量`｜`M7.4 60帧`｜`M7.9删隐私屏＋全屏`｜`M7.10合并清晰度`｜`M7.11文案更正`｜`M7.12端口8899`｜`M7.13远控窗口＋重连`｜`M7.14挂起＋结束会话`｜`M7.15文件分发页签`｜`M7.16帧率旋钮`｜`M7真机清单`
- 冒烟 `deploy/smoke/remote-e2e.py`（须 Edge）｜帧率 `deploy/smoke/remote-fps.py`｜回归 `deploy/verify/check-tests.py`
- ⚠️ 构建：`dotnet`＝`…/dotnet8/dotnet.exe`｜🔥前后端不许并行｜🔥`bin\Debug` 被锁⇒停服或 `-p:BaseOutputPath=.build/altbin/`｜「0 警告」加 `-m:1`

## 1. 技术路线

.NET 8｜Kestrel `0.0.0.0:8899`｜Win服务/systemd 同一 `Program.cs`｜SignalR｜SQLite+EF｜Vue3+TS→`Server/wwwroot`

## 2. 铁律

1–4. **分层**：`Core` 不引 ASP.NET/EF/平台 API；时间戳只用 `BeijingTime`；路径经 `IDataDirectory`；**未知一律 null**。
5. 🔥 新控制器须 `[Authorize]`/显式 `[AllowAnonymous]`；新 `/api` 须进 `LicenseGate.cs` 分类表（未归类＝拒）。
6–7. Agent 库不引 `Server`；客户端不开入站端口；服务端不扫子网；**作用到别的机器的写在客户端校验归属**。
8. 🔥 前端「刷新」不能让正文卸载重建：占位只用于「还没有内容」。
9. 🔥 **断言三问**：①判不为空先问空长什么样；②有说明还要问指向对不对；③轮询精确到要的值；**假 FAIL 比不断言更坏**。
10. 🔥 `void asyncFn()` 连异常一起吞⇒"点了没反应"；`v-model` 别指不存在的键。
11. 🔥 前端两条路：源码态读磁盘；`dist/`/冒烟/绿色版读**内嵌那份**⇒改前端**必须再 `dotnet build Server`**。**判据 `Directory.Exists(wwwroot)` ⇒ 优先磁盘**：拷进 `bin/Debug/net8.0/wwwroot` 只需重启。
12–13. 🔥 用例不绑固定端口；**「源码改了」≠「现场修好了」**⇒全仓搜 0 处先怀疑产物（本机安装目录也是产物）。
14–16. **M5 远控**：手在托盘（`Service` 只转发；Session0 无桌面）；「静止」＝抓屏没报新帧（`IsCursorIncluded:false`）；MF 码率/质量改不动⇒只能重建。
17. 🔴 **备份**：`control.db` 是 WAL⇒拷文件＝假备份⇒只能 `VACUUM INTO`；私钥不进备份；**恢复先停服**；日志不许手删。
18. 🔥 `.ps1` 须 UTF-8 BOM；控制台只用 **GBK 有字**符号。
19. 🔥 **「配置项存在」≠「生效」**：审计须 grep **消费点**；接线 fail-closed。
20. 🔥 口令迁移只能 rehash-on-login；换慢哈希要补时序对齐（PBKDF2 ms 差暴露用户是否存在）。
21. 🔥 **回滚＝恢复备份＋装回旧程序**；**"页面能打开"不是升级验收**⇒`--dbinfo` 前后逐行比。
22. 🔴 ACL 收紧后未提升的管理员也读不了（deny-only⇒`IsInRole` False）⇒维护命令要提权；`icacls /T` 遇已有子对象必失败。
23. 🔴 版本号别三处写死：真源只留 `Directory.Build.props`＋`SourceRevisionId`。
24. 🔴 **日志要能 grep**：JSON 默认 `\u` 转义⇒`findstr 授权` 零命中；只碰 `log-*.ndjson`。
25. 🔴 **删除判据用「年龄」**：上传是 `Move→落库` 两步⇒"不在库里就删"会删刚传的。
26. 🔥 **PS 起进程/停服/退出码**：`Start-Process` 遇双代理变量**必崩**⇒`ProcessStartInfo`；真退出码靠 `-File`＋管道；回滚成功≠升级成功⇒**退出码 3**。
27. 🔴 **`Ok(null)`⇒204**⇒`unwrap` 得 `undefined`⇒模板读 `.text` **整页空白**（像"不推流"）。可空 GET 收口 `null`；判空用 `!x`。
28. 🔴 **跨身份执行**：服务(root/systemd)操作用户侧资源必须「定位登录会话＋`runuser`/`sudo -n` 降权＋显式 `XDG_RUNTIME_DIR`」；直接跑＝退出码 0、桌面无变化。探测别放构造函数；回落判据用 `IsSupported`。
29. 🔴🔴 **"画面全黑 + 被控端一动就好"＝息屏**（已白查两轮）。DDA 息屏**不失败**，交付一整片黑 ⇒ "帧在流、码率正常、日志干净"。**判据：IDR 每像素字节数**（正常 0.03／黑 0.0003）。
30. 🔴 **`ES_CONTINUOUS` 是线程级**：必须由一个只为它活着的线程持有；`IsHeld` 判"线程还活着"。**Session 0 调用会假成功**⇒按 SessionId 拒绝。点亮(`-1`)对屏保无效。
31. 🔥 `bin\Debug` 被管理员进程锁死时：`-p:BaseOutputPath=.build\altbin\` ＋ `--no-build -p:BaseOutputPath=…` 跑测试。
32. 🔥 **异步 MFT 一拍晚** ⇒ `帧率=1/(2×(周期+抓屏+编码))`。量上限要量**持续出货率**，不是"编码调用耗时"。
33–34. 🔴 **帧头三件同坐标系**：`frameWidth/Height`＋`regionX/Y`＋`width/height`。整幅取哪个数由**编码能力**定：支持区域编码(1:1)⇒屏尺寸；MJPEG 等⇒**payload 尺寸**（长边 1920）。取错⇒缩在左上(4K 占 1/4)、其余全黑，且两端日志全正常。判据收在 `RemoteFrameExtent.Resolve`。⇒合并显示只能缩略图。
35. 🔴 **浏览器保留键 `preventDefault()` 无效**（Ctrl+W/R/F5 在事件到页面**之前**已执行）⇒远控里按 Ctrl+W 关的是**观看端自己的标签页**。唯一真拦法＝**全屏＋`navigator.keyboard.lock([...])`**（🔴不锁 Esc/F11/修饰键）；非全屏只能 `beforeunload` 问一句，**这层同时是判据**。
35′. 🔥 **解释性文案也是代码，会腐烂**：结论必须放在**能被断言**的纯函数里且**只在条件成立时出现**（`codecFallback.ts`）。无条件横幅必被当噪声。
36. 🔴 **"重连/恢复"要用的记忆，必须在每一条入口都写**："创建时记录"只覆盖自己创建那条；`接着看`/列表点进去/分享链接都不经过创建⇒`lastConnectParams` 为 null⇒死胡同。判据＝**从第二条路进去，再断一次**。
37. 🔴 窗口/面板显示"当前实体"不许 `?? 0` 兜底（会画出「机器 #0」存在感）。窗口要有自己的归属字段（`windowAgentId`），生命周期比会话长。
38. 🔥 **异步回报的能力要在"打开窗口"时补读一次**（屏清单那类）。判据＝"窗口刚开时下拉框是空"。
39. 🔥 **退避/合并规则没有界面证据** ⇒ 抽进 `web/src/reconnect.ts`（12 条）：`connecting`忽略／`waiting`合并／次数用完放弃／`failed`忽略。**热更改用当前表单，重连用 `lastConnectParams`（刻意相反）**。
40. 🔴 **帧率与档位是两个旋钮，不许相乘/取小**：`Min(请求, 档位)` 会让 20/30/60 全变 12。⇒ `FoldTargetFps=Clamp(请求 ?? 档位默认,1,60)`（**请求优先**）。建会话/重建/重连三条路**都要带 quality**。
41. 🔴 **"实现写好了但没接线"没有日志也没有告警**（`WindowsGpuFrameEncoder` 漏挂 `IRegionSurfaceEncoder`⇒M5.5 视口整块死码）。判据只能在**类型层面**；修完补编译期钉子 `ContractPin()`。
42. 🔴 **"心跳通"≠"长连接在"**：客户端把心跳 HTTP 成功当成"我还连着"⇒中心 `ConnectionIdOf=null` 时它**永不重连**。判定只认中心那一侧。
43. 🔴 **量帧率之前先证明"桌面真的在动"**：DXGI 增量投递，静止桌面测出的是**退避节奏**。工具 `.build/anim.py`＋`.build/shot.py`（隔秒比字节差 **>0**）。**Edge `--app` 与 `start file://` 建不出可见窗口**。
44. 🔥 **「收起来」与「真结束」必须各有名字，且危险动作占最右**：`✕` 改名「**挂起**」挪最左＋列表加「**结束会话**」。判据＝断言**顺序**与"中心会话数仍在"。
45. 🔴 **凭据里的"记住的地址"是缓存，不是答案**（2026-10-20）：有值就短路广播发现 ⇒ 服务端晚起/搬家后**永不回连**（现场 09:15–09:20 十几轮空敲旧网段，改地址一秒就好）。规则：人指定的（本机页/安装参数）**永不自动替换**（同网段可能有两套中心）；缓存连续失败 **2 轮必须广播**，发现不同就换址并重注册。判据收在 `ServerAddressResolver`。
46. 🔥 **退避等待必须能被打断**：改完地址要睡满 60 秒⇒用户以为"改了没用"（实测 48 秒）。`SemaphoreSlim(0,1)`＋`WaitAsync(timeout)`，醒来重读地址而不信信号里带的值。
47. 🔥 **连接失败要按性质分流**：对面连话都说不上时做"凭据探测"只白等（20 秒/轮 ⇒ 一轮 40 多秒）。401/403 直接重注册；"重置"仍要问（可能是认证层在拒）。另：`RegisterAsync` 曾把 HttpClient 超时 `throw` 上去⇒Error「主循环出错」。
48. 🔴 **监听地址 ≠ 访问地址**：`0.0.0.0` / `*` / `+` 只表示"监听所有网卡"，浏览器与 DNS 都不接受它作目标（`SslStream.AuthenticateAsClient("0.0.0.0")` 抛「未指定」），SAN 里也永远不会有它 ⇒ **装什么证书都救不了**。给运维看的地址必须是**本机可路由 IP / 主机名**。本项目现存的误导源（未修）：`ServerAddressRegistry.DescribeHost`（`IPAddress.Any`→`0.0.0.0`，写进启动日志）、`ServerAddressService.CurrentUrls` 回落、`TrustEndpoints.HttpsUrlsOf` 回落。

## 3. 进度/挂账

M1–M5.5✅→M6✅→M7.1✅→M7.3✅→**M7.5 僵尸会话＋4K 吞吐**✅→**M7.6 息屏黑屏**✅→**M7.4 60 帧**：`smooth` **57.1**（95%）。真因是三处与帧率无关的固定代价（令牌迟到一轮／定时器 15.625ms／"没抢到新画面"付两次钱）。low/high/smooth＝11.9/29.0/57.1。**"硬编上限 30fps"作废**（是 59÷2）；瓶颈已是"等桌面更新"（抓屏 11.2ms＞编码 4.9）⇒降分辨率收益≈0。
**基线**：Core **942**／Agent **429**／Integration **620**／前端 **226**（总计==基线才 `--update`）。
**实测锚**：JPEG q70 1920×1280 27ms；GDI 抓屏 47–58ms＝MJPEG 上限；DXGI 3.15ms＋H.264 106fps。
**已证**：升级/回滚 13/13；远控冒烟 41/41；GPU 假红已清（`RunFrames` 每轮重抓等四处口径错，连跑三轮 10/10）；容量 100台30min→CPU1.2%/151MB。
**M7.9**：隐私屏整体下线；网页全屏（`.rd-viewport`，状态只认 `document.fullscreenElement`）。**打包坑**：对已存在 exe 原地写被策略拒（`MSB4018`）⇒ `-OutputDir .build\agent-payload` ＋ `-SkipPayloadBuild`。
**M7.10**：合并显示糊＝MJPEG 长边钉死 1920。实测**缩放是瓶颈**（1920→3840 每帧只多 66ms）⇒ `ResolveMaxLongEdge`＝clamp(1920,3840)；🔴**预算须随像素放大**（否则降质量重编，越提越糊）；Bicubic 不可用。
**M7.11**：删常驻横幅＋修「画面不受影响」；文案抽 `codecFallback.ts`。🔴 普通权限起服务写不进 `ProgramData\ControlServer\logs`（ACL）。
**M7.12**：默认 HTTP **8080→8899**（真源 `Server/appsettings.json`）。**业务端口不动**（TargetPort 8080、UDP 8090）。坑：防火墙仍放 8080／托盘 `--port`／`server-overrides.json` 优先。
**M7.13**：远控页只剩机器列表，参数进窗口内悬浮面板＋断线自动重连＋热更改。三个 bug：接着看不记参数／标题「机器 #0」／开窗屏清单空。
**M7.14/15**：`✕`→「挂起」＋列表「结束会话」；页签「文件」→「**文件分发**」。
**M7.16**：帧率旋钮＋播放延迟；`targetFps` 请求优先；🔴内嵌产物仍旧版⇒**打包前必须先停服 `dotnet build Server`**（铁律 11）。**重启服务**：`dotnet bin/Debug/net8.0/ControlServer.Server.dll`，**cwd 必须 `src/ControlServer.Server`**。
**M4.3b AMD 温度**✅：硬件树**递归拍平**（老 AMD 温度在主板 SuperIO）＋分级 `Tctl/Tdie`→`CCDs Max`→`Average`→逐核→SuperIO；`ok` 但 CPU 缺失也写 `Note`。**0 ℃ 合法，勿丢**。⏳真机 AMD 未验。
**M4.3c 晚启动回连**✅（10-20）：见**铁律 45–47**；复现脚本 `.build/repro-late-server.py`（假凭据＋假中心 UDP，6/6，实测换址 21 秒）。⏳**客户端产物未重发**（改了 Agent 库必须重发 `dist/agent` 才到现场）。
**M5.3 信任**✅（10-20）：`https://0.0.0.0:8443` 报不安全＝两因叠加：①**0.0.0.0 是监听地址、永远不是访问地址**（`SslStream` 直接抛「未指定」，SAN 里也不可能有）；②根 CA 没进信任存储。装证书后 localhost／127.0.0.1／机器名／本机 IP **四个全通过**。脚本 `deploy/windows/trust-ca.ps1`（自提权写 LM\Root、`-Uninstall`、**装完做真实握手验证**）。🔴 **Windows 不许静默写 Root 存储**（非交互 `Import-Certificate`＝「此操作中不允许使用 UI」）⇒必须 UAC 一次。🔴 **「存储里有这张证书」是假验证**（装错 store／指纹不符／Firefox 用自己存储都会显示已装但照旧报红）。
**待真机**：Inno 安装包／Linux·Docker／Session0 GPU／ACL／100台／4K60·8K／**多屏拼接**；**Linux Agent 部署物还没有**。
**授权**：ECDSA P-256；🔒私钥不进 `Core`
