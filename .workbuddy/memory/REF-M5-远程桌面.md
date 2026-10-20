# REF-M5 — 远程桌面（M5）详版

> **从 `MEMORY.md` 迁入（2026-10-16）**，原因：`MEMORY.md` 长到 15 K 字符，注入时被截断，
> 于是排在这些"不许忘"**后面**的条目根本没机会被读到。迁到详版之后它们始终可查，
> 而 `MEMORY.md` 只留索引与进度。
>
> 相关方案：`docs/M5-局域网远程桌面实时方案.md`（v3.5）｜`docs/M5.0-实施清单.md`｜
> `docs/M5.4-实施方案.md`｜`docs/M5.4b-实施方案.md`

## 1. M5.4-a：手写 COM 互操作 + 共享 D3D11 设备 + DXGI 抓屏

- **M5 远程桌面** → `docs/M5-…方案.md`（**v3.5**）＋ `docs/M5.0-实施清单.md`（**六批全交付：M5.1 只读观看 ✅、M5.2 输入回传 ✅、M5.2 归属校验 ✅、M5.3 声音回传 ✅**）＋ `docs/M5.4-实施方案.md`｜**超 4K 只能 HEVC**（H.264 封顶 4096）｜**8K**：8192＝**分辨率**上限≠**吞吐**上限，**8K60 需双 NVENC+SFE（仅 Ada 中高端）**，单引擎 ≈30fps；**Chromium 无 HEVC 软解⇒观看端无硬解＝不可用**｜**「尽力做」**（验收＝拿到硬件上限 ≥90%）｜**黑白模式**＝编码前置色度 128＋标准 4:2:0：**彩色省 10–30%、白底黑字≈0** ⇒ 真价值＝**消 4:2:0 色边**
- 🔥 **M5.4-a 不许忘（10-16 落地·手写 COM 互操作 + 共享 D3D11 设备 + DXGI 抓屏）**：方案 `docs/M5.4-实施方案.md`（四个已拍板：垂直切片／手写 COM／2160×1440 本机实测／WebCodecs+MJPEG 兜底）｜🔴 **`AccumulatedFrames==0` 的帧不能用**＝「刚连上看见一片黑」的真身（全新 duplication 首帧恒此状态，实测读回**全 0**；第 2 帧起 99.98% 非黑）⇒ 不变式**交付帧必须 `AccumulatedFrames>0`**｜🔴 **槽位号是「接口内」的，跨接口不通用**（从 `ID3D11Device` 取第 10 槽＝`CreateInputLayout`，当纹理 `GetDesc` 调返回 `E_INVALIDARG`）⇒ `ComVtable.Bind` 必须传**那个接口的实例**｜`ID3D11DeviceContext` **115 槽**：Map=14/Unmap=15/CopyResource=47/Flush=111（用 Begin=27/End=28 交叉验证）｜`AcquireNextFrame` 第三参是 **`IDXGIResource**`**，要 QI 成纹理，**两份引用都要放**｜**`TotalMetadataBufferSize` 不含指针形状**（实测首帧 meta=0B 而 pointerShape=4096B 并存）⇒ 判"有没有脏区"只能看前者｜**`GetFrameDirtyRects` 不许用「先传 0 字节问大小」的两段式**（实测那样问回来是 0，明明有 16B）⇒ 一次给足缓冲再看写入多少｜**适配器必须按 output 反查**：本机 4 个节点里**只有 #0 Intel 挂 `\\.\DISPLAY1`**（#1 MX250／#2 Intel 第二节点／#3 Basic Render Driver 全无输出）⇒ 按序号挑 3/4 概率挑错｜**DDA 帧里没有鼠标指针**（`IsCursorIncluded` 恒 false 是如实上报）**且移动鼠标不产生新帧** ⇒ M5.4-b 必须自己用 `GetFramePointerShape`+`PointerPosition` 合成｜`virtual-desktop` 多屏拼接**带原因拒绝**（DDA 一次只能抓一个 output，不许安静回落主屏）｜**两个后端共用一份屏枚举**（`WindowsScreenEnumerator`，别各抄一遍）
- 🔸 **M5.4 验收**：`DxgiCaptureSmokeTests` 6/6（含**与 GDI 对拍**、**读回对照组**＝绕开抓屏只验 `CopyResource`+`Map`+`GetDesc` 原样搬运）；本机 2160×1440 抓帧+读回≈15ms；证据图 `.tools/m5.4-capture/`
- 🔥 **M5.4-a 色彩转换不许忘（10-16 落地·`D3D11Nv12Converter`，方案 §3.4／§5.3，用例 2/2）**：🔴🔴 **驱动「能力声明」与「读回状态」都不等于「行为」**——饱和度滤镜 `FilterCaps` 说支持、量程报得出 `0..1000/默认100`、`GetStreamFilter` 读回**正是我们设的值**，而输出与"完全关掉"**逐字节相同**（8 变体矩阵全试过）⇒ **RGB 输入通路上根本不接滤镜块**，**没有任何 API 能发现它，只能读回像素证伪** ⇒ 灰度＝**两趟 Blt**（第二趟在 YUV 域压饱和度）＋**灰度必须自证**（首次真要灰度时读回验；≥3 次未判定就如实写"未经证实"）｜🔴 **`112` 是"色度偏离中点的最大幅度"不是缩放系数**：`Cb=128+224×(B′−Y′)/1.8556`（**归一化 0–1**）；用错 ⇒ MAE 39→**0.06**，而**症状与"色度平面错位"一模一样** ⇒ 用例必须**把最偏样本连原始 RGB 打出来**（同向偏且正比 `|B−Y|` ⇒ 量纲；乱跳 ⇒ 错位）｜🔴 **vtable 槽位错一个＝进程级崩溃**：`SetStreamAutoProcessingMode=37`（写成 31＝`SetStreamDestRect`，第 4 参 `const RECT*` ⇒ 把 0/1 当指针传 ⇒ `AccessViolation` 干掉宿主）｜**Set/Get 成对交错**：27–39 Set／40–52 Get／`Blt=53`；`GetVideoProcessorFilterRange=12`（靠"唯一读不到量程的那项＝`FilterCaps` 唯一不支持的那位"反证）｜🔴 **量程表的 `Default` ≠ "什么都不设"**：**边缘增强默认 44/64** 且**作用在亮度**上 ⇒ 连同降噪**必须显式关掉**｜`RGB_Range` 位 **0=Full**（`0b011` 其实是有限范围；写反＝不报错、只整体发闷）｜**NV12 在 D3D11 里是一张纹理**：色度起点 `Data+RowPitch×Height`、逐行用 `RowPitch`、宽高必须偶数｜输出视图必须 `BIND_RENDER_TARGET`；`CheckVideoProcessorFormat` 的 INPUT/OUTPUT **分开判**
- 🔸 **M5.4-a 色彩转换实测**（2160×1440／UHD 620／**纹理直连**）：有限范围 MAE **0.06** vs 全范围 18.77；色度 MAE **Cb 0.06／Cr 0.04**（43,730 块）；灰度 UV `128.0（128..128）σ=0`、偏离 ±1 **0/1,555,200**；灰度亮度差 **平坦 0.10**／边缘 1.98／彩色 3.37（⇒驱动用 **BT.601 亮度**去饱和，**不是**重采样）｜⚠️ **「灰度省多少带宽」仍无数字**：要等固定质量模式（`RateControlMode_Quality=3`）对比**输出字节数**；文档 §16.1 ⑥「彩色省 10–30%、白底黑字≈0」**尚未验证，不许当结论引用**

## 2. M5 动工铁律 + M5.1（观看）

- 🔥 **M5 动工铁律（10-15 实测得出）**：被控端 Agent 在 **`agents/`**（`src/ControlServer.TrayAgent` 是**中心自己的**托盘）｜双进程 `Agent.Service`(Session0)＋`Agent.Tray`(用户会话)｜**抓屏/编码/注入只能在托盘**（Session0 无桌面）；**原始帧不过管道**（4K60=1.99GB/s）⇒ 只有**码流**能回传；`SessionPipe*`＝**一请求一连接** ⇒ **不适合视频流** ⇒ 已另开 `RemoteStreamProtocol`（§1.3 **已拍板走 B**）｜**能力探测必须在客户机跑**（中心探的是自己那块卡）｜「有没有硬编」用 **MFT 枚举**判（可靠）；**PDH `engtype_VideoEncode` 按进程活动建实例⇒空闲测不出**（只能当推流时下界）⇒ **引擎数留 M5.4 推流实测**｜引擎数**不许按芯片代号查表**（AD104 猜错一半）｜**DXGI 节点数≠显卡块数**（本机 Intel 核显两个不同 LUID）⇒ 报告按型号归并
- 🔥 **M5.1 不许忘（10-15 落地）**：**管道方向＝托盘建·服务连**（默认 DACL＝SYSTEM+管理员+创建者；反了普通用户连不上，症状像"权限没配"其实是**方向错**）｜**建会话＝先登记账本再下发指令**（反了⇒被控端快过登记⇒第一帧判"会话不存在"⇒**画面永不出来、两边日志都正常**）；**停会话＝先下发再关账本**（与建相反）｜🔥 **两个帧计数必须并排**（中心收／被控端发）：只给一个时"**链路掉帧**"与"**被控端编不动**"长得一模一样而处置相反｜`HubClientMethods` 加了常量**必须同时实现服务端方法**（编译能过、真跑才炸）｜**中心不下发停止**（账本不碰网络）⇒ 被控端必须自己收场：`StartupFrameBudget=30s`（判据＝**从没出过帧**，不是连续失败次数）｜`Sweep` 若不判 `requested` ⇒ 一次失败变**永久**故障（该机再也建不了会话）｜**MJPEG 只在 1080p 级**（实测 2160×1440⇒189KB JPEG⇒base64 253KB／上限 512KB；4K 会**超限被整条丢弃**⇒画面停住，非"糊一点"）｜**虚拟桌面只在多屏时枚举**（单屏请求 `virtual-desktop` 抛"找不到屏"，刻意）

## 3. M5.2（输入 / 归属校验）与 M5.3（声音）

- 🔥 **M5.2 不许忘（10-15 落地）**：**两道独立闸门**（中心会话级 `InputEnabled` **默认关** ＋ 被控机本机否决 `remote-input.deny`）——**缺一个都点不动，且两件必须分开显示**（一个去点开关、一个去删文件）｜**本机否决必须是本机文件、不走中心下发**（否则能改中心的人就能翻回来 ⇒ 只是"建议"）｜**第二条反向管道**（不复用码流那条：同流双向写会读到**另一方向的半句话**）；**方向仍是托盘建·服务连**｜**归一化 0–65535、分母＝尺寸−1**（用"尺寸"当分母⇒最右/最下一列永远点不到：**8K 下是 30+ 像素的边**）｜**`MOUSEEVENTF_ABSOLUTE` 必须配 `VIRTUALDESK`**（单屏机两者**完全一致**⇒开发机测不出）｜**`INPUT` 结构体必须 `LayoutKind.Explicit` 联合体**（否则 `cbSize` 校验失败、**静默返回 0**）｜**`SendInput` 返回值必须看**（0＝被 UAC 安全桌面/完整性级别拦住，不看就把"被系统拦住"当"点下去了"）｜**移动可合并、其余三类一条不丢**（丢一条"抬起"＝那个键在被控机**永远按着**；同理前端要跟踪修饰键＋`blur`/切会话/关闸门时**强制松开**）｜**坐标换算必须在 `contain` 语义下算**（`getBoundingClientRect` + 手动去 letterbox；用元素框会在 `fit` 下整体偏一个缩放比，而 1:1 模式看不出）｜**`SendInput` 走 `Invoke` 要回执**（输入是唯一"发了没反应就等于没发"的动作）｜**判端点别看状态码**：404 既可能是"会话不存在"也可能是 **LicenseGate fail-closed** ⇒ 必须比**响应体里的码**
- 🔥 **M5.3 不许忘（10-16 落地）**：🔴 **硬前提**＝`AudioDecoder`/`AudioWorklet`（**及 M5.4 的 `VideoDecoder`**）**只在安全上下文存在**，`http://192.168.x.x` 下是 **`undefined` 不是报错**（整块功能静默消失）⇒ 中心自带 https：**本地根 CA＋并存监听器**；根证书走**明文 http、匿名、且在 `/api` 之外**（从 https 取会先撞"不安全"警告 ⇒ 大家习惯性点继续；放 `/api` 下则"没授权就拿不到证书"死结）｜**私钥目录＝安全边界，不许进备份/分发/日志导出**｜**音频刻意没有中心侧闸门**（有声音是默认行为；与 M5.2 两道闸门**故意不对称**）｜**Agent→中心必须另开第二条管道**（同流双向写会读到**另一方向的半句话**）；中心→浏览器虽是同一连接但**必须两条独立消息**（视频忙就丢、音频忙就等，**策略相反**）；转发队列**必须分开**（音频 1 包 / 视频 50 帧；合并⇒**一有声音画面掉到 1 fps**）｜🔥 **音画对齐要"测"不要"算"**：视频与音频**共用同一个 SessionClock 实例**（各持一个＝"两套估计碰巧差不多"）；锚点取**历史最小值、不平滑**；画面**从"收到就画"改成按目标延迟排期**（否则"显示的是哪一刻"没有定义）；两流**同一个目标延迟 60ms**；读数**恒为 0 ⇒ 测量写错了**，不是做得好｜**三态不许压成 bool**（`connecting`/`ready`/`unavailable`）｜**"没有声音"分两句说**（本机放不出声＝换 https/浏览器；被控端没音频＝去那台机器查声卡）｜**静音只切本机**（不做中心下发的音量）｜🔥 **一条规矩被抄 5 遍 ⇒ 抽成 `RemoteChannelStates.ReasonToKeep`**（"原因只在 `unavailable` 时有值"；旧写法会让页面出现"通道就绪：本机已禁止远程输入"这种**看起来很合理**的假原因）｜**`audioGeneration` 世代号**：`await addModule` 期间用户可能切走 ⇒ 不判世代号会把**上一路的播放器**重新挂上（而声音真的在响、看不出异常）｜**`clock.observe` 必须在判静音之前**（静音＝这个浏览器不出声，不是数据不存在；否则锚点被饿死）｜**AudioWorklet 必须是 `public/` 下的纯 JS**（由浏览器自己的加载器加载、**不过打包器**，写 TS/写 import 会在 worklet 里直接抛错）
- 🔥 **M5.2 归属校验（10-15 第五批）**：**两个输入端点都过 `RemoteWatchScope.IsWatching`**——"你正在看这一路"是**唯一**资格条件，判据里**不许有"别人"**：多人同看**谁都合格**（**刻意**，没有独占控制权；判据两入参已被反射钉住）｜**不做观看人数统计**（要一张"谁在看哪一路"的表⇒有表就有漏水路径；**漂高⇒"还有人在看"⇒没人敢操作**，比没有更坏）⇒ 只靠页面一句"会话级开关，多人同看先约定谁上手"｜**`notWatching` ≠ `SessionNotFound`**（重新打开这一路 / 刷新列表；曾一个码说两件事，前端那两句文案也已拆开）｜🔥 **`Join` 强制"一条连接同时只看一路"**（进来先退上一路：不退是**双向**代价——一直收旧路帧＋还能往旧路送输入）；**`Leave` 只在匹配时摘登记**（无条件摘会误摘**新**登记⇒"刚切过去点第一下被拒"）｜**`SetInputEnabled` 返回回执**（`RemoteSessionDto?` 的 `null` 说不清两种拒绝）；⚠️ 前端 `invoke<T>` 的 T 忘了改 **TS 不报错**⇒每次翻开关都弹"被拒"（`ack.ok` 读成 undefined）

## 4. M5.4-b：立项、开工测量、尺寸闸门

- 🔥 **M5.4-b 开工（10-16 立项，未写生产代码）**：方案＝`docs/M5.4b-实施方案.md`｜四决策：**端到端最小闭环**（GPU 管线接入→description 穿协议→中心透传→前端 WebCodecs，MJPEG 兜底）＋**指针合成**｜**编码格式＝中心按「浏览器能力 ∩ 被控端能力」取交集下发**（唯一保得住"浏览器解不了就别编"）｜验收＝本机 2160×1440 端到端实测
  - 🔥 **「M5.4-a 已完成」＝三层各自证明可用，不是"链路已换"**：抓屏/转换/硬编**全只活在集成测试里**，托盘装配的仍是 `quality => new GdiJpegFrameEncoder(quality)`（GDI+软 JPEG）。⚠️ 且 M5.4-a §4 那条"下一批第一件事"**三处过时**：`IsKeyframe` **早已存在**；`TryReadSequenceHeader()` **早已写好**（只被测试调过，缺的是接线）；"不碰 Contracts"只属于 a 批
  - 🔴🔴 **硬阻塞：IDR 撞穿管道上限**。实测 2160×1440 的 **IDR＝306.9 KB**（且是**静态内容的下界**）；上限＝中心 `MaximumReceiveMessageSize 512 KB`÷1.333＝**裸码流 384 KB** ⇒ **余量只剩 20%**；4K 是 2.67× 像素 ⇒ IDR ≈800 KB **必然超限**。🔥 **失败方式更糟**：`WriteFrameAsync` 超限**抛异常**，而 `RemoteStreamHost` 把**任何**写异常都当"接收端已断开"⇒ **整条流结束**，现场看的是 **"码流写不出去，接收端已断开"**（真因却是一帧太大）⇒ 必须改"可区分失败"｜⚠️ **若选分片**：中心 `RemoteSessionRegistry` 转队列是**容量 1+DropOldest**（`:680`，"一条消息＝一帧"成立、**分片不成立**）⇒ 3 片互挤只送最后一片＝**画面不动而日志全正常**
  - 🔥 **开工测量三数字**（`.tools/m5.4b-measure.py`，只读解析落盘码流，**换机型要重跑**）：**无 B 帧**（29 P + 1 I，零 B slice ⇒ 解码顺序＝显示顺序，前端**不必重排**；但这是**靠默认值**，`AVEncMPVDefaultBPictureCount` 仍未显式设 0）｜**`GopSize` 默认 60** ⇒ 12 fps 下 **5 秒**、30 fps **2 秒**才等到下一关键帧（与 §1.6 手段 11「放大 GOP」**方向相反**）｜SPS `profile 0x64/level 0x32`⇒`avc1.640032`（**必须从真实 SPS 反推**，手写错＝`NotSupportedError`＝假结论"浏览器不支持 H.264"）
  - 🔥 **H.264 的帧不能像 MJPEG 那样自由丢**（本批最易写错）：前端抖动缓冲"队列满从最旧的丢"对 MJPEG 成立、对 **P 帧不成立**（丢一个 ⇒ 之后全花且**解码器不报错**）⇒ WebCodecs 路径**只能整段放弃到下一个关键帧**（`reset`→等 keyframe→`configure`→再喂）｜同理被控端"忙就丢帧"要改成**丢到下一关键帧**，否则两边计数都正常而画面花
  - **指针要两件事一起做**（缺一个都表现为"鼠标不动"）：① DDA 桌面图**从来不含指针**（`IsCursorIncluded` 恒 false）⇒ 要在 **GPU 上**画进 NV12；② **纯鼠标移动不产生新帧**（`TryCaptureSurface` 只看 `AccumulatedFrames`）⇒ 要加 `LastMouseUpdateTime` 判据｜⚠️ `PointerShapeBufferSize=0` **不代表没指针**（可能是"形状没变，复用上次"）；`MASKED_COLOR` 是 **XOR 语义**，当普通位图直画＝**深色背景上指针变黑洞**
  - **装配有形状冲突**：GPU 那套（`WindowsMediaFoundationVideoEncoder`/`D3D11ColorConverter`/`D3D11GpuContext`）**全是 `internal`**（托盘够不着）⇒ 要加**公开门面**；且 `IFrameEncoder.TryEncode` 吃 **CPU 帧**而 GPU 要纹理 ⇒ 新增 `ISurfaceFrameEncoder` 走 `CaptureSurfaceAsync`（M5.0 已备好、一直没人用）

## 5. M5.4-a：MF 硬编

- 🔥 **M5.4-a 硬编不许忘（10-16 落地）**：🔴🔴 **异步 MFT 令牌就是权限，没令牌不许要输出**（最贵一条）：旧写法"一次 `TryEncode` 连着 `ProcessOutput` 64 次"必然**在喂第一批帧之前先要一次**；QSV 对"没东西却来要"回 **`E_UNEXPECTED`+`NO_SAMPLE`**（**不是** `MF_E_TRANSFORM_NEED_MORE_INPUT`），**且那一次之后它再也不发 `NeedInput`** ⇒ 现场＝"编码器建得好好的、喂 1 帧、一辈子不出货"，日志只有无信息量的 `0x8000FFFF`；**以令牌数封顶**（令牌内可再追几次，B 帧/重排）｜🔴🔴 **`MF_E_TRANSFORM_STREAM_CHANGE` 是硬编出码流前的必经一步**，且必须在**同一次调用里**重谈输出类型并 `continue` 重试（**它不会因为我们答应换类型而补发 `HaveOutput`**）；不处理＝"一帧不出、无错码"｜🔴🔴 **`dwStatus` 是四个互斥枚举不是位标志** ⇒ `0x300` ＝ `NO_SAMPLE` **一个值**，判它一律 `==`｜🔴🔴 **`GetEventType` 在 `IMFMediaEvent`（槽 33）不在 `IMFMediaEventGenerator`（只有 7 槽）** ⇒ 绑错＝读到 vtable 之后 264 字节当地址 call＝**进程消失**｜🔴 **`MF_E_NOTACCEPTING` 不是错误**（"先把输出取走再来"）⇒ 喂样本**必须三态**，压成 bool 两种归法都错（归失败判死好码流／归成功静默丢帧）｜🔴 **`MFTEnumEx` 编解码器参数相反**（编码器 H.264 填第 4 参、**解码器填第 3 参**；填错＝拿到 0 个＝假结论"本机没装解码器"）｜🔴 **同步 `Drain` 要取到取不出为止**（`COMMAND_DRAIN` 对同步 MFT **允许 `E_NOTIMPL`，别看返回值**；旧写法"第一轮 0 就 break"⇒ 30 进 22 出，静默吞 8 帧）｜🔴 **测试驱动异步编码器必须给真实时间**（全速转＝180 次机会只喂进 5 帧 ⇒ 测的是"我们的循环多快"）｜🔴 **`MF_TRANSFORM_ASYNC_UNLOCK` 有两半**（activate ＋ **transform 自己的 `GetAttributes`**），漏第二半＝`0xC00D6D77` 且在**后面某次调用**才报｜🔴🔴🔴 **`MFSetAttributeSize` 宽度在高 32 位**（`mfapi.h:799/801`：`PackSize(uw,uh)`→`Pack2UINT32AsUINT64(uw,uh)`，而它第一个形参叫 `uh` ⇒ **形参名会骗人**）；**踩过一次**＝某测试**自己又写一份 `Pack(low,high)`**（语序正相反）⇒ `FRAME_SIZE` 转置 + `FRAME_RATE` 变 **1/30**，**零错误码**，码流 **37,115,145 B → 882 B（≈4 万倍）**，坐实它的是**把 `Lock.max` 一并打出来**（16,666,000→555,000）⇒ **打包只留一处实现；"数字对不对"要并排打**｜🔥 **「零拷贝成立」不能用 `ResetDevice` 的 `S_OK` 代替**：`pUnkDevice` 是弱类型且**官方文档自己矛盾**（参数表说 DXGI、备注说 D3D；`ID3D11Device` 本来就能 QI 出 `IDXGIDevice` ⇒ 两种传法都 `S_OK`，所以**所有用例都发现不了**）⇒ 唯一判据＝**真的调一次 `OpenDeviceHandle`**（`DeviceHandleVerified`）；`已挂上` 与 `句柄自检通过` **必须分开显示**｜⚠️ 文档：`ResetDevice` 成功会**作废所有已开句柄**｜🔥 **排查手法的价值本身**：把「我们的 D3D 接线错了」与「这个 MFT 在这台机器上不出货」分开的唯一办法＝**写个把 D3D 整个摘掉的探针**（内存样本、不发 `SET_D3D_MANAGER`）——生产路径里这两件事症状**逐字节一样**；该探针已**转正**为 `MfMemoryModeEncodeProbeTests`（**不要求**机器有硬编，没有就什么都不判 ⇒ 防假 FAIL）｜**实测（UHD 620／QSV H.264／2160×1440@30）**：喂 30/编 30/解回 30，**325.3 KB / 30 帧、单帧 10.8 KB**，关键帧 1/30，Annex-B，SPS `0x64`（High）；**彩色 333,118 B / 灰度 270,453 B ⇒ 灰度省 18.9 %**（落在 §16.1 ⑥ 的 10–30 %；⚠️ 只验了一种内容，**"白底黑字≈0"那半边仍未验**，不许当固定系数引用）

## 6. M5.4-b §8 第 1–2 步落地（10-16）：尺寸闸门

### 🔴 走法已拍板：**(B→A) 两步走**（本批抬上限；分片留 M5.4-c）

背景：M5.4-a 实测 2160×1440 的 **IDR 单帧 306.9 KB**，而中心 512 KB ÷ 1.333（base64）
⇒ 裸码流上限只有 **384 KB**，**余量只剩 20%**；4K 是 2.67× 像素 ⇒ IDR ≈800 KB **必然撞穿**。
⚠️ **8K 验收前必须先做完分片**，且分片要连带改中心 `RemoteSessionRegistry` 的转队列
（现为**容量 1 + DropOldest**，隐含"一条消息＝一帧"，分片会被互相挤掉 ⇒ 只送最后一片
⇒ **画面彻底不动而日志全正常**）。

### 🔥 落地：把"两个数字"收成**一处口径**

- 新增 `Contracts/RemoteStreamLimits.cs`：`SignalRMessageBytes = 8 MB`；
  `MaxFramePayloadBytes = 8 MB / 4 × 3 − 8 MB / 32` ≈ **5.75 MB**（扣 base64 的 +33%，再留 1/32 给帧头与 JSON 外壳）。
- 中心 `Program.cs` 直接引用那个常量（**不再抄字面量**）；`RemoteStreamProtocol.MaxPayloadBytes`
  也引用它 ⇒ **"客户机能发多大"与"中心愿意收多大"只可能是一个数**。
  ⚠️ 这两个数**已经漂过一次**（托盘 384 KB / 中心 512 KB），而漂的方向只有一种：
  **客户机还在发、中心已经把整条连接掐了** —— 而那条连接上跑的不只是视频，
  心跳、遥测、配置、音频全在它上面 ⇒ 「某一帧大了几十 KB」现场表现为**整台客户机掉线**。

### 🔴🔴 修掉"症状指向相反"：帧太大 ≠ 接收端没了

- 新增 **`FrameTooLargeException`**（**刻意不继承** `InvalidOperationException` / `IOException`）：
  让"可恢复（丢这一帧）"与"终止（对面没了）"用**不同的类型**带出去。
  ⚠️ 读异常**消息**去分类是脆的 —— 文案一改分类就错，而错的方向恰好是**把一次丢帧升级成一次掉线**。
- 推流循环：**写之前用 `RemoteStreamProtocol.FitsInPipe` 预检**（不走异常路径），
  再留一个 `catch (FrameTooLargeException)` 兜底 ⇒ **忘了预检的代价是"丢一帧"而不是"掉一条流"**。
- 超限处置＝**丢这一帧 ＋ 下一帧强制关键帧**（`wantKeyframe`）。
  🔥 这是「H.264 的帧不能像 MJPEG 那样自由丢」的落点：P 帧参考前面的帧，丢一帧就在参考链上
  戳一个洞，此后观看端解出来的每一帧都是花的 —— **而解码器不报错**。
  强制关键帧把恢复代价从"最多一个 GOP（默认 60 帧）"压到"下一帧"。
- **关键帧自己也超限**时不能无限重试（再要一个关键帧只会得到同样大的）⇒ 连续
  `OversizeKeyframeGiveUp = 3` 次 ⇒ **如实收场**并把两个数（这一帧多大 / 上限多大）写进
  `LastError`（它会随状态上报回中心、显示在页面上）。理由是内容突变会让某一帧异常大、
  下一帧就正常，所以要给两次重来的机会。
  ⚠️ 与 `StartupFrameBudget`（判据＝**从没出过帧**，不用连续失败次数）**刻意不同**：
  那边失败的是"抓屏配置"（坏就永远坏），这边失败的是"这一帧的形状"（随内容变）。

### ⚠️ 实测纠正了一个想当然：序号**不会**留空洞

原本以为"丢一帧会在 `Sequence` 上留一个洞"——**实测 `Sequence` 是连续的**
（`received[0].Header.Sequence == 1`）。真因：序号是在**构造帧头时**才取的，
而超限帧在尺寸闸门就被拦下，根本走不到那里。
⇒ 这是**对的**语义（序号回答"真的发出去了第几帧"），**不该反过来改它**；
要表达丢帧请用 `Dropped`（它随每一帧带到中心、再显示到页面上）。
一个没人消费的空洞只是噪音 —— 用例改成钉「序号连续 ＋ `Dropped` 递增」。

### 交付

- 新增用例 **`tests/ControlServer.Agent.Tests/RemoteStreamOversizeTests.cs`（5/5）**：
  ① 超限后流还在、② 序号连续且 `Dropped` 递增、③ 下一帧确实带 `forceKeyframe`、
  ④ 日志**没有**"接收端已断开"、⑤ 关键帧一直超限会**如实收场**；
  另两条纯契约：预检与写入器**用同一个常量**、上限的**两条推导关系**
  （扣 base64 后仍装得进消息 / 必须够 4K 关键帧用）。
  用**真命名管道**跑（要证的是"循环没退出"，纯函数测不出来）。
- 全量回归：**Core 844 / Agent 133 / Server.IntegrationTests 499**，0 警告 0 错误。
- 顺带更正一处过时注释：`GdiJpegFrameEncoder.DefaultMaxFrameBytes = 380 KB`
  **不再是**"中心的上限"，而是 **MJPEG 降级路径自己定的预算**（上限抬了，这个数保留的理由换成了三条）。

---

## 7. M5.4-b §8 第 3 步落地（10-16）：**纯函数层**（Annex-B / avcC / hvcC / codec 串）

### 放在哪：`agents/ControlServer.Agent/Video/`（**不是** `Platform.Windows`）

🔥 **这是本仓自己立过的规矩**（`ControlServer.Agent.csproj` 里放 Concentus 的那段注释）：
**"纯托管、不碰平台 API 的东西放 Agent 程序集 —— 塞进 `Platform.*` 会让「平台层 = 平台专有实现」
这条分层判据失效。"** ⇒ 方案 §4 原先写的 `Platform.Windows/AnnexBToAvcC.cs` **是错的**，已改。
额外好处：`Agent.Tests` 已有 `InternalsVisibleTo`，用例不必挤进要启动服务的集成工程。

六个文件：`AnnexB.cs`（枚举 NAL ＋ 换长度前缀）、`NalUnitTypes.cs`、`RbspBitReader.cs`、
`AvcConfiguration.cs`（SPS 3 字节 ＋ avcC）、`HevcConfiguration.cs`（真解 `profile_tier_level()` ＋ hvcC）、
`VideoParameterSets.cs`（一站入口）。

### 🔴🔴 WebCodecs 的**两条路**：`description` 在不在，决定码流的**封装**（不只是"多一段字节"）

W3C 注册原文（两篇都核过）：
- `description` **有** ⇒ 它是 `AVCDecoderConfigurationRecord` / `HEVCDecoderConfigurationRecord`，
  **码流被假定为 `avc` / `hevc` 格式**（canonical ＝ **长度前缀**，ISO 14496-15 §5.3.2/§8.3.2）。
- `description` **没有** ⇒ 码流被假定为 **Annex B**。
⇒ 「贴一段 avcC 进去、码流还是 Annex-B」＝ configure 失败或花屏。**两者是连体的。**

### 🔴🔴 实测**推翻**了原来那条假设：**MF 在每个关键帧都重复 SPS**

| 原假设 | 实测（10-16，`DxgiHardwareEncodeSmokeTests` 加第二个强制关键帧） |
|---|---|
| "30 帧里 SPS 只出现 1 次 ⇒ 参数集一辈子只发一次" | **样本太小造成的假象**。第 0 帧与第 15 帧的 NAL 序列**完全一样**：`9,7,8,5`（AUD,SPS,PPS,IDR）⇒ **每个关键帧都带 SPS+PPS** |

**为什么这条重要**：注册写明 Annex-B 下**每个 `type:"key"` 的块都必须自带全套参数集**；
而本批定的丢帧恢复是 `reset()` → 等下一个关键帧 → 重新 `configure()`。
若参数集只在第一个关键帧出现，这个动作就会让新解码器**手上没有参数集**
（症状＝重连后一直花屏、两端日志正常）。⇒ 实测证明 MF 满足要求。

⇒ **路线偏好反过来**（但**留到第 6/8 步用浏览器实测收口**，不在这里拍死）：
**带内（annexb，不带 description）＝ 新默认候选**（热路径零转换、不需要 avcC/hvcC）；
带外（description）＝ 原选定（中途加入立刻能 configure；`reset()` 不依赖编码器行为）。
⚠️ 带内那条路 codec 串前缀应是 **`avc3`**（参数集在码流里），带外是 `avc1`；
⚠️ **"Chromium 认不认 `avc3`"没有证据** ⇒ 第 8 步探测**两条前缀都试**，用 `isConfigSupported` 定。

### 🔴 codec 串：两套规则，都容易"看起来对"

- **H.264**：`avc1.` / `avc3.` ＋ **6 位十六进制** ＝ SPS **载荷前 3 字节**（`profile_idc` / 约束字节 /
  `level_idc`）。**字节对齐，抄就完了** —— 这就是"从真实 SPS 反推"成本极低的原因。
  本机实测 `27 64 00 32 …` ⇒ **`avc1.640032`**（High / level 5.0）。
- **H.265**：`hvc1.` / `hev1.` ＋ 四段。🔴 **兼容性标志必须"位序反转"**（ISO 14496-15 §E.3 /
  3GPP TS 26.244 §A.2.2 / MDN）：以 `flag[31]` 为最高位。
  ⇒ Main profile（位流里 `flag[1]` 与 `flag[2]` 都是 1 ＝ `0x60000000`）反出来是 `0x6`，
  串是 **`hvc1.1.6.L93.B0`**（**不是** `…1.2…`）。TS 26.244 特意解释过：第二个点后面是 6 不是 2，
  是因为 Main 码流按 ISO/IEC 23008-2 **也应当标成与 Main 10 兼容**。
  - 约束那 6 个字节**不反转**，逐个十六进制、点分隔，**末尾零字节可省但至少留一个**（全零写 `.00`）。
  - `profile_space` → 前缀 `""`/`A`/`B`/`C`；tier → `L`/`H`。
  - ⚠️ 写这个串最容易的错法是"直读"或"按字节反"，两者都能得到**合法但浏览器不认**的值。
- ⚠️ **H.265 的 `profile_tier_level()` 是变长的**（子层 flags ＋ reserved 补齐 ＋ 每层 88/8 位），
  所以色度/位深**不能按固定偏移取字节** —— 必须真的位解析。用户例的合成素材也**必须按语法写全**，
  少写一截会让解析器读出一串"合法但错"的值。

### 🔴 emulation prevention：**判据看输入，不看输出**

编码器在源数据出现 `00 00` 之后插 `0x03`。而"已经解出来的输出"在跳过之后**也**以 `00 00` 结尾 ⇒
源数据 `00 00 03`（码流里 `00 00 03 03`）时两种判据结论相反：看输出会把第二个 `03` 也删掉，
解出 `00 00`。⇒ 用**输入**里前两个字节判断。症状是 SPS 解到一半开始错，**不一定立刻失败**。

### ⚠️ IDR 大小**随内容浮动 1.6 倍**

同机同参数三次落盘：**314,267 / 234,623 / 198,209 字节**（2026-10-16 又跑了两轮）。
⇒ 方案 §6.1 #3 那句"306.9 KB 是**静态内容的下界**"**不成立**，只是某一次的取值。
（抬到 5.75 MB 之后已不影响 1440p/4K 结论，但 4K 的 2.67× 外推也**只有量级意义**。）

### 交付 / 回归

- 新增 **40 条纯函数用例**（`Agent 133 → 173`）：`VideoTestVectors.cs`（**素材是从真机码流里抠出来的**
  SPS/PPS，不是手搓）＋ `VideoBitstreamFramingTests` / `VideoCodecStringTests` / `VideoConfigRecordTests`。
  含：尾部填充零归属、空 NAL 跳过、长度前缀大端、丢掉参数集后**第一段必须直接是 AUD**、
  目标缓冲小 1 字节必须返回 −1、EPB 两个方向的判据、Exp-Golomb 取值、H.264/H.265 取类型的位不同、
  codec 串的位序反转（`0x80000000 → 1`、`0x00000001 → 80000000`）、avcC/hvcC 逐字节。
- ⚠️ 用例当场红过一次：我把 `0x0000FF00` 的反转结果写成 `FF00`（其实应是 `FF0000`）——
  **是我的算术错，不是代码错**，改预期不改代码。
- `DxgiHardwareEncodeSmokeTests` 加了**第二个强制关键帧**＋每个关键帧的 NAL 序列打印（就是它推翻了原假设）；
  并加了真断言：**第一个关键帧必须自带 SPS 与 PPS**（那是 description 的唯一来源）。

### 挂账（已写进方案 §7）

⚠️ **HEVC 那半边只有"按规格合成"的素材**，没有真机 HEVC 码流复验 ⇒ **8K 之前必须清**
（HEVC 是超 4K 的唯一路径；且**没有现成工具能当标准答案**，真正判据是"喂进去能解出画面"）。
⚠️ `avc1`/`avc3` 前缀待浏览器实测；⚠️ "带内/带外"最终路线留第 6/8 步收口；
⚠️ avcC 刻意不写高档次可选扩展块（若带外那条路解不动，这是第一个要查的地方）。

---

## 8. M5.4-b §8 第 4 步落地（10-16）：**GPU 公开门面**＋`ISurfaceFrameEncoder`（7/7）

> 方案详版：`docs/M5.4b-实施方案.md` **§3.8**。

### 交付

- `Platform.Abstractions/RemoteDesktop.cs`：**新增 `IVideoFrameEncoder`**（`Codec`/`LastFailure`/
  `Grayscale`/`Dispose` 的共同底座）＋ **`ISurfaceFrameEncoder : IVideoFrameEncoder`**；
  `IFrameEncoder` 改为继承底座（**签名一字未动**）；`EncodedStreamPayload` 加 `Description`
  （可空、**带默认值** ⇒ 既有构造点一行不动）。
- `Platform.Windows/WindowsGpuFrameEncoder.cs`（**新增**）：门面持有 `WindowsDxgiScreenCapturer`
  ＋`D3D11Nv12Converter`＋`WindowsMediaFoundationVideoEncoder`；`GpuPipelineStage`/`GpuPipelineVerdict`；
  `TryValidateGeometry`（纯函数闸门）；`Describe()`；`TryReadParameterSets()`。
- `MediaFoundationInterop.cs`：新增 `CODECAPI_AVEncMPVDefaultBPictureCount`
  = `8d390aac-dc5c-4200-b57f-814d04babab2`（**核自 `codecapi.h:754`**）；编码器侧 `已设 0`。
- `tests/.../BitstreamPeek.cs`（**新增**）：把两个测试文件里**各一份**的极简 NAL 扫描收成一处。

### 🔥 三条"形状级"设计决定（比代码本身重要）

1. **共同底座**（`IVideoFrameEncoder`）——宿主**同时只持有一个**编码器（GPU 或 MJPEG，
   **不会并存**）⇒ 有底座就只持一个字段，**只在"抓屏那一步"分一次叉**（`is ISurfaceFrameEncoder`）。
   ⚠️ 底座**刻意不含 `TryEncode`**：那个方法的入参（`CapturedFrame` 的 `byte[]` vs
   `CapturedSurface` 的纹理句柄）正是两条路的分水岭，提上去等于把"像素在哪"变回运行时问题。
2. **门面持有抓屏器**（`ISurfaceFrameEncoder.Capturer`）——零拷贝要求三者同设备，
   而设备是**抓屏器绑屏时建的、随换屏重建**（`RebuildDuplication()` 会 `_gpu?.Dispose()`）。
   让两条工厂线各产一个再靠构造顺序对齐 = 不变式靠调用顺序维持，**破坏了不报错**，
   只会让驱动偷偷多一次 PCIe 往返（4K 1.99 GB/s）⇒ 收进同一对象 = **类型级**不变式。
3. **三种不匹配都必须整体重建，且三种的症状都不是报错**：①**设备换了**（旧设备已被 Dispose
   ⇒ 用后释放）；②**分辨率变了**（媒体类型与视频处理器写死尺寸 ⇒ 拿旧实例编新尺寸只得到噪点）；
   ③首次。判据是**引用相等 / 尺寸相等**这类廉价比较，不是"试一发看看"。

### 🔴 本层**刻意不填** `Description`（不是"还没做"）

MF 的 `MF_MT_MPEG_SEQUENCE_HEADER` 是 **Annex-B**，而 `EncodedStreamPayload.Description`
的契约是 **avcC/hvcC**。**"填它"与"把码流转成长度前缀"是连体的** ⇒ 只做一半就
`configure()` 失败/花屏。转换是纯函数、住 `ControlServer.Agent.Video`（**平台层不做**），
接线在第 6 步。⇒ 本层交出去的 `Description` **恒 null**，参数集只从 `TryReadParameterSets()` 出去。
✅ 用例 `The_platform_layer_never_fills_the_avcC_slot_with_annex_b_bytes` 把这条边界钉死。

### 🔴 结构化回退判决：`GpuPipelineVerdict { Stage, Reason }` ＋ `IsEnvironmentProblem`

`Stage ∈ {Platform, Screen, Capture, ColorConvert, Encode, Unexpected}`。
Screen/Capture = **环境**（屏被别的抓屏工具占着 / 会话没桌面）⇒ 安静回落 MJPEG；
其余 = **缺陷** ⇒ 进告警。**它同时是验收用例的"跳过 / FAIL"分界**：
环境问题跳过、缺陷必须红（否则接线错误会以"本机没硬编"的姿态被**永久跳过**）。

### 🔥 真机实测（2160×1440 / UHD 620 / QSV，与 M5.4-a 同机）

| 项 | 值 | 判读 |
|---|---|---|
| 带外参数集 | **36 字节**，`00 00 01 27 64 00 32 AC …`（**3 字节起始码**！） | ⚠️ **与 §6.2 #7 的"恒为 null"不一致** ⇒ **两条路都要准备** |
| 码流内联 | `AUD,SPS,PPS,IDR片` | 与 §3.4.2 一致 ⇒ 挑哪条路解码器都拿得到参数集 |
| codec 串 | `27 64 00 32` ⇒ **`avc1.640032`** | 第 3 步那条"必须从真 SPS 反推"的**第二次独立证据** |
| B 帧上限 | `已设 0` | §6.1 #1"靠默认值"的挂账**降级**（但**"设上了"≠"码流里真没有"**） |
| 零拷贝 | 纹理直连（无中间拷贝）＋**设备句柄自检通过** | 报告文字与这两个可独立读出的事实**由用例钉住一致** |
| 关键帧 #0 | 225,041 字节 | ⚠️ 40 轮猛喂只出 13 帧（其余 `NoDesktopUpdate` ＋ 忙丢帧）⇒ **那不是帧率** |

### 用例（7/7）＋两个自造的坑

纯函数 2 条（尺寸闸门 / 判决分类）＋ 真机 5 条（参数集自带、`Description` 恒 null、
灰度翻转重开 GOP、**报告不许说谎**、释放后推帧可读）。**报告不许说谎**那条的判据是
"`Describe()` 的每一句都要与一个**可独立读出**的 flag 一致"（`DeviceHandleVerified` /
`UsesIntermediateCopy`）—— 报告和实际没有任何一刻会被对齐时，现场看到的是**理直气壮的假话**。

⚠️ 两个坑都是**我自己错、改用例不改代码**：
① `TryValidateGeometry(Hevc, 8192, 8193)` —— `8193` 是**奇数**，奇数闸门先触发 ⇒
拿到的原因是"奇数"不是"超上限"。**闸门有先后，用例输入必须体现。**
② FluentAssertions 的 `Contain(predicate)` 吃**表达式树** ⇒ 里面**不能写 `is` 模式**（`CS8122`）。

---

## M5.4-b §8 第 5 步：指针合成（10-16 落地）

> 详版在 `docs/M5.4b-实施方案.md` **§3.3.2**。这里只留"下次开工必须记住"的。

### 拍板与机制（§3.3.1）

§3.3 只写"**必须在 GPU 上**把指针画进 NV12"，**没写怎么画**。核查：现有转换器是
`ID3D11VideoProcessor`、**固定功能**（明写"不引 HLSL / d3dcompiler"），**没有"再叠一层"的能力**，
全仓**零 shader 基础设施** ⇒ 当时**没有可用的执行体** ⇒ 出 §3.3.1 三候选请用户拍板。
**结果 ＝ B（CPU 小块合成）＋ 完整三态**。

- **C（VideoProcessor 多流）是"做不到"不是"精度不够"**：混合单元只会 `src α over dst`，
  做不了 `MASKED_COLOR` 的逐位 XOR / `MONOCHROME` 的 `(dst AND and) XOR xor`。
  ⚠️ **10-16 实测 `MaxStreamStates = 8`（本机）** ⇒ 别再用"多路流开不起来"当理由，
  **唯一**理由是语义。`FilterCaps` 那条老教训在这里的同型复现：**能力声明 ≠ 行为**。
- **A（HLSL 渲染管线）** 是行业标准但要引本仓第一份 `d3dcompiler` 依赖，本批不走。

### 🔴 实施**偏离**了 §3.3.1 里 B 的描述（不是跑了题）

方案 B 写"CPU 合成 → **CPU 转 NV12** → `CopySubresourceRegion` 写回"，**实际没让 CPU 转 NV12**：

```
CPU 合成（只碰指针那一小块 BGRA，~4 KB）
  → UpdateSubresource 上传到一张 BGRA 小纹理
    → 交给**同一个** ID3D11VideoProcessor 局部 Blt（SourceRect + DestRect）→ NV12
```

1. 🔴 **CPU 自己转 NV12 ＝ 可见贴片**：整屏那趟是 VideoProcessor 用 BT.601 有限范围系数转的
   （M5.4-a 实测 MAE 0.06）⇒ 两套系数**不可能逐位一致**，指针周围颜色肉眼可见地偏，**不报错**。
2. 🔴 **NV12 的色度平面在 D3D11 纹理里没有独立地址空间**（`Desc.Height` 只有图像高度）
   ⇒ `UpdateSubresource` + `D3D11_BOX` **写不进 NV12**。

⇒ 与 §3.3 (a) "必须在 GPU 上画进 NV12"**不矛盾**：CPU 只碰 4 KB BGRA（没放弃零拷贝），
**颜色转换与 NV12 写入仍在 GPU、且是同一条通路**。B 里"CPU 转 NV12"半句作废。

### 🔴🔴 桌面副本（`_desktopCopy`）是"纯指针帧"的硬前提

DDA **只在桌面真变时才填纹理**，`AccumulatedFrames==0` 时内容**未定义**（本机实测全 0）
⇒ **纯鼠标移动那一帧没有桌面可发**。副本另解"`ReleaseFrame` 后 DDA 那张纹理随时被复用"。
⚠️ `ReleaseDesktopCopy()` **必须连带清 `_desktopCopyFilled`** —— 否则新建 duplication 头几帧
会"**通过闸门却内容全 0**"。⚠️ **刻意不在 `TryAcquire` 里 `ReleaseFrame`**：`CopyResource` 只是提交命令，
留到"下一次抓之前"放 ＝ 给 GPU 整整一帧时间跑完拷贝。

### 🔴 六条"形状级"决定（每条都能单独把画面搞坏而不报错）

1. 🔴 **`PointerUpdated` 与 `DesktopUpdated` 刻意分开**：合并后"帧率很高"与"画面一直没动"就分不开（处置相反）。
2. 🔴 **纯指针移动判据要两个条件**：`LastMouseUpdateTime != 0 && != 上帧值`。
   **只看"变了"不够** —— DDA 无鼠标事件时**既可能清 0、也可能保留上次值**，两者都见过。
3. 🔴 **"形状变了"与"位置变了"是两件事**：`PointerShapeBufferSize > 0` 只说形状有变；
   位置与可见性**每帧都读** `PointerPosition`。⚠️ 本机实测 `Visible=False` 与"形状拿到了"**并存**。
4. 🔴 **CPU 合成后是"覆盖"不是"混合"**（混合已在 CPU 做完）⇒ `TryOverlay` **必须按成败都复位 stream rect**
   —— 残留到下一帧会让**整屏那趟只转一个角**。
5. 🔴 **`_overlayInputView` 不能与 `_inputView` 共用**：共用 ⇒ 整屏那趟拿**指错纹理**的视图去 `Blt`，
   驱动**不报错**，画面是"上一帧那一块糊上去"。
6. 🔴 **`GetFramePointerShape` 支持两段式**（不够回 `DXGI_ERROR_MORE_DATA`）；
   ⚠️ **别把 `GetFrameDirtyRects` 那条"不许两段式"的教训挪过来**（那个 API 那样问回来恒是 0）。

### 🎯 真机实测①：指针形状类型**会变**（不是机器的属性）

| 采样 | 形状 | Pitch | 热点 |
|---|---|---|---|
| 早先一次 | **`Monochrome`** 32×32 | **4** | 8,9 |
| 后来连续 4 次 | **`Color`** 32×32 | **128** | 0,0 |

**两读数各自都自洽**（`Monochrome` ⇒ 1bpp、32 宽 ⇒ 4 字节/行、`Height=64`＝2×32 双掩码；
`Color` ⇒ 32bpp ⇒ 128 字节/行）⇒ **不是读错，是两种都真的出现过**。
⇒ 🔥 类型是**"当时活跃光标"的属性**（箭头＝`Color`、文本 I 形＝`Monochrome`）。
⇒ **只做 `COLOR` 分支的话，光标一进文本区指针就消失** ⇒ **"完整三态"不是保险，是机制要求**。
⚠️ 且 **DDA 只在形状变化时才给字节**、形状不变时**复用缓存** ⇒ **"开抓那一刻的光标"决定这一轮形状**。
⚠️ 欠一次**受控实验**（光标分别停文本区/桌面各采一次）—— 留给第 9 步。
⚠️ 连带：`Monochrome` 那行 Pitch 恰好能用 `宽/8` 算、`Color` 那行必须 `宽×4` ⇒ **两种口径都出现过**
⇒ **一律用驱动给的 `Pitch`**，任何"按宽度推算"都会在某类光标上错。

### 🔴 指针合成"整体链路"**尚未用真像素验过**（本轮最大缺口）

`PointerCompositor` 三态**纯函数**有 20 条用例钉死；**但** `TryReadPointerRegion`
（`CopySubresourceRegion` 读回）→ `TryUploadAndOverlay`（`UpdateSubresource` ＋ `TryOverlay`）
这一串**只在编译期过了**，**没有**用例证明"指针真被写进编码器看到的 NV12"。
⚠️ 本机实测时指针恰好 `Visible=False` ⇒ 这一串**那次根本没跑**。
⚠️ 且这条链**出错是静的**（`CopySubresourceRegion` / `UpdateSubresource` 都是 `void`，无错误码）
⇒ 门面自己加了 `CursorFailures` / `CursorNote` 自证。
⇒ 判据 ＝ 第 9 步端到端真机验收（**动鼠标、截屏、肉眼确认**）。

### ✅ 顺带纠正一条用词

`IsCursorIncluded` **仍恒为 `false`** —— 那是**如实**的（DDA 桌面图确实不含指针）。
合成结果走 `FramesWithCursor` / `CursorNote`，**没有**去改这个 DDA 字段（**改了才是说谎**）。

### 用例与回归（10-16）

- `PointerCompositorTests` **20/20（新增）**：区域 6（含**断言钉 `Pitch×行数` 而非 `宽×高×4`**）
  ＋ `COLOR` 4（含**整份 α 全 0 按不透明画**）＋ `MASKED_COLOR` 2（断言 XOR `NotBe(0x00)`）
  ＋ `MONOCHROME` 3（四情形 `[Theory]` ＋ 高位在前 ＋ XOR 取自缓冲后半）＋ 边界 2。
- `DxgiCaptureSmokeTests` 7/7 已适配：断言从 `AccumulatedFrames > 0` 改为 **`HasPopulatedDesktopCopy`**
  （**表述变了、含义没变**）；并**打印指针三态**留痕（上面那张表就是它打印出来的）。
- ⚠️ **算术反例（留作教材）**：用例最初断言"需要 1024 字节"（`32×32×4`）而红了 ——
  真因是**我的算术错，不是代码错**：`NeededBytes = Pitch × 行数 = 32 × 8 = 256`。
  ⇒ **改用例、不改代码**。
- **全量回归**：`Core.Tests` **844** / `Agent.Tests` **173** / `Server.IntegrationTests` **526**
  （506 → 526，＋20 指针用例）全绿；全解决方案 **0 警告 0 错误**。

**下一步 ＝ §8 第 9 步：端到端真机验收**（动鼠标 / 截屏，肉眼确认指针在；🔥 **第一次看到真正的 H.264 画面**）。
> ⚠️ **验收方案已立档（§12 之后新增 §14；文档侧 ＝ `docs/M5.4b-实施方案.md` §3.12），四问待拍板，
> 未跑任何验收动作、未起任何进程。**
> ✅ 当时那条"下一步 ＝ §8 第 7 步（中心协商）"**已于 10-16 落地**，见 §11。
> ✅ 当时那条"下一步 ＝ §8 第 8 步（WebCodecs ＋ 真探测 ＋ 横幅）"**已于 10-16 落地**，见 §13。

---

## 9. M5.4-b §8 第 6 步落地（10-16）：**被控端接线**（8/8 新增用例）

### 这一步真正改变了什么

**M5.4-a 那三层（DXGI 抓屏 / GPU 转 NV12 / MF 硬编）此前只活在集成测试里** ——
托盘装配的仍是 `quality => new GdiJpegFrameEncoder(quality)`（GDI+ 软 JPEG）。
本步第一次把它们**接到真实推流上**。所以"已完成"要分两层说：
**三层各自可用 ≠ 链路已换**。

### 三条拍板（用户全选"推荐"）

| 问 | 拍板 |
|---|---|
| 打包方式 | **两条都做，默认带内**（Annex-B 原样转发、`description=null`、codec 串 `avc3`；带外留开关） |
| 装配范围 | **接上，默认启用 GPU**（M5.4-b 本意就是端到端看到硬编画面；回退必须说得出停在哪一级） |
| 丢帧配对 | **本轮一并修**（门面加"喂进 ↔ 取出" FIFO 配对） |

### 🔴🔴 修掉一处**既有缺陷**：异步 MFT 输出**晚一次调用**才到

- **实测**：喂 17 帧 / 出 16 帧 ⇒ `TryEncodeSurface(surface_N)` 拿到的字节是**第 N−1 帧**的。
- **旧写法的两个后果**：
  1. 把这份字节贴上 `surface_N` 的时间戳 ⇒ **AV 整体差一帧**（30 fps 下 33 ms，唇音看得出）；
  2. 把"本轮没出帧"记成一次丢帧 ⇒ **页面报"掉了很多帧"而实际一帧没丢**。
- **修法**：门面自己按 `EnqueuePendingFrame` ↔ `DequeuePendingFrame` 配对元数据，
  抓屏时刻由 `EncodedStreamPayload.CapturedMonoTicks` **带出来**（不让调用方在编完之后自己取）。
- 🔸 与 §3.6"把一次丢帧升级成一次掉线"**同族**：**计数错了，排查方向就反了**
  （现场会先去查网络，而真因在编码器/时间戳）。

### 三分类：判据是"**喂进去没有**"，不是读失败文案

`accepted = encoder.FramesFed > fedBefore`

| 情形 | 判定 | 要不要关键帧 |
|---|---|---|
| `accepted && 没吐` | **正常延迟**，不计丢帧 | **不要**（那帧没丢，参考链上没洞；4K 的 IDR 本就贴着管道上限） |
| `!accepted` | 忙丢（没进编码器 ⇒ 也不留洞）或真失败 | 要 |

⇒ 宿主侧**调用前后各读一次 `DiscardedFrames`，增量 > 0 才算丢帧**（文案一改分类就错）。

### 🔥 "抓屏没有新帧"**不是失败、不是丢帧，也不该收场**

DDA 是**增量投递**（`CaptureSurfaceAsync` 返回 `null` ＝ 桌面与指针都没变，按约定如此）。
当成失败会同时得到两个错误结论：① `Dropped` 虚高；② **启动预算按"从没出过帧"判定
⇒ 定格在一张图上的展厅机器 30 秒后被收场**，而它完全正常。
⇒ 处置：不计失败 / 不计丢帧 / 不喂编码器 / **重置启动预算时钟**
（预算的含义＝"只统计**有东西可发却发不出去**的那段时间"）。

### 🔴 挑屏的唯一出处：**门面挑，宿主搬**

两个后端的挑屏规则**刻意不同**：内存路径（GDI）**优先虚拟桌面**（能拼），
GPU 门面**优先主屏**（DDA 一次只能抓一个 output）。
⇒ 宿主若也挑一次，多屏机上**必然挑到另一块屏**：两边都"挑对了"、**无处报错**，
症状是 **看着 A 的屏幕、实际操作 B**。修法：
`ScreenId` / `ScreenName` / `FrameSize` 上抬到 `ISurfaceFrameEncoder`，
`Prepare` 分两半（GPU 那条**不接受 `screenId` 参数**）。

### 六条"形状级"决定（每条都能单独把画面搞坏而不报错）

1. **封装三件套必须自洽**：`avc3`/`hev1`＝带内（码流一字节不动、`Description=null`）；
   `avc1`/`hvc1`＝带外（码流必须转长度前缀**并摘掉参数集**）。
   "贴了 avcC、码流却还是 Annex-B" **是连体的**，只做一半不报错、只在浏览器黑屏/花屏。
2. **打包必须在尺寸闸门之前**：带外把 3/4 字节起始码换成**固定 4 字节**前缀，**长度会变**。
   拿转换前的长度过闸门 ＝ 用一个错的数字决定要不要丢帧。
3. **参数集从"这一帧自己的码流"里解，不问编码器**：`TryReadParameterSets()`
   （`MF_MT_MPEG_SEQUENCE_HEADER`，实测 36 B、3 字节起始码）**只做兜底**；
   解的时候**一律用带外的采样入口名**（`avc1`/`hvc1`），带内那份用 `ToInBand` 换前缀
   —— 解两次＝给自己留一个"两次看起来一样、实际不一样"的机会。
4. **只认得的打包方式才不回落**：`Normalize` 只管取值，**留痕由调用方做**
   （只回落不留痕 ⇒ 中心写错 `out-of-band` 被安静按带内跑，**症状在浏览器、真因在中心**）。
5. **嵌套类看不见外部实例字段**（只看得见 static/const）⇒ 宿主的 `_packaging` 传不进 `Session`，
   改 `Session(string sessionId, string defaultPackaging)` 构造参数。
6. **编码器工厂要带 `Fps`/`ScreenId`**：少了 `Fps` ⇒ 码控按错帧率估（码率忽高忽低）；
   少了 `ScreenId` ⇒ 门面按主屏挑（就是上面那条"看 A 操作 B"）。

### ✅ 补证：`gopSize = 4` ⇒ **8/8 个关键帧都自带 SPS**

真机（Intel QSV H.264，2160×1440，80 轮 ⇒ 32 帧 8 个关键帧）：**8/8**。
⚠️ 同文件里那条**同名旧用例**用默认 `GOP 60`、只跑 40 帧 ⇒ **每次只看到 1 个关键帧** ——
那正是"30 帧里 SPS 只出现 1 次"这个**错误读数**的成因：
**样本量不够时，结论与没有结论长得一模一样**。

### 托盘装配：GPU 优先 ＋ 自动回退 ＋ 本机强制开关

- 先问 `AgentGpuPolicy.ForcesMjpegIn(stateDirectory)`（存在 `remote-video.mjpeg` 就强制 MJPEG）
  —— 与 `AgentInputPolicy` 同款：**读文件、每次开流问一次、不走中心下发**。
  它处理的是"**做起来有问题**"，自动回退处理的是"**做不了**" ⇒ 判定权必须在现场。
- 否则 `WindowsGpuFrameEncoder.TryCreate(..., screenId: request.ScreenId)`；
  失败 ⇒ 回退 MJPEG 并把 **`verdict`（停在哪一级）＋ `IsEnvironmentProblem`** 写进日志
  （环境问题＝正常路径，不是故障；**非**环境问题才值得查）。
- 起始行三态：GPU 硬编（注明参数集带内/带外）／MJPEG（注明"逐帧独立：无参数集/GOP，打包方式不适用"）／
  中心下发的打包方式不认得。

### 用例与回归（10-16）

`tests/Agent.Tests/RemoteStreamPackagingTests.cs`（**新增 8/8**）：

| 用例 | 钉住什么 |
|---|---|
| `In_band_leaves_the_bitstream_untouched_and_sends_no_description` | 码流**逐字节相同**＋`Description==null`＋`avc3.640032` |
| `Out_of_band_prefixes_lengths_and_carries_the_configuration_record` | avcC 非空（`configurationVersion==1`）＋`avc1.640032`＋码流是长度前缀且**已无起始码** |
| `Out_of_band_without_parameter_sets_falls_back_to_in_band_for_that_frame` | 解不出参数集 ⇒ **整条**退回带内（帧头＋码流两半都退）＋两句话都要在日志里 |
| `An_unknown_packaging_falls_back_to_in_band_and_says_so` | `out-of-band`（连字符）⇒ 回落＋**留痕** |
| `A_desktop_that_never_changes_is_not_a_drop_and_not_a_failure` | 不计丢帧、**不收场**、不喂编码器 |
| `A_late_encoder_output_is_not_counted_as_a_dropped_frame` | `DroppedFrames==0` 而 `FramesFed>0` |
| `The_frame_timestamp_comes_from_the_encoder_not_from_encode_completion` | 帧头时刻来自**抓屏瞬间**（夹具把码流回溯 1 秒） |
| `The_gpu_screens_choice_wins_over_the_hosts_own_screen_picking` | 夹具里虚拟桌面 100×100 与主屏 2×2 **是两块不同的屏** ⇒ 宿主搬的是门面那块 |

- 夹具：`StubCapturer`（`VirtualDesktopFirst` / `NeverChanges`）、`ScriptedSurfaceEncoder`
  （`NeverProduces` / `backdate` / `FramesFed` / **`CapturerToReport` 可写**）。
- **四个自造的坑（留作教材）**：
  1. **FluentAssertions 的集合 `NotContain({0x00,0x00,0x01})` 是"不含这些元素"不是"不含这个子序列"**
     ⇒ 手写 `ContainsStartCode(byte[])` 扫描。
  2. **GPU 路径上抓屏器是编码器自己持有的** ⇒ 用例只把 stub 传给 `capturers` 工厂没用，
     必须 `encoder.CapturerToReport = capturer`（"会出画面"的默认 stub 会让"永远没新帧"用例收到帧）。
  3. **用 `await Task.Delay(1500)` 代替读帧 ⇒ 没人连管道** ⇒ `LoopAsync` 卡在
     `WaitForConnectionAsync`（超时 8000 ms），推流主循环根本没启动（`FramesFed == 0`）。
     改用 `ReadFramesAsync(sessionId, want: 1, timeoutMs: 1500)`。
  4. **`CS1003 语法错误` × 16 处**：我在**字符串字面量里**用了 ASCII `"` 做强调
     （项目惯例是 `「」`/`""`）⇒ C# 里第一个 `"` 就结束字面量。批量换成全角引号。
- **回归**：`Core.Tests` **844** ／ `Agent.Tests` **181**（173 → 181）／
  `Server.IntegrationTests` **527**（526 → 527）全绿；全解决方案 `dotnet build` **0 警告 0 错误**。

### ⚠️ 本步留下的未验证项（不掩盖）

1. 🔴 **GPU 管线的线程亲和没改也没验**：契约写着"从推流主循环那**一个**线程调用"，
   而循环是 `async` 的（`Task.Delay` 后续接在线程池上）、管线又**在 `Start` 线程建、在推流线程用**。
   依据只是**推理**（D3D11 即时上下文与 MF 异步 MFT 只要求"不并发"、不要求"同一个线程"，
   且本仓两处都有 `lock (_gate)` 串行化）。
   ⇒ **触发信号**：第 9 步若出现"崩溃"或"无法解释的花屏"，**第一件事是把泵钉到单线程**
   （专用线程 ＋ 所有 `await` 改阻塞），而不是先去查编码参数。
2. 🔴 **`Description` 的"跨帧可变"需要前端配合**：带外模式下某个关键帧解不出参数集时，
   那一帧会**单独**退回带内 ⇒ 第 8 步必须按
   "**关键帧上若 `(CodecString, Description)` 与上次不同就重新 `configure()`**"写，
   不能只在第一帧 configure 一次。
3. ⚠️ `GdiJpegFrameEncoder.DiscardedFrames` 与"返回 false"在这条路上**恰好等价**
   （两个字段同一个数）—— 仍然分开报，是为了让接口语义在两条路上一致。
4. ⚠️ **"8 MB 装得下"仍只验了一半**：`Packaging` 接上后码流长度会变（带外可能略增），
   4K 真实 IDR 要等第 9 步用运动画面测。
5. ⚠️ **`AgentGpuPolicy` 那个开关没有用例**（纯读文件的一行函数，与 `AgentInputPolicy` 同款）；
   真正要验的是"翻开关之后下一个会话真的走 MJPEG"，要等第 9 步。

✅ **10-16 已完成**：§8 第 7 步 = 中心协商取交集 + 下发 codec（见 **§11**）；
第 8 步 = 前端 WebCodecs 路径（方案立档见 **§12**）。

---

## 10. M5.4-b §8 第 7 步开工：协商方案立档（10-16，**未改一行生产代码**）

方案落在 `docs/M5.4b-实施方案.md` **§3.10**（(a)~(f) 六小节 ＋ 五问带推荐）。

### 🔴 两条被推翻的前提（本步真正的价值）

**① "取交集"取不出可以用的一对。** §3.2 原话是"被控端 `SupportedCodecs` ∩ 浏览端 `supports`"，
但还差**第三项输入：本次会话的分辨率**，而它有两个性质：
**只有被控端知道**（中心看不到被控端的屏）＋**只有在挑屏之后才知道**
（`WindowsGpuFrameEncoder.TryCreate` 先挑屏、再拿真实尺寸）。

| 反例 | 中心的交集 | 被控端的现实 | 结果 |
|---|---|---|---|
| 浏览器只支持 h264，机器 h264+h265，屏 **5120×2880** | 取到 `h264` | h264 **编不了**（超 4096），h265 能编 | 交集指向的那个**恰好不可行**，被排除的**恰好是唯一出路** |

⇒ 形状定为：**中心下发「偏好 ＋ 可接受集合」，被控端在挑屏之后裁决** ——
理由是**信息在哪一侧**，不是"谁更权威"。
⚠️ 裁决**只许往"我做不到"的方向走**（h264 → hevc），
**不许往"我能做但你不想"的方向偷偷升级**（中心说 h264-only 却发 hevc ⇒ 浏览器黑屏）。

**② §3.2 第 2 点"探测失败 ⇒ 回落到 MJPEG"已被第 6 步的结论推翻。** 那句写在第 6 步之前，
当时"带内"还没被证明可行。现在默认走**带内 `avc3`**（连 `description` 都不需要，失败方式最干净），
而 MJPEG 在 1440p 是**软编 12–25 fps**（`RemoteCodecs.Mjpeg` 注释）
⇒ 把"不知道"翻译成"降级到 20 fps 软编"，**是在为一次探测失败付长期代价**。
建议改判成"回落**带内 H.264**、MJPEG 退回它本来的位置（被控端连硬编都没有时的终点）"。

### 五条现状事实（都有出处）

1. **探测层刻意不给 `RecommendedCodec`**（`WindowsEncoderCapabilityProbe.cs:127-137`，
   `RecommendedCodec: null`），理由写得很好："格式由**目标分辨率**决定，而分辨率是**会话**才知道的事
   （同一台机器接不同的屏，答案就不同）" ⇒ **这不是遗漏，是纪律**。
2. **"尺寸 → 格式"早已是纯函数**：`WindowsGpuFrameEncoder.TryValidateGeometry(codec, w, h, out reason)`，
   `H264MaxDimension = 4096` / `HevcMaxDimension = 8192`（`:116-120`、`:713-750`），
   且**拒绝时理由比中心准**。
3. **被控端能力已在中心手上**：`GET api/remote/capabilities/{agentId}` → `RemoteCapabilityDto.SupportedCodecs`（值域 `RemoteCodecs`）。
4. 🔴 **`Packaging` 已就绪但无人驱动**：全仓 grep 后，除 `RemoteStreamProtocol` / `RemoteStreamHost` /
   用例之外**没有任何一处写它**；`RemoteSessionCommandDto` 里**没有** `Packaging`；
   `RemoteSessionCoordinator.cs:270` 构造 `RemoteStreamRequest` 时也没传
   ⇒ **第 6 步做好的两条路，从中心根本够不着**（永远走默认带内）。**第 7 步就是补这个洞。**
5. **前端视频路径仍是 MJPEG**（`web/src/remotePlayback.ts:837-843`：`new Image()` + `image.decode()`，
   无 `VideoDecoder`）⇒ 换挡是第 8 步；第 7 步**不许碰坏这条路**（它现在是自动回退终点）。

### 两条"零改动"结论（避免无谓的改动面）

- `RemoteSessionDto.Codec` 已存在，且由**首帧** header 填（`RemoteSessionRegistry.cs:822`）
  ⇒ 第 7 步**不需要**再加"协商结论"字段：加了就是**两处真相**，
  而它们**必然在"被控端改判了"的时候分叉**。
- `POST api/remote/sessions` 已在 `LicenseGate` 的 `Gated` 表（`LicenseGate.cs:189`）
  ⇒ 只改请求体，**不需要动授权分类表**。

### 五问（各带推荐）＝ §3.10 (d)

| # | 问 | 推荐 |
|---|---|---|
| 1 | 观看端能力怎么传 | **(A) 能力包**（`codecs: ["h264","hevc"]` ＋ `supportsInBand`）。⚠️ 要**限死成"中心真会用的那几个位"**，别做成"浏览器能力清单"（那会变成一份要跟着浏览器升级的表）。 |
| 2 | 探测失败 / 老前端没传 / `VideoDecoder` 不存在 ⇒ 回落目标 | **(A) 带内 H.264**（理由见上②） |
| 3 | 中心与被控端结论不一致时谁说了算 | **(A) 被控端裁决 ＋ 必须上报改判理由**（理由见上①） |
| 4 | `Packaging` 这一轮一起下发吗 | **(A) 一起**。"试两条前缀"**只能在建会话时决定**（中途改封装＝中途改码流的解读规则）。⚠️ 连带：**由浏览器定、中心只做透传＋回落留痕** |
| 5 | HEVC 这一轮算不算"可交付" | **(A) 只到"能下发"为止**。本机 2160×1440 ⇒ **HEVC 路径物理上无法端到端验**（它只在 >4096 才有意义），且本仓 HEVC 素材**只有按规格合成的那一份** ⇒ 说"已交付"就是编数字 |

### 本步不做（先说清楚）

**不做 WebCodecs 解码**（第 8 步）、**不做横幅**（第 8 步）、**不端到端实测 HEVC**、
**不动 `LicenseGate`**。⇒ **本步结束后看不到任何视觉变化**，
验收依据是**日志与 `codec` / `packaging` 三处留痕**。

**下一步 ＝ 等五问拍板后实施 §8 第 7 步。**

> ✅ **10-16 已拍板（五问全取推荐）并落地 ⇒ 见下面的 §11**（本节留作"当时为什么这么定"的回溯）。

---

## 11. M5.4-b §8 第 7 步落地（10-16）：**中心协商取交集**（17 ＋ 3 条新增用例）

> 本节的**前身是上面的 §10**（"方案立档、五问待拍板"）。**五问按推荐全取**，
> 本节记的是**落地**与**实施中撞到的两处真问题**。
> 设计细节的完整版在 `docs/M5.4b-实施方案.md` §3.10 (g)；这里只留"不许忘"。

### 11.1 拍板结果（五问全取推荐）

①**能力包**（`codecs` ＋ `Declared` ＋ `packaging`）；②回落**带内 H.264**（**不是** MJPEG）；
③**被控端裁决 ＋ 上报改判理由**；④`Packaging` **一起下发**（由浏览器定、中心透传＋留痕）；
⑤HEVC **只到"能下发"为止**（真实端到端留给第 9 步 / M5.4-c）。

### 11.2 🔴 形状：中心下发的是「偏好 ＋ 可接受集合」，**裁决在被控端**

交集有**三项**输入，第三项（**本次会话的分辨率**）**只有被控端在挑屏之后才知道**：

| 例子 | 中心的交集 | 被控端的现实 | 结果 |
|---|---|---|---|
| 浏览器 hevc、机器 hevc、屏 2160×1440 | `hevc` | 能编 | 能用，但**白付 HEVC 的兼容性风险** |
| 浏览器 h264、机器 h264+hevc、屏 **5120×2880** | `h264` | **h264 编不了**（封顶 4096），**hevc 能编** | 交集指向的**恰好不可行**，被排除的**恰好是唯一出路** |

⇒ 所以中心的产物**不是一个格式，是一个有序集合**（第 0 项＝偏好）；
**只许往"我做不到"的方向改判**（h264→hevc），**不许往"我能做但你不想"的方向偷偷升级**（会黑屏）。

**为什么"裁决 + 上报"不能省**：这是一次**正确的**偏离（本机没越出集合），但中心不知道发生了改判。
没有那句话，现场看到的是"页面写着 H.264、码流却是 HEVC"，而"哪一侧坏了"**完全无法判断**。

### 11.3 🔴 真问题一：`null`（还没上报）与 `[]`（明确无硬编）**处置相反**

`Decide` 第一版把 `agentSupported is null || agentSupported.Count == 0` 合成一支 —— 两者在业务代码里
**几乎总被写成同一句判断**，而正确处置相反：

| 输入 | 含义 | 正确处理 | 混掉的后果 |
|---|---|---|---|
| `null` | 这台机器**还没上报过**能力 | 回落 `["h264"]` | —— |
| `[]` | **明确报过、一种硬编都没有** | 回落 `["mjpeg"]` | "H.264 回落"必然在建编码器时失败 ⇒ **永远建不起会话** |

⇒ 判据只能是**显式的"报过没有"**（与 `Probed` 那条同族："没测出来" ≠ "不支持"）。
⚠️ 兼容：**集合非空本身就等于声明过**，`Declared` 只用来消解 `null` 与 `[]` 的歧义。
⚠️ **这条是用例抓出来的，不是 review 出来的** —— 第一版直接写错。

### 11.4 🔴 真问题二：`mjpeg` 不过硬编筛；且声明的必须是"**本页显示得了什么**"

- `SupportedCodecs` 是**硬件编码器枚举结果**，而 MJPEG 走 **GDI+ 软编、永远可用**。
  拿它筛，会把"这台机器没有硬编"错译成"连 MJPEG 都不能用" —— **那正是 MJPEG 唯一该被用上的场合**。
  ⇒ `NeedsHardwareEncoder(name)`：`h264`/`hevc` 真、`mjpeg` 假。
- 🔴 前端更隐蔽：`isConfigSupported('avc1…')` 为真**只说明浏览器能解**，**不代表本页会去解**
  （第 8 步之前页面只有 `new Image()`）。按"浏览器能力"声明 ⇒ 中心挑 H.264、被控端老实开硬编，
  而**本页什么都不做** ⇒ **画面不出来、两端日志全正常**。
  ⇒ `probeViewerVideo()` 在第 8 步之前**只声明 `mjpeg`**，由 `WEBCODECS_RENDERER_READY`（**开关**，不是探测结果）控制。

### 11.5 其余形状级决定（每条都能单独把事搞坏）

1. **协商必须放在"登记之前"**：协商失败是"确定做不到"，在账本上留一条就变成
   之后每次建会话被 `SessionBusy` 挡掉 ⇒ **一次失败变成永久故障**。
2. **整个集合交给工厂**，宿主不许先挑（`requested[0]` 在上表第二行那个反例上直接死）。
3. **只在 `Encode` 一级换格式**：`TryValidateGeometry` 的尺寸闸门在 `Prepare` 里**建 duplication 之前**，
   所以改判重试廉价且安全；`Screen`/`Capture`/`ColorConvert`/`Platform` 四级**与格式无关**，
   在那里继续试只会多几条没人能解释的失败。
4. **`CodecNote` 不复用 `Reason`**：`Reason` 是"这路会话为什么**结束**"，而它是"为什么没按首选做、
   但仍然**正常在推**" —— 语义相反。同理**不复用 `Codec`**（`Codec` 是首帧头里读到的**事实**）。
5. **`CodecNote` 刻意不做 `ReasonToKeep`**：它始终成立，而 `ReasonToKeep` 会在 requested→streaming 时
   把它清掉 —— 那正是观众最需要它的时刻。
6. **三句话**：照首选 ⇒ `null`（别在状态上报里占地方，否则真正该看的那次改判被淹没）；
   集合内改判 ⇒ 说清"做不到首选"＋"**没有越出**集合"；中心没说 ⇒ 点明"**本机自选**"。
7. **打通那个缺口**：`RemoteSessionCoordinator.cs:270` 原来没传 `Packaging`/`Codecs`
   ⇒ 第 6 步做好的两条路**从中心根本够不着**，`RemoteStreamHost` 永远走默认带内。**现已补上**。
8. **`EncoderCapabilityMapper` 加反向映射 `TryParseWireCodec`**（`internal`→`public`）：
   正反是一对，分开写必然漂，而漂掉的表现是"本机报了一个自己都不认得的格式名"。
9. **过渡期风险**：中心在"回落 H.264"那条分支要打警告"**强制刷新一次页面**" ——
   因为第 8 步之前的旧页面只渲染 MJPEG，回落 H.264 会让它**黑屏**。第 8 步之后自动消失。

### 11.6 用例（新增 17 ＋ 3）

- `tests/ControlServer.Core.Tests/RemoteVideoNegotiationTests.cs`（**新增 17 条**，6 组）：
  正常取交集与偏好顺序｜未知格式剔名｜大小写去重｜**`null` vs `[]` 处置相反**｜回落不带 HEVC/MJPEG｜
  只有 HEVC 的机器｜`mjpeg`-only 观看端在只有 HEVC 硬编的机器上仍得 `mjpeg`｜空表能力表＋`mjpeg`｜
  `null` 不是拒绝｜**两边都清楚无交集才拒绝**｜封装来自观看端且 `out-of-band` 回落带内｜
  只给 `codecs` 不置 `Declared` 也当声明过。
- `tests/ControlServer.Agent.Tests/RemoteStreamPackagingTests.cs`（原 8 → **11**，＋3 条端到端形状）：
  ① **整个集合到工厂**（顺序不能丢）＋ 集合内改判**必须带 `codecNote`**（两边名字都在、点明"没有越出集合"）；
  ② 照首选 ⇒ `CodecNote` **为 `null`**；③ 中心没指定 ⇒ `CodecNote` 点明"**本机自选**"。
  ⚠️ 为此给夹具加了两处接线：`NewHost(captureRequest:)`（让用例看见**工厂收到的请求**）
  与 `ScriptedSurfaceEncoder.CodecToReport`（让编码器能自称 HEVC）。

### 11.7 回归（10-16）

| 套件 | 之前 | 现在 |
|---|---|---|
| `ControlServer.Core.Tests` | 844 | **873** |
| `ControlServer.Agent.Tests` | 181 | **184** |
| `ControlServer.Server.IntegrationTests` | 527 | **528** |

全解决方案 **0 警告 0 错误**。前端 `vue-tsc --noEmit` 干净 ＋ `vite build` 成功（**先前端后后端**）。

### 11.8 本步留的坑（写清楚，不掩盖）

- ⚠️ **本步不产生任何视觉变化**（页面显示的还是 MJPEG 画面）⇒ 验收只能靠**日志与三处留痕**。
- ⚠️ **真实 HEVC 端到端仍是挂账**（第 5 问取 (A)）：只到"能下发/能改判/能上报理由"，
  **没有**跑过一次真 HEVC 码流的端到端 ⇒ 第 9 步 / M5.4-c（**8K 之前必须清**）。
- ⚠️ "默认带内"仍依赖"浏览器认 `avc3` 这个前缀"——**要靠第 8 步的 `isConfigSupported` 收口**，不许当已知。
- ⚠️ 第 8 步交付后**要把 `WEBCODECS_RENDERER_READY` 打开**；那之前它只会声明 `mjpeg`（第 7 步刻意如此）。
- ⚠️ 第 8 步必须按"**关键帧上 `(CodecString, Description)` 变了就重新 `configure()`**"写。

### 11.9 顺带更正的口径

§3.2 第 2 点原来写"探测失败 ⇒ **必须回落到 MJPEG**"—— **已被推翻**（就地加了 ⚠️ 10-16 更新）。
它在第 6 步之前写：当时 `Description` 能不能拿到还是未知。第 6 步之后，"带内 H.264"
是三种情况里**失败方式最干净**的一条（`isConfigSupported` 直接给布尔值）；
而 MJPEG 在 2160×1440 是**软编 40–80 ms/帧 ⇒ 12–25 fps**。
⇒ **把"不知道"翻译成"降到 20 fps 软编"，是为一次探测失败付长期代价。**

---

## 12. M5.4-b §8 第 8 步开工（10-16）：**方案立档 ＋ 现状核查**（**未改一行生产代码**）

> 完整方案在 `docs/M5.4b-实施方案.md` §3.11（(a) 事实 / (b) 核心冲突 / (c) 三条路 / (d) 五问 / (e) 改动面）。
> 本节只留"不许忘"。**四问已被拍板并落地 ⇒ 见 §13**（第 3 问事实已定、未单独占一问）。

### 12.1 🔴 核心冲突（本步最容易被低估的地方）

**抖动缓冲的"忙就丢最旧"与 H.264 的参考链直接冲突。**

| | 丢一帧的实际含义 |
|---|---|
| MJPEG | 少一张**独立**图，前后照解 ⇒ 天经地义 |
| H.264 | 丢一个 P 帧 ⇒ 它后面**所有**依赖它的帧都花，**直到下一个关键帧** |

而现行"丢"实现在 `dropUpTo`/`pumpDecode`（`remotePlayback.ts`）里 —— 那些对象的身份是
**解码前的帧** ⇒ **直接复用会把 H.264 打花**，而症状是"花屏"（最容易被误判成"编码参数错"，
于是排查方向直接反掉）。
⇒ **"解码"必须从 `RemoteVideoBuffer` 抽出来**，"能不能丢、怎么丢"交给实现回答（排期逻辑只有一份）。

### 12.2 🔴 一条"不补就黑屏"：TS 帧头缺两个字段

服务端 `RemoteStreamFrameHeader` 已有 `CodecString` / `Description`（`RemoteDtos.cs:854-865`），
而 TS `RemoteStreamFrameHeader` **只到 `dropped`**（`types.ts:2422-2450`）
⇒ `configure()` 拿不到码流串与 avcC。
⚠️ **症状是"运行期 `undefined`、编译不报错"**（多一个字段不影响类型兼容）—— 又一条"黑屏而日志全正常"。

### 12.3 ⚠️ `web/` 零测试基础设施

`web/package.json` 只有 `dev`/`build`/`typecheck`/`preview`，**无 vitest、无测试文件**
⇒ 本批**最复杂的前端逻辑（H.264 解码编排）不可回归**。
而"花屏"的三种根因（参考链断 / 参数集错 / 丢帧策略错）**肉眼分不出来**。
⇒ 是否补 vitest 列为第 5 问。**若不加，文档里要显式记一条"本步逻辑不可回归"。**

### 12.4 五问（带推荐）

| # | 问 | 推荐 | 一句话理由 |
|---|---|---|---|
| 1 | H.264 路上"忙"怎么办 | **(B) 不丢帧、只丢上屏** ＋ **(C) `reset()` 兜底** | `decode()` 必须照序全喂（保参考链），"丢"只发生在**上屏**那层 |
| 2 | 解码编排放哪一层 | **(C) 排期层留口 ＋ 解码抽接口** | 与第 7 步"整个集合交给工厂、宿主不猜"同族 |
| 3 | 探测结果与 `configure()` 串的关系 | **(A) 探测只定"要不要建这条路"** | 探测阶段拿不到真串（真串从首帧 SPS 反推） |
| 4 | `packaging` 探测怎么衔接 | **(B) 两条都试**（`avc3` 通过保持带内；只有 `avc1` 通过 ⇒ 改请求带外） | 唯一能让"Chromium 不认 `avc3`"时还用上硬编的路 |
| 5 | 要不要建 vitest | **(A) 加** | 花屏的三种根因肉眼分不出来 |

⚠️ 问 1 的连带：H.264 的丢帧计数**必须分成两个数** —— **没上屏**（画面流畅度）vs
**解码器被 `reset()` 重建**（真故障）。混成一个数会把"降帧"读成"解码故障"。

### 12.5 本步不做的

不做 HEVC 端到端（本机屏 2160×1440，>4096 那条路**物理上验不了**，留第 9 步 / M5.4-c）；
不动被控端（第 6 步）、不动中心（第 7 步）、不动 `LicenseGate`。
⚠️ 但**本步结束后应当第一次看到真正的 H.264 画面**。

> ✅ **10-16 四问已拍板（全取推荐）并落地 ⇒ 见下面的 §13**（本节留作"当时为什么这么定"的回溯）。

---

## 13. M5.4-b §8 第 8 步落地（10-16）：**前端 WebCodecs ＋ 真探测 ＋ 前端测试基础设施**

> 完整落地记录在 `docs/M5.4b-实施方案.md` §3.11 (g)。本节只留"不许忘"。

### 13.1 四问拍板全取推荐

① 不丢帧、只丢上屏（`reset()` 兜底）｜② 排期层留口、解码抽接口｜
③ 探测只定"要不要建这条路"（**事实已定**：`packaging` 建会话时定死）｜④ 两条前缀都试｜⑤ 加 vitest。

### 13.2 🔴🔴 三条"错了不会报错"的真问题（本节最值钱的部分）

1. **vitest 在受限环境（沙箱 / 受管构建机）会「假绿」** —— 它把转换后的模块写进**系统 temp** 再 import，
   并行写会撞 `EPERM: … open '…\Temp\…\ssr\<sha1>'`。
   **真正危险的不是那个报错，而是它的后果**：错误发生在**收集阶段** ⇒ "4 个用例文件只跑了 1～3 个"，
   而汇总行照样写 `Test Files 3 passed` / `Tests 48 passed` / `Errors 1`。
   ⇒ **假绿比没有测试更坏**（让你以为有回归网）。
   修法（**写进配置**，不是只挂命令行）：`pool: 'forks'` ＋ `poolOptions.forks.singleFork: true`，
   `cacheDir` 放项目内。判据是"**连跑三次都 4/4 文件、58/58 用例**"，不是"跑过一次绿"。
   ⚠️ 断言**一条没动** —— 单进程是**正确性**要求，不是性能调优。
2. **`RemoteStreamOversizeTests` 里那条红是「测试自带的竞态」** —— `ReadFramesAsync` 自己
   `await using` 客户端、**读完 want 帧就把管道关了**，而后面几条断言（`IsStreaming` 仍为真 /
   日志无"接收端已断开"）是在**断开之后**才读的 ⇒ 服务端下一次写失败、**如实**记一句断链。
   表现：**单独跑绿、整程序集一起跑红**（xunit 并行改时序）。
   ⇒ 修法不是放宽断言，而是**别在断言之前把管子拔掉**：客户端提到用例里建、用例里释放
   （**两个用例都要改**：`:64` 与 `:156`）。⚠️ **本步一行 C# 生产代码都没改。**
3. **切会话必须重建 `RemoteVideoBuffer`，不能只 `clear()`** —— 缓冲里住着**按上一路格式造的解码器**，
   只 `clear()` 之后会"拿 MJPEG 的解码器去解 H.264"，症状只是**花屏**。
   原来那条注释给的理由（"让 rAF 循环留着跑、省一帧启动延迟"）**其实不存在**：重建同样在第一帧上、
   同样立刻排期。⇒ `videoBuffer?.stop(); videoBuffer = null`。
   ⚠️ `stop()` 关解码器、`clear()` **不关**（`clear()` = "清空队列留着复用"），有专门用例钉住。

### 13.3 四条不可回退的设计决定（免得下一批"顺手简化"掉）

1. **`maxInFlight` 由解码器回答，H.264 是 `POSITIVE_INFINITY`** —— WebCodecs 的 `VideoDecoder`
   内部要缓存若干帧才有第一帧输出，**限深会在第一帧上死锁**。
2. **`feed` 不许是 `Promise<DecodedFrame>`** —— "喂了 N 帧只出 1 帧"是**常态**不是异常 ⇒
   `feed()` 立刻返回、结果走 `DecodedSink` 回调。
3. **`DecodedFrame.release()` 是合同、必须幂等** —— `VideoFrame` 不会被 GC。
   MJPEG 那个是空操作但**仍然要实现**（排期层不许知道"哪种解码结果要释放"）。
   面板里"一帧配不上队列项就反过来释放 `decoded`"那处是**显存泄漏点**（漏 GPU 内存，GC 不收也不报错）。
4. **`codecString` 前缀与 `description` 的有无必须自洽**（`avc3`/`hev1` 带内**不许**带；
   `avc1`/`hvc1` 带外**必须**带）⇒ 在**进解码器之前**挡掉。且**无 `codecString` 时不许手写默认串**
   （串错 ⇒ `NotSupportedError`，看起来像"浏览器不支持 H.264"）。

### 13.4 其余形状级事实

- **排期层一行未改**：`tickOnce` / `pumpDecode` / `dropUpTo` 三件事对两条路语义相同。
- **契约单独一个文件**（`frameDecoder.ts`）：四方都要引 ⇒ 挂 `remotePlayback.ts` 下会出现循环引用，
  且已实测踩到 `TS2459`（那个文件只是**转手** `FrameDecoderStats`，不是定义处）。
- `bytesToBase64` 必须用**全局** `btoa`（`window.btoa` 在 node 用例里没有）。
- **`api.ts` 一行未动**：问 4 取 (B) 的实现是改 `packaging` 的**取值**，字段第 7 步就已就位。
- 实际文件名与方案预览的三处差异：`h264Decoder.ts` → **`webCodecsDecoder.ts`**（H.264 **与** HEVC 共用）；
  多出 **`frameDecoder.ts`**；`api.ts` 未动。

### 13.5 用例与回归（10-16）

前端 `npm test` **58/58**（`webCodecsDecoder` 28 / `remoteCodecProbe` 10 / `mjpegDecoder` 10 /
`remotePlayback` 10，**连跑三次稳定**）＋ `typecheck` / `build` 干净（`index-JC2nh57P.js` 434.94 kB）；
`Core.Tests` **873** / `Agent.Tests` **184** / `IntegrationTests` **528**（⚠️ 第三次才拿到）；
全解决方案 **0 警告 0 错误**。

### 13.6 本步**没有**得出的结论（不掩盖）

🔥 **真机肉眼验收全在第 9 步**：H.264 画面第一次出现、指针在不在（第 5 步留下的最大缺口正好收在这里）、
延迟与帧率读数。**本步只保证"代码路径通了 ＋ 可回归"。**
⚠️ **HEVC 端到端仍属挂账**（本机屏 2160×1440）。
⚠️ 第 9 步若出现"崩溃 / 无法解释的花屏"，**第一件事是把泵钉到单线程**再谈别的。

### 13.7 🔴🔴 顺带挖出的**第二条"假绿"形状**：集成回归会「中止但打印已通过」（**新挂账**）

`ControlServer.Server.IntegrationTests` 三次里**两次整程序集没跑完**：

```
活动的测试运行已中止。原因: 测试主机进程崩溃
已通过! - 失败:  0，通过:  136，已跳过:  0，总计:  136      ← 只跑了 136 / 528
测试运行已中止。
```

三次实测：`136 中止` → `246 中止` → **`528 全绿`**。`--blame` 点名的用例**两次还不一样**
（都在 `AgentTelemetryApiTests`），blame 自己写着"此测试**可能**是(也可能不是)崩溃的来源"。

**已排除**：无残留进程 / 无端口占用（不是我强杀出来的）；近 60 分钟 Application 事件日志里
**没有任何** `Application Error` / `.NET Runtime` / WER 记录 ⇒ **静默退出，不是原生崩溃**；
全仓**零处**"按名字杀进程"代码；控制台那条是测试工厂**故意**设 `AttachConsole=true`
（输出被管道接管时那段代码什么都不做）。**本步零 C# 生产改动** ⇒ 不是本步引入。**根因未定位。**

> 🔥 **口径修正：「失败: 0」不算通过 —— 必须同时满足「总计 == 基线」。**
> 与前端那条 vitest 假绿**同一个形状**：错误发生在"跑不完"这一层，而汇总行只看"跑过的那部分"。
> ⇒ 第 9 步若还要跑全量，应把"总计 == 基线"做成**显式断言**，不靠人眼看。

---

## 14. M5.4-b §8 第 9 步**验收方案立档**（10-16）：端到端真机验收

> **状态：方案已立档，四问待拍板，未跑任何验收动作、未起任何进程、未改一行代码。**
> 文档侧 ＝ `docs/M5.4b-实施方案.md` **§3.12**。本节只留"不许忘"。

### 14.1 六条现状事实（详见 §3.12 (a)）

1. **产物新鲜度一半达标、一半是坑**：`dist/agent` 今天 **15:04** ✅（晚于被控端源码最新改动 14:07）；
   但 **`dist/win-x64/ControlServer.Server.exe` 是 10-15 15:36 的旧包**（早于第 7/8 步）
   ⇒ 从 `dist` 起服务端 ＝ **"新客户端 ＋ 旧服务端"**（铁律 13/14 的形状）。
   ⇒ **第 9 步开工第一件事：服务端走源码态 `bin/Debug`（`run.cmd`）或先重打。**
2. `deploy/smoke/` 九个场景（web/pairing/admin/agent/transfer/sync/browse/license/rbac）**没有 remote**。
3. 🔴 **指针那条链到此刻仍是"零真像素证据"**：`DxgiHardwareEncodeSmokeTests` 喂的是
   `surface.TextureHandle`（**裸桌面纹理**）；`DxgiCaptureSmokeTests` 只**打印**三态，
   dump 的 `dxgi-captured-frame.jpg` 是 `IsCursorIncluded=false` 那张
   ⇒ **现有夹具里没有一张图能回答"指针在不在"** ⇒ 要**另造"合成后落盘"这条路**。
4. 协商链路已闭合（`RemoteController` / `RemoteHub` ＋ 17 用例）；`WEBCODECS_RENDERER_READY = true`。
5. 授权不是拦路虎：远程端点已在 `LicenseGate.cs:189-192`（写）/`328-336`（读）分类。
6. 🔥 **"肉眼"不必由人来当**：执行者是多模态的，给图就能判读（指针在不在 / 有没有黑洞 / 花没花）。
   ⚠️ **但不能替掉人最终复核** —— 图是它挑的、标准也是它定的 ⇒ 判据写死在 §3.12 (b) 表里。

### 14.2 四问

| # | 问 | 推荐 |
|---|---|---|
| A | 怎么跑 | 单机自环起步（中心＋被控端＋浏览器同机），再固化成 `remote-e2e.py` |
| B | 谁动鼠标 | 先脚本驱动 `SetCursorPos` ＋ 截图留证，**人只复核** |
| C | 证据从哪来 | **两者都要**：宿主侧合成帧落盘（可逐像素查）＋ 浏览器截图（"人看到的"） |
| D | HEVC 边界 | 本步只到"能协商/能下发"，真实 >4096 端到端**记挂账**（8K 前必清） |

### 14.3 验收清单要点

🔥 ① **第一次看到真正的 H.264 画面**（页面统计里"解码器 产出>0、解不出=0、重建不涨"）；
🔥 ② **指针在**（宿主 `FramesWithCursor > 0` ＋ `CursorFailures = 0`，且观看页图里看得见）；
③ **深/浅底各一张**（`MASKED_COLOR` 的 XOR 不许出黑洞）；
④ **欠的受控实验**：光标停**文本区**（I 形 ⇒ `Monochrome`）与**桌面**（箭头 ⇒ `Color`）各采一次；
⑤ 全程不崩；花屏 ⇒ **先把泵钉到单线程**；
⑥ 落账时全量回归必须是 **「失败 0」且「总计 == 基线」**（§13.7 的新挂账）。

### 14.4 风险与回退（摘要）

动鼠标**会干扰正在用这台机的人** ⇒ 排在明确同意之后；起进程一律**便携/前台**态（不装服务）；
"崩溃/无法解释的花屏" ⇒ 先钉单线程；带外那条路解不动 ⇒ 先查 `avcC` 没写高档次扩展块（§7 挂账）。

### 14.5 本步不做

不改被控端/中心/前端的生产代码（真机暴露缺陷则**另开一轮**并重跑全量）；
不做分片、"只拷脏区"、静态降帧（那三件是 M5.4-c）；不假装"压满 8 MB"（要靠 4K/8K 的 IDR）。

### 14.6 🔴🔴 宿主侧落盘先行，抓到 **M5.4-b 的第一个真缺陷**：指针会被"内容帧"清掉（10-16）

新增用例 `DxgiCaptureSmokeTests.Pointer_is_composited_into_the_frame_with_real_pixels`
（`tests/ControlServer.Server.IntegrationTests/DxgiCaptureSmokeTests.cs`）。它做了三件此前**一件都没做**的事：
① 主动把光标挪到屏中央（不再"碰运气等指针可见"）；
② **每帧同时记 DDA 说的与 `GetCursorPos` 说的**（判"谁对"必须有基准）；
③ 用真实桌面读回的 BGRA ＋ 真实光标形状跑 `PointerCompositor`，数"矩形内 / 外各变了几个像素"。

**采样表（前 5 轮每轮抖光标 ±1px，第 6 轮起停手，只让屏幕内容自己变）：**

| # | DDA 结局 | DDA 说 | `GetCursorPos` 说 | |
|---|---|---|---|---|
| 1 | `NoDesktopUpdate` | **可见 1081,720** | 1081,720 | ← 纯指针帧：对 |
| 2–5 | `TimedOut`/`DesktopUpdated` | **可见** | 一致 | ← 光标在动：对 |
| **6–15** | **`DesktopUpdated`** | 🔴 **不可见 0,0** | **1081,720** | ← 🔴 **光标没动、内容在变：全错** |

⇒ **天然对照实验**：切换点正是"我停止抖光标"那一刻，错的从那之后开始。

**根因**：`WindowsDxgiScreenCapturer.TryReadPointer`（`:1005`）**无条件**读
`_frameInfo.PointerPosition` 并写回 `_pointer` 的 `X/Y/Visible`；而 DDA 的规矩是
**`LastMouseUpdateTime == 0` 时该字段不可读**（本机实测回 `0,0/Visible=0`）。
🔥 **同一份知识就写在同一个文件里**（`:388-389` 的 `LastPointerPosition` 注释），
只是 `TryReadPointer` 没照做 ⇒ 把一份好快照覆盖成坏快照。

**后果**：`WindowsGpuFrameEncoder.TryCompositePointer`（`:1063`）读的正是这个 `Pointer`
⇒ `TryGetRegion` 返回 false（"指针当前不可见"）⇒ **不合成**。
现场看到的就是：**用户不动鼠标、而屏幕在变（视频 / 动效 / 别人在操作）时，观看端的鼠标消失**；
一动鼠标又回来。**不报错、`CursorFailures` 不涨、日志里什么都没有。**

**修复建议**（属"另开一轮"）：只在 `_frameInfo.LastMouseUpdateTime != 0` 时更新 `X/Y/Visible`，
否则沿用上一份快照（形状缓存那条路本来就这么做，位置这条漏了）。

⚠️ **该用例现在是红的**（`invisibleWhileShown` 实测 10 次违规）—— **故意的**：
把缺陷钉成回归网。⚠️ 也因第一条判据先失败，**像素级合成那一半这次没跑到** ⇒ 第 5 步缺口**仍未关闭**，
但第一次有了会红的守卫。

### 14.7 ✅ 缺陷已修 + 第一次拿到指针真像素证据（10-16）

**修法**（`WindowsDxgiScreenCapturer.TryReadPointer`，`:1003`）：加一条
`var pointerMoved = _frameInfo.LastMouseUpdateTime != 0;`
—— 只在它为真时更新 `X/Y/Visible`，否则**沿用上一份快照**（把"上一份的位置"先取出来，
因为 `TryReadPointerShape` 返回的 `fresh` 里 `X/Y/Visible` 是**占位值** 0/0/false）。
⚠️ 顺带把**方法自己的文档注释**改了：它原来写"位置**每帧都取**，不能沿用上一帧的 ——
那是'指针不动'的直接成因"，**恰好说反**。不改掉它，下一个人会照着它把缺陷改回来。

**验证**（同一条用例，转绿）：

| | 修复前 | 修复后 |
|---|---|---|
| 15 帧里 `invisibleWhileShown` | **10** | **0**（15 帧全部 `DDA 可见`，与 `GetCursorPos` 逐帧一致） |
| 矩形内变了几个像素 | **0** / 1088 | **131** / 1024（另一次 144 / 1088） |
| 矩形外变了几个像素 | **144** / 3109312 | **0** |
| 用例 | 🔴 失败 | ✅ 通过 |

🔥 **第 5 步欠的账第一次有了真像素证据**：新增两张**同帧**对照图
（`.tools/m5.4-capture/dxgi-pointer-before.jpg` / `dxgi-pointer-composited.jpg`），
用 `.tools/m5.4-capture/_zoom.py <X> <Y>` 裁到指针那块放大 5 倍 ⇒ `pointer-ab-zoom.png`：
**左（不合成）红框里是普通桌面，右（合成后）红框里清楚地是鼠标箭头**。
⚠️ 脚本坐标**必须**从用例输出的「合成矩形」抄（每次跑都会变，取决于光标当时在哪）。

⚠️ **仍未被证明的一刀**：用例走的是"CPU 裁剪 → `Composite` → 贴回"，
生产走的是 **GPU `CopySubresourceRegion` ＋ `D3D11Box.From(region)`** 读回子矩形
（`WindowsGpuFrameEncoder.TryReadPointerRegion`，`:1109`）。
⇒ "**入参给对了，合成就是对的**"已证；"**那一刀裁在对的位置**"未证。
⚠️ 那种错误的表现是"**指针位置不对**"而**不是报错** ⇒ 正好由第 9 步"指针跟着动"覆盖。

### 14.8 🔴 同轮撞到的两件"夹具 / 环境"问题（**不是**产品缺陷）

**① 把整屏缓冲配矩形尺寸传进 `Composite` ⇒ 看起来像"合成整个坏了"。**
`PointerCompositor.Composite` 的 `background` 要求"**你给我的缓冲就是那块矩形**"
（`backgroundPitch == regionWidth * 4`；看它内部 `baseX = originX - regionX` 与
`by * backgroundPitch` 的用法即知）。第一版用例传了**整屏** ⇒ `baseX/baseY` 被算成
"缓冲左上角"的 (0,1) ⇒ 指针被画到**屏幕左上角**去。症状「矩形内 0 个变、矩形外 144 个变」，
而 **144 ≤ 32×32 里不透明像素数**，数字自洽 ⇒ 极容易被读成"坐标算错了"。
⇒ 用例改成"**裁剪 → 合成 → 贴回整屏**"，`insideChanged > 0` 与 `outsideChanged == 0`
两条判据都保住。
> 🔥 **教训：断言全红时先怀疑自己的入参，再怀疑被测对象** —— 尤其当"错误的数字"
> 恰好能自洽地解释成另一种成因时。

**② `SetCursorPos` 回 `true` 也可能根本没移动光标。**
实测：`SetCursorPos(1080,720)` 返回 `True`，紧接着 `GetCursorPos` 回读 `1692,1173`（原位置）；
下一次运行**同样的调用却生效了**（合成矩形正落 `1080,720`）。根因未查明。
⇒ **`SetCursorPos` 的返回值不能当真，一切以 `GetCursorPos` 回读为准。**
它与「构建报成功而产物是旧的」「集成测试打印已通过而只跑了一半」**同一个形状**：
**回了成功 ≠ 真做成了**。⇒ 用例的判据**不许**依赖"光标已被挪到已知位置"（现在都以实时回读为准）。

### 14.9 回归（10-16，全部**新产物**）

`Core.Tests` **873/0**、`Agent.Tests` **184/0**、`Server.IntegrationTests` **529/0** —— 三个都
**「失败 0」且「总计 == 基线」**（529 ＝ 528 ＋ 本步新增的指针合成像素用例）。
⚠️ 集成那条走的是 **`-p:OutDir=.tools/iso-build/` 隔离输出**：另一路的
`ControlServer.Server` 进程（16:49:40 起）**锁着** `src/ControlServer.Server/bin/Debug/net8.0/`，
报 `MSB3021/3027`。**不杀别人的进程**，改用隔离输出绕开（这条已进 `MEMORY.md` 构建铁律）。

### 14.10 M5.4-b §8 第 9 步·**浏览器半边**（10-16 晚）：**画面 ✅ ／ 指针 🔴**

> 文档侧 ＝ `docs/M5.4b-实施方案.md` **§3.12 (j)**；脚本 ＝ `deploy/smoke/remote-e2e.py`（第十个冒烟）。
> 本节只留"不许忘"。

**① 内核能力必须先量**：playwright 自带 Chromium ＝ 开源构建**不含 H.264** ⇒ 必须 `channel="msedge"`。
Edge 155 实测：`avc1.42E01E/4D401E/640028/640034` ＋ `avc3.42E01E/640028` **全 true**；
`hvc1.*`/`hev1.*` **全 false** ⇒ 🔴 **HEVC 是内核不给软解**（不只是">4096 本机屏验不了"）⇒ 挂账口径更正。

**② 四道"错了不会报错"的坑**（详表见日志 #37 §3；每条都只能靠"中心系统日志 ＋ 两侧进程日志"对时间轴）：

| 坑 | 形状 |
|---|---|
| `/api/agents` 不是数组 | 实返 `{items,total,online}`（写操作又是 `list.items`）⇒ `isinstance(body,list)` **静默永远空**；在线硬判据＝**`connected`** |
| 会话 1~2 s 消失 | 假象；实为 **20 s** 后 Sweeper 按「**没有任何状态上报**」清掉（`RequestedTimeout=20s`）⇒ 加 `dump_remote_logs()` 才看见 |
| 只起 `Agent.Service` | 🔥 协商齐全、状态停 `requested` —— **抓屏＋编码在 `Agent.Tray`**（服务跑 Session 0，**没桌面**） |
| `ExecuteUdpCommands=false` | 客户端只有一行 Warning、**中心侧完全没有** —— 它是**总闸**，**远控建流一起挡** |

**③ ✅ 画面成立**：`state=streaming` · `codec=h264` · 首帧 **205286 字节** · 解码统计 **16/0/0** ·
`viewer-h264.png` 是真桌面 ⇒ **§8 第 9 步「看见画面」关闭**。

**④ 🔴 指针未合成**（硬结论）：光标不动时画面 **0 px** 变化、屏幕正中心定点 ±45 px 小框差异 **0/0 px**、
`pointer-zoom-at-p1.png` 左右**完全一样**。映射已核对（930×621 与 2160×1440 宽高比同为 1.5，
`GetCursorPos` 回读在中心）。
⚠️ 第一版判据曾"假 PASS"：bbox 圈到的是**一个窗口的消失**（桌面真实变动）⇒ **必须定点裁剪放大**
（12×20 的指针缩到 930 宽只剩 4~5 px）。
⚠️ `SetCursorPos` **返回 true 也可能没移动** ⇒ 只信 `GetCursorPos` 回读。
🔥 **下一步入口（不许猜）**：`WindowsGpuFrameEncoder.Describe()` 的
「指针合成：画上 X 帧 / 失败 Y 帧（最近一次没画上：原因）」**无任何生产调用点**
⇒ **先接出口**（开流/收流打一行，或进 `AgentStats`）再看 `_cursorNote`。
（怀疑方向：`TryReadPointerShape` 只在该帧 `PointerShapeBufferSize>0` 时拿形状，DDA 只在形状真变时给字节
⇒ `_pointer` 可能**一直停在 `None`**；`WindowsDxgiScreenCapturer.cs:1033-1037` 已预见。）

**⑤ 夹具缺口**：深/浅底受控实验**未做** —— 本机 venv **无 tkinter** ⇒ 原标记窗方案落空。
**⑥ 顺带发现**：观看页顶部「M5.1 MJPEG 脚手架」横幅**文案已过时**（挂账）。
**⑦ 产物**：`.tools/remote-e2e/`（`viewer-h264.png`、`pointer-a/b/c*.png`、`pointer-diff.png`、
`pointer-zoom-at-p{1,2}.png`、`server.log`、`agent.log`）。
**⑧ 本步未改一行生产代码**；指针缺陷**另开一轮**。

### 14.11 🔴🔴 指针另开一轮（10-16 深夜）：**上一轮的结论是错的** —— 不是"没合成"，是**那一刀把整屏写坏了**

> 文档侧 ＝ `docs/M5.4b-实施方案.md` **§3.12 (k)**（含 A/B 修法对照表）。本节只留"不许忘"。

**① 先接出口（三处，都是"让事实可见"，不是修缺陷）**

| 位置 | 改动 |
|---|---|
| `Platform.Abstractions/RemoteDesktop.cs` | `IVideoFrameEncoder` 加 `string? CursorDiagnostics => null;`（默认实现 ＝ 这条路不合成指针） |
| `Platform.Windows/WindowsGpuFrameEncoder.cs` | 实现它；`Describe()` 那一格改成**同源**；新增 `_cursorLastInput` 记**最近一次合成的入参** |
| `Agent/Tasks/RemoteStreamHost.cs` | 推流循环**每 5 秒**一行（`CursorLogPeriod`）—— ⚠️ **必须是周期**，开流那句在一帧都没编之前 |

🔥 **"没有出口"的真正原因**：`Describe()` / `CursorNote` 只长在**类**上，而宿主（Agent 库）**只看得到接口**
⇒ 结构上谁也拿不到 —— 不是"忘了调用"。**这与 `DiscardedFrames` / `LastFailure` 是同一条理由。**

**② 拿到的事实**：`画上 141 帧 / 失败 0 帧`，入参逐项正确 ——
`指针 1800,1100 形状 32×32 Color（可见，形状字节 4096）⇒ 矩形 1800,1100..1832,1132`，
挪光标时位置**跟着走**（300,300 → 1800,1100，回读确认）。⇒ 🔴 **"指针没有被合成"被推翻。**

**③ 真相（新用例按像素判死）**：`D3D11PointerOverlaySmokeTests`（**同一帧内前后对照**：
转一次 → 读回 Y → 叠 64×64 纯洋红 → 再读回），实测 **矩形内 56.5 / 矩形外 183.00**
⇒ **叠加写进去了，但把整屏都写坏了。**

**④ ~~根因（强假设）~~ ⇒ ⚠️⚠️ 这一段是错的，已由 14.12② 更正（照它去修会白跑一轮）**：
VideoProcessor 声明**输入＝整屏**（`VideoProcessorContentDesc.For(2160,1440,60,1)`），
叠加喂的却是 **64×64**，**且从未更新声明**（`VideoProcessorSetStreamFormat` 一次都没被调用）
⇒ `Blt` 回 `S_OK`、输出未定义。
🔥 **整屏那趟"输入＝声明"天然匹配 ⇒ 这个 bug 藏了整整一个里程碑**；
而上一轮那条 CPU 版用例**不过 VideoProcessor**，全绿也证不了这一刀。
🔴 **实际真因不是这个**：把尺寸改成恒等后**结果一字不变** ⇒ 真因是
**`VideoProcessorSetOutputTargetRect` 从来没设过**（Blt 把没被 stream 覆盖的部分全填背景色）。

**⑤ 修法（未实施）**：**A** 叠加输入改成整屏纹理（位置交给两个 rect，显存多一块画面尺寸 BGRA／4K 33 MB）｜
**B** 给小纹理单建一套 enumerator ＋ processor ＋ output view（按 region 尺寸缓存）。
**不取 C**（调 `VideoProcessorSetStreamFormat`）：本机无 `um/d3d11.h` 逐条对数，而本仓**因猜槽位崩过一次**。

**⑥ 顺带**：`deploy/smoke/remote-e2e.py` 加 `--dwell SEC` —— ⚠️ 必须留在 `http_probe`
**内部**：它一看到 `streaming` 就 `DELETE` 会话，实测推流循环**只活 0.6 秒**（在外面等＝白等）。
**⑦ 重编了 agents 侧** ⇒ 顺带把另一路"客户端Server 显示名"的源码一起编了进去（那一路可编译、0 错）。

### 14.12 ✅✅ 指针**修好了**；而且 14.11 的根因判断**也是错的**（10-16 深夜续）

**① 修法 A 实施了，但它不是解药。** 按 A 改完（`_pointerTexture` 建成**画面尺寸** ＋ `D3D11Box`
写到 region ＋ 源/目标矩形都取 region），同一个用例**结果一字不变**（内 56.5 ／ 外 **184.00**）
⇒ **「输入尺寸 ≠ 声明尺寸」被排除**。A 保留（契约恒等是对的、且只碰已验过槽位），但**不是真因**。

**② 真因：`VideoProcessorSetOutputTargetRect` 从来没设过。**
`VideoProcessorBlt` 的语义 ＝ **target rect 之内、没被任何 stream 的 dest rect 覆盖的部分，
一律填背景色（默认黑）**：

| | target rect | stream dest rect | 空白 | 结果 |
|---|---|---|---|---|
| 整屏那趟 | 未设 ⇒ **整个输出** | **整屏** | **无** | ✅ 一直正常 |
| 指针那趟 | 未设 ⇒ **整个输出** | 只有指针那一小块 | **整屏其余** | 🔴 **全填黑** |

⇒ 「矩形外 184.00」＝ 桌面平均亮度**被抹成 0**。🔥 **整屏那趟"零空白" ⇒ 藏掉整整一个里程碑**，
也是 14.11④ 判断失误的成因：**盯着"输入尺寸"，没盯"输出的覆盖范围"**。

**③ 槽位是推导的，不是猜的。** `IUnknown` 占 `0..6`（含 `GetDevice`/`GetPrivateData`/
`SetPrivateData`/`SetPrivateDataInterface`），紧接 decoder 那 6 个占 `7..12`，然后 `VideoProcessorSet*`
**打头的就是它** ⇒ `13 = SetOutputTargetRect`、14 = BackgroundColor、**15 = ColorSpace（本表早就在用）**
✅ 同链自洽。⚠️ 错位也不崩（13~19 签名同形），**验证落在像素上**。

**④ 验证（三道全绿）**：用例 内 56.5 ／ **外 184.00 → 0.00** ／ **复位后整屏重转漂移 0.11**；
端到端「离开 P1 ⇒ 那块有变化」**0 → 73 px**、「到达 P2」**0 → 19 px**、「不动」仍 **0 px**；
**人眼复核 `pointer-zoom-at-p1.png`：左（P1）白色箭头清晰可见、右（P2）为空** ✅

**⑤ 顺带**：`ResetStreamRects` 扩建为 **`ResetOutputRects`**（output target rect **一并复位** ——
整屏那趟依赖它处于"未设"状态，残留会让整屏转换**只写一个角**）；用例加第 ⑦ 段钉它，
且**先自检"桌面静止"前提**，不成立就**如实跳过**（不造假 FAIL）。

**⑥ 回归与两条诚实交代**：`Core.Tests` 873 ✅ ／ `Agent.Tests` 184 ✅ ／
`Server.IntegrationTests` **530** ✅（529＋本步 1 条）／ 前端 vitest **83** ✅。
🔴 ① 集成**第一次跑是「529 通过 / 1 失败 / 530」**，第二次才全绿 ⇒ **有一条 flaky，没抓到是谁**
（详情抓取管道没接上）⇒ **下次跑集成回归必须先落盘完整输出再筛**。
⚠️ ② 前端基线我记的 **58 是过时的**，实测 **83**；6 个测试文件全是 10-16 建的
（旧值应为第 8 步 58 ＋ 第 9 步浏览器半边的新增）。

### 14.13 ✅✅ 浏览器半边**收尾**（10-16 夜）：深/浅底、形状类型、读数、被控端帧率 —— 22/22 ＋ 12/12（判据升级后 **23/23**）

**① 深/浅底：换了一条零副作用的路。** 原计划"临时换纯色壁纸"**实测在本机不生效**，
而且是"回了成功 ≠ 真做成了"的典型：`SystemParametersInfoW(SPI_SETDESKWALLPAPER)`
返回 **True**、`SPI_GET` 读回**也变了**，但用 PIL `ImageGrab` **直抓屏**比对，
屏幕主体亮度**没动**（238.3 → 238.5）。
⚠️ **险些误判**：远控侧那两张壁纸截图**完全一样**（均值 / 标准差 / 最平块位置逐项相同）——
那不是"桌面被窗口铺满、找不到平坦块"，而是**画面根本没有变化**；同样两个数、两种结论，
只有把两张图**并排比对**才分得开。
⇒ 改用**零副作用**办法：在**真实桌面**里找"确凿很暗的平坦块"与"确凿很亮的平坦块"
（`find_flat_zones`；**块内标准差 ≤ 6** 是必需项 —— 文字密布的块看着均匀、其实噪声极大）。
实测：暗区亮度 **61.1**（标准差 1.7）／ 亮区 **254.5**（0.5）；深底 **192 px** ／ 浅底 **69 px**；
两块底色确实分开（63.4 vs 252.8）。
⚠️ 覆盖边界：本机 DDA 给的是 `Color` ⇒ 验的是"**Color 的 alpha 混合**在两块底色上都正常"，
`MASKED_COLOR` 的逐位 XOR **不在**真机覆盖内（由 `PointerCompositorTests` 兜着）。

**② 形状类型（复核，不是新发现）。** ⚠️ 第 5 步的真机实测**已记录过**两种类型都出现过
（§8 第 5 步那条："不是这台机器是哪种，是当时活跃光标是哪种"）。本步是**端到端链路上重确认**，
补上两条当时没有的证据：字节数自洽 ＋ 热点坐标自洽。
做法：**一个窗口内上半文本区 / 下半空白区**（`--headful`；I 型光标只出现在**窗口里的**文本区）。
实测：空白区 `Color` 32×32 字节 **4096**（＝32×32×4）／ 文本区 `Monochrome` 32×32 字节 **256**
（＝32×32×2bit÷8）⇒ **两条分支各被真机走到一次**。
🔥 **把一处"看似错位"解释掉了**：脚本设 `(450,170)`、被控端报回 `442,161` —— 差 `(8,9)`。
**那是热点**：DDA 报回的是**位图左上角**，`(442+8, 161+9) = (450,170)` 精确等于目标
（`Color` 那次热点 `0,0`，报回零偏差）⇒ 已做成**硬判据**。

**③ 读数**：中心实测帧率 4.0~4.8 fps（与"帧数差 / 时间差"算出的实际速率差 10~20%，自洽）／
单帧 ≈ **0.6 KB** ／ 丢帧 **1**（建流初期排队溢出，**之后不再增长**，占比 2.0%）／
**帧时延 172~192 ms** ／ 解码器 **解不出 0、重建 0**。
⚠️ 三条口径：① 帧时延**依赖两侧时钟一致**（页面用 `Date.now() − capturedUnixMs`）——
单机自环下两侧同源、可信，但**不能**据此说"跨机时钟同步可用"；
② 丢帧判据**不取「== 0」**：实测序列就是 `0,1,1,1,1,1,1,1`，拿"首末比较"会误判成"还在涨"
⇒ 改判"**后半段不再增长**"＋"占比 < 10%"；
③ **定点小框还不够，必须再扣噪声** —— P1（屏幕正中心）那一框**静止时也有 123 px 在变**
（编辑器里的光标在闪 ＋ 文字区的高对比压缩噪声），而"指针消失"的信号只有 198 px，
两者**混在同一框里** ⇒ 任何"信号 > 噪声×k"的判据都会误杀
⇒ 新增 `region_signal_excl_noise`（先把"无端在动"的像素扣掉），三个数一起报；
同时把"移动后全图变化 > 静止 × 3"那条**降级为说明文字**（真实桌面上它不成立）。

**④ ✅ 已修（10-16 深夜）：页面「被控端实测帧率」恒 `—` —— 改成"推流期间每秒上报一条"。**
原现象：会话活着期间中心 `agentStats` 恒为 `null`。原根因＝整路会话里**只有终态那条**上报带计数
（"进入 streaming"那条刻意 `withCounters: false`，注释写的是"计数留给**终态**上报"）——
⇒ 中心手上"被控端发了多少"在会话进行中是**空的**，而那正是"它发得多 / 我们收得少"
（治网络）与"它发得少 / 我们也收得少"（治编码）**唯一的分界线**；只给一个数时两者长得一样。
🔥 **只把 `withCounters` 翻成 `true` 是不够的**（下一个人最容易想的就是这一招）：
宣布"我开始推了"那一刻 `Record` 还没跑（它在这一帧送出去之后才跑）⇒ 计数**必然是 0**
⇒ 页面从 `—` 变成 `0 fps`，**一样是错的**。缺的不是数字，是**周期性**。
修法：`PumpAsync` 起一条**每秒一次**的上报（**复用** `ReportChannelStatesAsync` —— 与两条通道状态
共用同一套字段拼装，那十几个位置参数少传一个就整体错位而编译器不报），
`RunAsync` 包进 `try/finally`：**先取消、`await` 干净，再报终态**。
⚠️ 顺序反了 ⇒ 迟到的 `streaming` 盖在 `stopped` 之后 ⇒ 页面钉在"还在推流"且**不会自己好**。
⚠️ 中心侧另有一道**独立免疫**：`ApplyState` 里 `if (State == Requested) State = Streaming;`
—— 状态**只许往前**，迟到的 streaming 复活不了已停的会话。
✅ **当初"要一起评估"的 `Sweep` 已评估完，互不干扰**：巡检对**已推流**会话看的是
`LastFrameAt`（**帧**），"20 秒"那条只作用于卡在 `requested` 的会话且用 `RequestedAt`
（**上报不会重置它**）⇒ 周期上报既不会被误杀，也不会把"托盘没跑"的假会话喂活。
✅ 回归网 `RemoteSessionReportTests`（2 条，`Agent.Tests` 184 → **186**）：① 带非零计数的
streaming 上报**不止一条**（周期性 ≠ 一次性 —— 只报一次等于把"活数"变成"第一秒的定格值"，
**比 `—` 更坏**，因为它看着正常）；② 第一条 streaming 上报计数**必然是 0**；
③ 接流泵收完场后**一条都不再来**。
⚠️ 写 ③ 时**第一次就踩了"测试自带的竞态"**（与 `RemoteStreamOversizeTests` 那次同型）：
停止路径上**有两条** `stopped`（`StopAsync` 一条、泵自己收场再一条），等第一条会让
**正常收尾**被当成"迟到的周期上报" ⇒ 改成**精确等泵那条**（两条的**原因文案不同**）。
**没有放宽断言。** 冒烟脚本那条判据同时从"明示记录"**升回硬判据**（22 → **23 项**）。
端到端实测：被控端 avg **4.3 fps**、页面卡片 **4.5 fps**、`framesSent=53` / `framesReceived=54`。

**⑤ 顺带纠正**：「深 / 浅底各一张」此前记为"本机 venv **无 tkinter** 所以欠着" ——
现已用零副作用办法做完，**tkinter 不再是障碍**。

---

### 14.14 ✅ 修掉「会话活性判据把**静止画面**判成断线」（M5.1 遗留缺陷，10-17 凌晨）

**怎么发现的**：不是复现，是**读**出来的 —— M5.4-b 收官后按例做"下一批开工前核查"，
读 `RemoteSessionRegistry.Sweep` 的**判据来源**时发现：**三处说明写着同一句话，而代码没实现它**。

| 说明（三处，措辞一致） | 代码实际做的 |
|---|---|
| `IdleTimeout`：「不再收到帧、**也没有状态上报**时，多久判成断了」 | `last = LastFrameAt ?? StartedAt ?? RequestedAt` |
| `RequestedTimeout`：「多久还没等到**任何**状态上报就判没起来」 | `now - RequestedAt > 20s` |
| `Sweep` 自己的文档：「清掉已断的（**帧停了、状态也不再上报**）」 | 同上 |

**触发路径（产品主线、必现）**：DDA 是**增量投递** ⇒ 桌面没变就不给新帧
（被控端日志自己写着「这是正常现象，**没有丢帧**」）⇒ 中心 `LastFrameAt` 停住
⇒ **15 秒后判死** ⇒ `Close` 后被控端下次推帧收 `Rejected` ⇒ **整路结束**；重开 15 秒后再死。
🔥 **"展厅里定格在一张图上的机器"就是这一台**；而 M5.1 起每次冒烟屏幕上都有东西在动
（光标在闪/鼠标在动/任务栏时钟在跳）⇒ **从来没有触发过**。

**修法**：判据收成**一个字段** `Entry.LastAgentAt`（= 最近一次收到这个被控端的**任何上行**），
`Observe`（帧）与 `ApplyState`（状态上报）各刷新一处；`Sweep` 两个分支都改用它
（`requested` 分支的计时起点从 `RequestedAt` 改成"最后一次回话"）。
🔥 **为什么不是"各处补一个 if"**：这条链路上还有**第三条上行 = 音频**，而它**刻意不算**
（没声卡的机器照样在推画面；只有声音在走也不该让死会话赖着走）
⇒ 做成字段 + **要求每处新上行明确表态**，下一个加上行的人会**看见**这个字段。

⚠️ **取舍（写明）**：修后"服务活着在回话、但托盘那条流**真卡住**"不再被自动清掉。
**故意换的** —— 协议层上**「桌面静止」与「托盘卡住」长得一模一样**（都是没帧、状态照报），
而前者每 15 秒毁一次会话、后者只是画面冻住（页面读数会明说 0 fps，用户还能自己点结束）。
真要分开得让被控端**上报「本机桌面静止中」**（它早知道 —— `idleTicks`）⇒ 挂账。

⚠️ **顺带记一条不一致**：中心 `RequestedTimeout`（20s）vs 被控端 `StartupFrameBudget`（30s）
—— 中心可能在被控端自认"还在预算内"时先把它清掉。**只记录、不改**（要先统一"多慢算太慢"）。

**回归网**：新增 `tests/ControlServer.Server.IntegrationTests/RemoteSessionSweepTests.cs`（**5 条**），
**修前 2 红 / 修后 5 绿**。集成 530 → **535**。（`Sweep` 此前**一条用例都没有**。）

🔥 **用例教训（值得升级成通用规律）**：**"时间只有一半能注入"** ——
`Open(now:)` / `Sweep(now:)` 能注入，而**事件自己的时刻**（`Observe`/`ApplyState` 里的 `UtcNow`）
注入不了。第一版用例写的是"40 次状态上报"（其实是**连续的微秒级调用**）+ `Sweep(t0+40s)`
⇒ **修完仍然红 —— 红的是用例，不是代码**（把"回话"与"帧"两个时刻**一起**推旧了，
测的就不是要证的那件事）。
⇒ 凡是「**两个事件时刻的相对位置**」这类判据，要么**真的等**（A 用例等 18 秒，余量 14 秒，不脆），
要么先给中心加一个可注入时钟（`TimeProvider`）。
**"我构造了 40 秒" ≠ "过了 40 秒"。**

⚠️ **顺带抓到一条间歇失败的"身份"**（此前只记过数量）：全量集成 `535` 里
`534 通过 / 1 失败`，失败的是
**`DxgiHardwareEncodeSmokeTests.Hardware_encoder_turns_a_captured_frame_into_a_decodable_bitstream`**
—— 自述是"**编成功、码流也合法**（419 KB / Annex-B / SPS·PPS 齐 / 2 个关键帧），
但本机 `Microsoft H264 Video Decoder MFT` **拒绝接收输入、又不产出输出**（卡住）"。
**单独复跑 2/2 通过** ⇒ 机器态（解码器 MFT / GPU 被占），**与本次改动无关**。
⚠️ **不能断言它就是此前那条未定位的 flaky**（那时名字已经丢了），但性质吻合。
⚠️ 能定位的原因：**输出先落盘再筛**（`verbosity=minimal` + 重定向）—— 失败名与全部自述都在。

---

### 14.15 📄 M5.4-c **方案立档**（10-17 凌晨）—— 未改一行生产代码

**产物**：`docs/M5.4c-实施方案.md`（451 行 / 12 节）。范围＝**①分片 chunking（硬前置）
②静止降帧＋上报「桌面静止中」③运行时自适应**；**多屏 / ROI / 剪贴板 / 分块编码 tile → M5.5**。

**① 先裁定了一处文档口径不一致（这是本轮的起点）**
「M5.4-c 到底含什么」在三处写得不一样 ⇒ 立档第一件事是裁定，并**就地更正 M5.4-b 文档头那行**
（原文「M5.4-c（码率/帧率自适应、多屏、ROI）」是**写宽了**）+ 更正 M5.4-b §3.12(f) 第 2 条
（原把「只拷脏区」也算进 M5.4-c）。
⚠️ **顺手点名一个用词陷阱**：**「分片 chunking」（一帧跨多条消息，改的是传输层）
≠「分块编码 tile」（一幅图切块各自独立编码，改的是编码层）** —— 中文只差一个字。此后**一律写全**。

**② 帧路径六跳，与那个被默默依赖的假设**
`「一条消息 = 一帧」被六跳里的每一跳依赖，而只有第 3 跳（服务→中心的 SignalR）真的挡不住 8K`：

| # | 跳 | 上限 |
|---|---|---|
| 1 | 抓屏+编码 | — |
| 2 | 托盘→服务（本地管道 `RemoteStreamProtocol`） | `MaxPayloadBytes` ≈ **5.75 MiB** |
| 3 | 服务→中心（SignalR，**中心 `MaximumReceiveMessageSize` = 8 MiB**） | 🔴 **那堵墙：越限＝掐断整条连接**（心跳/遥测/配置/音频都在上面） |
| 4 | 中心→每会话 `Fanout` 队列 | 容量 **1** + `DropOldest`（**不回报**） |
| 5 | 中心→浏览器（SignalR，服务端→客户端） | ⚠️ **JS 客户端 8.0.29 没有 `maximumReceiveMessageSize`**（那只存在于服务端/.NET 客户端）⇒ **这条路上没有同一堵墙** |
| 6 | 前端 `push`→解码 | `VIDEO_QUEUE_LIMIT = 12`，满则丢最旧 + `breakChain()` |

**③ 分片设计：终止在中心（方案乙），不做端到端**
- 🔴 **甲（端到端：被控端分→浏览器合）必须否掉**：中心的 `Fanout` 丢弃单位是**"一条消息"**
  ⇒ 它会丢掉**同一帧的中间一片** ⇒ 前端拼出**长度对、内容错**的码流 ⇒ **花屏，且中心与前端都不报错**。
  要救就得改队列的丢弃单位 —— 而**那等于中心已经在重组了**。
- 🔴 **丙（只抬 `MaximumReceiveMessageSize` 到 64 MiB）也否掉**：它是**整条连接**的上限。
- ✅ **乙（中心重组，再按整帧入队）**：跳 4/5/6 **零改动**、前端**零改动**、`Latest` 语义不变。
- 🔥 **只有"装不下"的帧才分片**，`ChunkCount == 1` 仍走老路 `PushRemoteFrame`
  ⇒ **2160×1440 与 4K 的行为一个字都不变**（改动是**惰性**的）。风险面因此**很小**。
- ⚠️ **必须新增 Hub 方法**（`PushRemoteFrameChunk`），**不许给 `PushRemoteFrame` 加参数** ——
  `HubContractArityTests` 钉住实测结论：SignalR 实参个数必须与签名**严格一致**，C# 默认值参数无效。
- 🔥 **单槽重组，不需要定时器/超时/后台清扫**，因为可以**论证**：
  第 3 跳是**单条顺序连接上的顺序流**（`PumpAsync` 是 `await sink(frame)` 之后才读下一帧，
  分片在同一帧内也顺序 await）⇒ **下一个帧 id 出现时，上一帧要么已齐、要么已永久丢**。
- 🔥 **离锁重组**：归属校验在 `_gate` 内（与 `AcceptFrame` 一致，**每一片都做**），
  **拼字节在 `_gate` 外**（每会话一把小锁），拼齐后**再**调 `AcceptFrame` ⇒ 既有并发假设不动。
  ⚠️ 理由：把十几 MB 的拷贝压进全局账本锁 = 让所有会话的所有帧去等它。
- **三个尺寸常量、三道墙、同一个根**：`SignalRMessageBytes` 8 MiB（不变）／
  `MaxChunkPayloadBytes` ≈5.75 MiB（**原 `MaxFramePayloadBytes` 改名**，值不变 —— 它现在管的是"一片"）／
  `MaxFrameBytes` 64 MiB（新增，**防呆**）。

**④ 🔴 两个新发现的缺陷（都不是推理出来的）**
1. **前端不检测序号跳变**：`remotePlayback.push` 只丢「序号 ≤ 已画」的**旧**帧，
   `sequence > lastSeq + 1` 无人处理 ⇒ **中心丢一帧（`Fanout` 的 `DropOldest` 今天就发生）
   会让解码器缺一个参考帧继续解、一路花到下一个关键帧，而没有任何一层知道**
   （不报错、不计日志、不重建解码器）。⇒ M5.4-c 补 `breakChain()`（**覆盖全部丢帧来源**）。
2. **分片带来新的丢帧来源（在中心），而被控端不知道** ⇒ `FrameTooLargeException` 的老问题再现
   （丢一帧＝在参考链上戳洞）。⇒ 新增 `AgentIngestAckDto.NeedKeyframe` 回传；
   **恢复代价从"最多一个 GOP"（`gopSize=60`/12fps ⇒ 5 秒花屏）压到"下一帧"**。
   ⚠️ 是本批**第二个跨层信号**（`AgentConnection`→`Coordinator`→`Host`，跨三层）⇒ **必须节流**。

**⑤ 手段 1–13 覆盖现状（诚实版）**：1 ✅（但决策只在开会话时算一次）／**2（SFE）❌**／
3 ✅／4 ✅／5 ✅／6 ✅／**7 ✅ 其实已天然实现**／**8 ❌**／9 → M5.5／10 ⚠️未核实／11 ✅／12–13 现场·采购。
- 🔸 **更正手段 7**：方案里写它「要维护上一帧与脏区状态机」，**实测不成立** —— DDA 是**增量投递**
  （`AccumulatedFrames`/`LastPresentTime` 判），**桌面没变就不给新帧** ⇒ 编码器**根本收不到那一帧**。
- 🔸 **`CapturedSurface.DirtyRects` 抓回来了但没有任何消费者**（全仓只有测试在读）。
  **拿到了 ≠ 该用**：它的用处是 M5.5 的 ROI / tile，本批**不动热路径**。
- 🔸 **手段 2 本批做不了**：能力探测 2026-10-15 实测「PDH 的 `engtype_VideoEncode` 实例
  **按进程活动创建** ⇒ **空转的 4090 与 MX250 长得一模一样**」⇒ 引擎数只能在**推流时**探；
  且 SFE **只有双引擎 Ada** 才有意义 ⇒ **另立小批**（不许"实现 + 假装验"）。

**⑥ 验收口径（不许编数字）**：本机屏只有 2160×1440 ⇒ **8K 真机端到端物理不可得**（沿用挂账）。
分片分三层验：**单测（中心重组：逐字节/缺片/错序/越界/头不一致）**＋
**单测（被控端切片：小帧仍走老路/边界 1 字节/片数=ceil/拼接相等）**＋
**端到端（冒烟加临时参数把分片阈值降到 64 KB，让正常帧也走分片路径）**。
🔴 必须写明：**「降阈值跑通」≠「8K 跑通」** —— 前者证"机制没 bug"，后者是**算术**，由单测覆盖。

**⑦ 待拍板 5 条**（见文档 §10）：范围裁定／方案乙／`NeedKeyframe` 做不做／
**本批先只做①**（改动面 7 个文件、四层）／③另立批。

### 14.16 ✅ M5.4-c① **六步全部交付**（10-17）：契约 ＋ 中心重组 ＋ **唯一一处切片实现** ＋ 前端序号体检 ＋ **请补关键帧四跳** ＋ **端到端（阈值旋钮）**

**拍板**：用户「按你的建议 继续下一步」= 认可 §14.15 ⑦ 的五条建议（**先只做 ①**）。

**① 契约（步 1）**：`RemoteStreamFrameChunk(Header, ChunkIndex, ChunkCount, Data)`；
Hub 方法 `PushRemoteFrameChunk`（**新方法，不许给 `PushRemoteFrame` 加参数** —— 实参个数被
`HubContractArityTests` 钉死）；`AgentIngestAckDto.NeedKeyframe`（**扩展字段**，不是改签名）。
三个尺寸常量**三道墙同一个根**：`SignalRMessageBytes` 8 MiB（不变）／
`MaxChunkPayloadBytes` ≈5.75 MiB（**原 `MaxFramePayloadBytes` 改名，值不变**）／`MaxFrameBytes` 64 MiB（新增防呆）。
⚠️ `RemoteStreamProtocol.MaxPayloadBytes` 同步**从"单片"抬到"整帧"**（`= MaxFrameBytes`）：
分片发生在**服务**，而一帧必须先**整帧**穿过本机管道 ⇒ 管道若还卡在 5.75 MiB，8K 那一帧在管道口就被丢了。

**② 中心重组（步 2）**：`RemoteFrameReassembler` 每会话一个实例，**单槽**。
- 🔥 **单槽够用是可以论证的**（不需要定时器/超时/清扫）：第 3 跳是单条顺序连接上的顺序流
  ＋ 泵是 `await sink(frame)` 之后才读下一帧 ⇒ **下一个帧序号出现时，上一帧要么已齐、要么已永久丢**。
- 🔥 **归属校验在 `_gate` 内（每一片都做）、拼字节在 `_gate` 外**（每会话一把小锁）；
  拼齐后**再**调既有的 `AcceptFrame` ⇒ `Observe`/`Latest`/`Fanout` 的并发假设**一行未改**。
- 🔴 **防御性上限一律"丢帧 + 计数"，绝不抛**（这条路直通 SignalR 调用栈，抛＝掐整条连接）：
  片数 `2..1024`、`0 ≤ ChunkIndex < ChunkCount`、同序号片数一致、同片不重复、累计 ≤ `MaxFrameBytes`。
- 🔴 **拼齐判据 `_received < _chunkCount ⇒ return null`（半帧不是帧）** ——
  **删掉它，下面的 `pieces[index]` 会直接抛 `KeyNotFoundException`** ⇒ 掐整条连接。
  本轮真抓到过一次（见 ④），所以它现在带着一段"为什么不能删"的注释。
- 🔥 **按 `ChunkIndex` 拼，不按到达顺序拼**：顺序是**链路的性质**，不是**协议的性质**。
- ⚠️ "迟到/重复的片"**不算丢帧**（`_lastCompletedSequence` + "比正在拼的那帧更旧"两道闸），
  否则一次无害的重发会换来**一路 IDR**（而 IDR 正是 8K 上最贵的东西）。
- 🆕 `Stats.ReassemblyDroppedFrames`：**中心丢的帧与"被控端本地丢的帧"（`Dropped`）绝不合并** ——
  合并了「它编不动」与「链路上掉了」就再也分不开，而那正是两个数并排存在的全部意义。
- 一片也算"这个被控端还在"（`Entry.TouchAgentActivity`）—— 新上行必须**明写表态**（`#42` 那次的教训）。

**③ 🔥🔥 步 3：`RemoteFrameChunker`（`src/ControlServer.Contracts/`）—— 唯一一处切片实现**
`CountFor` / `NeedsChunking` / `Slice`，**不写任何字面量尺寸**（三个数仍然只有一个根）。
- 🔥 **被控端切片与用例共用它**：这就是"用例到底证明了什么"的分水岭 ——
  用例自己再切一份的话，"3 片拼回来逐字节相等"证明的只是**「用例切的片能被拼回来」**，
  而生产切片器把最后一片多切一个字节时，**用例全绿**。
  ⇒ `RemoteFrameReassemblyTests` 里**没有**任何自己的切片代码（原先那个私有 `Slice` 已删）。
- ⚠️ **但有边界**：`ChunkCount` 荒唐 / 下标越界 / 片数为 1 / 同序号片数不一致 / 累计超上限
  —— 这五种片**生产代码永远造不出来**（那正是它们要防的东西）⇒ **必须在用例里手搓**。
  判据是「**生产代码造得出这种形状吗**」，不是"能不能偷懒用现成的切片器"。
- **片长取"均分"**（片数 = `ceil(len/max)`，片长 = `ceil(len/片数)`）而不是"装满、最后兜底"：
  两者都合法，选均分是为了**诊断** —— `len = max+1` 时后者产出 `[max, 1]`，看着像"有一片没内容"。
- ⚠️ `Slice` **会拷贝**（每片一份新数组）⇒ 调用方**必须先问 `NeedsChunking`**，
  否则等于给 2160×1440 / 4K 的**每一帧**加一次全量拷贝 —— 与"惰性改动"正好相反。
- 每一片共享**同一个帧头引用**（自描述；`Description` 有几 kB，逐片深拷等于把压力加回来）。
- 片数超过 `MaxChunkCount` ⇒ **当场抛**（与中心那条"绝不许抛"**不矛盾**：那边抛＝掐整条连接，
  这边只有本机自己调 ⇒「我们自己的代码要生成一份协议上非法的东西」当场炸更好查）。

**④ 🔥 被控端接线：`AgentConnection` 拆成两层**
`PushRemoteFrameAsync`（生产唯一入口）→ `internal static PushFrameAsync(invoke, frame, ct)`（**全部逻辑**）。
- 🔥 **为什么收一个"发射器"委托**：这段逻辑里三条判据都只能靠"看着到底发出去了几条什么消息"来验
  （惰性 / 片数 / 中心说停就停），而 `HubConnection` 是**具体类、方法不可替** ⇒ 不抽这一刀就没法验。
  这与 `RemoteSessionCoordinator` 的 `FrameSink`/`AudioSink` 同一套路：**接缝开在"谁真正动手"那一步**。
  `internal` + `InternalsVisibleTo(ControlServer.Agent.Tests)`（已存在）。
- 各片**顺序 `await`**（不是并发/不等应答）：中心的**单槽**假设正是靠"同帧各片顺序 await"撑住的。
  代价诚实说：12 片 = 12 次往返，局域网亚毫秒级 ⇒ 8K 本也只有 12 fps（83 ms/帧）的预算。
- `Ok == false` ⇒ **抛**（这路流停下），且**不许**因为"已经发了几片"降级成警告。

**⑤ 🔴 本轮真抓到的一个 bug（用例抓的，不是推理出来的）**
我在撤掉上一轮的 sabotage 时**把完成判据整行删掉了**（`_received < _chunkCount`），
于是 8 条重组用例立刻红，报 `KeyNotFoundException : The given key '1' was not present in the dictionary`
（`pieces[index]`）。⇒ 恢复该行后 11/11 绿。
**这就是"先红后绿"的价值**：它抓的不是"新代码写错了"，而是"**改完之后少了一句**"。

**⑥ "红"的两条记录（把 `Slice` 的 `ChunkIndex` 故意写成 `count-1-index`：偏移升序、下标倒序
⇒ 长度全对、位置全错）**：

| 侧 | 红 |
|---|---|
| 被控端 `AgentFrameChunkingTests` | **4**（C/D/E/G） |
| 中心 `RemoteFrameReassemblyTests` | **4**（A/B/C/H） |

⇒ **"同一处实现被两侧都用着"是被"弄坏一次"证明的，不是被"读代码"证明的。**

**⑦ 回归（10-17 凌晨，全绿）**：`Core` **873**（±0）／`Agent` **186→195**（**+9**）／
`Integration` **535→547**（**+12** ＝ 重组 11 ＋ 契约 1）／前端 `npm test` **83**（±0）。
编译 **0 警告 0 错误**。

**⑧ ⚠️ 验证环境的一个坑（新记）**：本仓验证把整个解决方案编到**同一个平铺 `OutDir`**
（`-p:OutDir=…`）⇒ `TrayAgent`/`Agent.Service`/`Agent.Tray` **并行往同一路径拷同一个 NuGet DLL**
⇒ 若干条 `MSB3026 文件被…锁定` 警告，**与代码无关**，`-m:1` 串行即消失。
⇒ **判"0 警告"之前先确认自己没被它骗**。

**⑩ 🔥 步 4：前端序号体检（`RemoteVideoBuffer.checkSequence`，`web/src/remotePlayback.ts`）**

`push()` **入队之前**调一次；新增字段 `lastReceivedSeq` ＋ `VideoBufferStats.gaps`（面板那格 4 个数 → 5 个：
`排队 / 挤出 / 过期 / 未上屏 / 未收到`）。

- 🔴 **判据为什么准**：**被控端丢帧不占序号** —— 这是一条**实测**结论，就写在
  `RemoteStreamHost.OversizeGiveUp` 的注释里：「曾经想当然地以为"丢帧会在序号上留一个洞"，实测 `Sequence` 是连续的」。
  ⇒ 序号一旦不连续，那几帧**只可能**是**离开被控端之后**丢的。
  ⇒ **整条链路上只有前端的 `gaps` 能看见中心那条容量 1 的 `Fanout` 在丢帧**（中心不回报自己丢了帧）。
- 🔴 **基线必须是「收到的号」，不是「画过的号」**：`lastPaintedSeq` 只在**上屏**时前进，
  而队列最多能积 12 帧 ⇒ 拿它当基线会把"正常到达"读成"跳了 N 个号" ⇒ **每一帧都重建解码器** ⇒
  **画面永远停在关键帧上（一直在等下一个关键帧），且没有任何报错**，而 `resets` 却一路涨
  —— 看起来像"中心一直在丢帧"。用例里专门有一条钉它（"队列积压时**不许**误报"）。
- **入队之前**调：断链要丢的是队列里那些**还没解的旧帧**，而**刚到的这一帧要留下**
  （它恰好是关键帧的话，画面就是从它接上的）—— 与队列溢出那条路（`push` 里的 `while`）共用同一个 `breakChain()`。
- MJPEG（`canDropUndecoded`）**不重建**（逐帧独立，缺一帧就是缺一帧，重建反而丢掉还能正常解的帧）—— 但 `gaps` 照记。
- 两刀 sabotage：① 基线换 `lastPaintedSeq` ⇒ **6 红**（含"积压不许误报"）；
  ② 拆掉 `if (!this.decoder.canDropUndecoded)` ⇒ **恰好 1 红**（就是 MJPEG 那条）。
  另有 **4 条守卫用例**（第一帧不算跳变 ×2、重复到达、积压不许误报）本该是绿的
  ⇒ **「红了几条」不是指标，「该红的红了、该绿的绿了」才是**。
- ⚠️ **重复／乱序到达的帧仍会重复入队**（早退那条用的是 `lastPaintedSeq`）——本步**有意不动它**（另立挂账）。
- 回归：前端 `npm test` **83→92**（**+9**）；`vue-tsc --noEmit` **0 错**。步 4 **零后端改动**。

**⑪ 🔥 步 5：四跳链路**（`NeedKeyframe` ⇒ 托盘 `wantKeyframe`）
- 链路：中心拼不起来 → `ack.NeedKeyframe` → `PushFrameAsync` 读（**按帧累积、循环外只问一次**）→
  `Coordinator.RequestKeyframe`（**1 秒节流**）→ 指令管道 `action=keyframe` →
  `Host.RequestKeyframe` → `Session.RequestKeyframe`（`Interlocked` 标志）→ 泵下一轮。
- 🔥 **节流在服务侧（收到那一侧）**：中心不知道对面多久能补上一个，让它去重＝让它猜。
  1 秒 ≈ 12 帧一个 IDR。**按帧去重**（中心同一故障会每片都回 ⇒ 每片各要一次＝把一次丢帧放大成一路 IDR）。
- 🔴 **两处"写出来的例外"**：①`Active._lastKeyframeRequestTicks` 用 `Interlocked`+CAS，**不进 `_gate`**
  （调用点在发帧路径，每帧一次）——本类第 2 个例外；②`RequestKeyframe` 是**同步 `void` + fire-and-forget**，
  被调方法**全包 try**，异常落 Warn（`void asyncFn()` 纪律的显式兑现）。
- 🔥 **顺带修一个静默洞**：泵里原 `wantKeyframe = false;`（无条件清）⇒ 编码器没照办时**请求永久消失**。
  改 `if (payload.IsKeyframe)`（与 `_oversizeKeyframeStreak` 同一条纪律）。
- 「先红」四处：托盘 3 红／服务 1 红（**5 条 vs 1 条**）／被控端 2 红／刀（无条件清）**恰好 1 红**。
- 🔴 **两次"红的是用例不是代码"**：①`ReadFramesAsync` 二次 `ConnectAsync` ⇒ `Already in a connected state`；
  ②测试桩反序列化用**大小写敏感**默认选项，而载荷是 camelCase（`"action"`）⇒ 计数恒 0，
  **看起来像"一条都没发"**。③另治一处**静默超时**（`WaitUntilAsync` 改成返回 bool ＋每个调用点硬断言）
  —— **静默超时比假 FAIL 更坏**（两边比 0 与 0 = 假 PASS）。
- 用例 **+12**：`RemoteStreamHostRequestKeyframeTests` 5／`RemoteKeyframeRequestTests` 3／
  `AgentFrameChunkingTests` +4。**`Agent.Tests` 195→207**。

**⑫ 🔥 步 6：端到端 —— 把"死代码"逼出来（`CONTROLSERVER_CHUNK_THRESHOLD_BYTES`）**

本机只有 **1920×1280** ⇒ 一条消息装得下 ⇒ **分片那条路平时是死代码**。要碰它只能把片上限降下来。

- **旋钮**：`AgentConnection.ChunkThresholdBytes`（默认＝协议上限）＋ `ResolveChunkThreshold()` 读
  **环境变量**（不是配置项/命令行：它不该看起来像产品能力、不该进配置快照与 UI、随进程结束消失）。
  `AgentRuntime.AttachRemoteSession` 读一次 —— 与 `FrameSink`/`KeyframeRequested` 挨着（"发帧这条路有哪几个旋钮"）。
- 🔴 **只许调小**：调大＝中心那条消息超 8 MiB＝**掐整条连接**。非法值**回落＋必须警告**
  （静默回落＝"跑了一整轮老路却以为在验分片"）。**不在热路径二次校验**（输入只在启动来一次）。
- ⚠️ 边界：`64 KiB × MaxChunkCount(1024) = 64 MiB = MaxFrameBytes` **刚好闭合**；再小（16 KB）理论上 `Slice` 会抛。
- 🔴 **可断言性的地基**：新增 `RemoteSessionStatsDto.ReassembledFrames`（中心拼成的帧数）。
  **没有它，旋钮没接线时冒烟会跑完整轮老路然后报绿 = 假 PASS**。
  与 `FramesReceived` **并排不合并**（后者在 2160×1440/4K 上永远更大）。
  **刻意不进观看页**（前端看到的永远是已拼好的整帧 = 方案乙立身之处）。
- 🔥 **两轮冒烟**：默认 **13/13**（1920×1280｜重建 0/解不出 0｜时延 117~135ms）；
  `--chunk-threshold 64` **16/16**（**收到 70／拼出 70／丢弃 0**｜时延 123~135ms ⇒ **分片无可见延迟**）。
- 「先红」：Agent **8 红**（N/O/Q/R×5）、中心 **2 红**（A/L）。三刀：阈值不传⇒N/O 红；
  不校验范围⇒R 的 `0/-1/999999999` 红（`abc/1.5` 绿，走解析失败支）；不记完成⇒A/L 红。

**⑬ 🔴 步 6 顺手修掉"看不见异常"（验证工具自身，代价 3 轮）**
`remote-e2e.py` 的 **`finally` 里那句 `sys.exit` 会把它正在传播的异常整个盖掉**
（Python 不打印被 finally 覆盖的异常）⇒ 现场＝**"只跑 4 项就正常退出 0，无任何报错"**，
而真因 `ModuleNotFoundError: playwright` 一个字看不见。修：`sys.exc_info()` ＋
`traceback.print_exception(..., file=sys.stdout)`（**必须 stdout**：默认 stderr 会因缓冲跑到**文件最前面**，
把"下面才是真因"指到空处）＋ 异常也计入失败退出码。
- 🔴 **冒烟必须用 `…/binaries/python/envs/default/Scripts/python.exe`**；`PATH` 上那个受管 3.13.12 **没装 playwright**。
- 🔴 **`run_browser()` 是独立函数、不是 `main` 闭包** ⇒ 里面写 `args.xxx` 是 `NameError`，**`py_compile` 查不出来**。

**⑨ 中心丢一帧之后的三个阶段（时长都一样 ≈5 秒 ＝ `gopSize 60` ÷ 12 fps，变的是**样子**与**谁知道**）：**

| | 丢一帧之后 | 有谁知道 |
|---|---|---|
| 步 4 之前 | **花屏** | **没有任何一层**（那正是这条洞最坏的地方） |
| 步 4 之后 | **冻结**（队列里的非关键帧不再上屏） | **前端**（`gaps` ＋ `resets` 都涨） |
| **步 5 之后** | 恢复到**下一帧** | **被控端**（收到 `NeedKeyframe` 去编一个 IDR） |

> ⚠️ 把「花屏」写成「冻结」不是措辞问题：花屏看起来像**编码器坏了**（于是人去查编码参数，
> 而真因是"参考帧压根没到"）；冻结是一个**明确可知的状态**，而且页面上有计数。
> 🔥 另一个方向也要记住：**中心 `ReassemblyDroppedFrames` 涨、前端 `gaps` 不涨** ⇒ 问题在分片；
> **反过来（`gaps` 涨而中心那个数不涨）** ⇒ 问题在**中心→浏览器那一段**，与分片无关。

**⑭ 🔴 集成那 3 条 `0x80070005`（环境，已定案）**：`DxgiCaptureSmokeTests` 报
`DuplicateOutput 失败（0x80070005 = E_ACCESSDENIED）= 每个显示器只允许一个 duplication 客户端`。
证据：①失败在 `DuplicateOutput` 那一刻；②**单独只跑这一组仍稳定 3 红**（连续 3 次）；
③本批**没碰 `Platform.Windows`** 也没碰该用例文件；④**同一时段两轮冒烟都成功抓屏编码**；
⑤**同类另外 5 条是绿的**（⇒ 不是整机不可用，是那个 output 被某个客户端占着；本机 QQBrowser 12 进程）。
⚠️ 那条用例"必须说清是桌面静止"的断言**缺一档「环境 / 被占用」**，会把环境失败误诊成桌面静止。

---

## 15. M5.5 第 4/5 项落地（10-18）：分块编码 tile>8192 ＋ ROI 只编视口

> 九项（多屏 / 剪贴板 / 并发配额 / 分块 / ROI / 静止降帧 / 质量档 / SFE / 关预处理器）至此全齐。
> **代码级验证已完成，真机未验** —— 见 §15.8。

### 15.1 一个决定：两项**共用同一套底座**，不是两套

ROI 是"只要一块"，分块是"切成好几块" —— 同一件事的两个用法。所以只有一条区域清单：

`EncodeRegion` → `EncodeRegionLayout.Tiles/Clamp` → `IRegionSurfaceEncoder.Regions` →
`WindowsGpuFrameEncoder.EnsurePipeline`（重建点）→ `EncodedRegionPayload` → 帧头 → 前端拼接。

分开写的代价不是多写一倍代码，是**转换器、指针叠加、帧头、前端拼接各写两遍**，
而其中至少三处"写错了不报错"（见 15.3）。

`IRegionSurfaceEncoder` 是**另开一个接口**、不往 `ISurfaceFrameEncoder` 上加方法：
MJPEG / 未来的软编都做不到"只编一块"，加进公共接口等于逼它们各写一个"我做不到"的假实现
—— 而假实现迟早被当真实现调用。

### 15.2 `EncodeRegion` 与 `EncodeRegionLayout`

- `EncodeRegion(X,Y,W,H)`：`Covers` / `Intersect` / `ToString() = "W×H+X+Y"`（日志与页面上是同一个写法）。
- `Tiles(viewport, maxTileWidth)`：竖条、均分后**向下取偶**、`maxTileWidth=0` ⇒ 一块、`viewport=null` ⇒ 空表。
- `Clamp(region, w, h)`：夹进画面 + 向下取偶，`<2×2` 返回 `null`。
- 🔥 奇数与越界是两种"驱动会安静接受"的输入：越界它裁掉、奇数它让色度平面错位 ⇒
  **夹完再建**，不指望 `Blt` / `CreateVideoProcessor` 报错。
- 🔴 均分 + 取偶会**丢 1–2 像素**（16400 切 3 块只盖 16398 ⇒ 画面右边少一条）⇒
  **最后一块吃尽剩余**（`i == count-1 ? remaining : min(stripe, remaining)`）。
  这个缺陷是本文件自己的用例（`EncodeRegionLayoutTests`）抓出来的。

### 15.3 平台层四个"不报错的错"

1. 🔴 **输入尺寸与输出尺寸必须分开声明**：`VideoProcessorContentDesc.For(inputW,inputH,outputW,outputH,…)`。
   早先两者恒等，"把输出尺寸当输入尺寸"永远是对的；到 ROI 就变成"声明一小块、实际喂整屏"⇒
   `Blt` 照回 `S_OK`，**输出未定义**。与 M5.4-b 指针叠加那次是同一个坑（当时表现：矩形外亮度 183.00）。
2. 🔴 **"复原基准矩形" ≠ "清空矩形"**：`ResetOutputRects()` 改名 `RestoreBaseRects()`。
   整帧基准＝不设矩形；单块基准＝源取 region、目标写满输出。
   改错的表现：指针叠加之后那一帧**整幅被压进一个小输出**（画面缩成一条，不报错）。
3. **每块一条独立码流**（`TileLane { Converter, Encoder, Pending, ProducedAnyFrame, … }`）：
   H.264/HEVC 的 P 帧只参考同一路，两块塞进一个编码器会互相污染参考链。
4. **配对队列必须逐块**：异步 MFT 的输出延迟是**逐编码器**的，共用一支队列会让每块的时间戳
   来自别的块 —— 症状是"有一块总比别块慢半拍"，而日志全绿。

### 15.4 四个判据 / 数值

- **要不要重建管线**：判**清单相等**，不判尺寸相等。ROI 只换位置（宽高不变）时，
  拿尺寸当判据会认为"没变" ⇒ **视口永远不生效**。
- **`FrameSize` 报源纹理尺寸**（不是某一块）：页面画布、输入坐标换算、会话信息都按它算，
  报成小块会让画布缩成一角且不报错。
- **码率按块均分**（`MinLaneBitrateBps = 500_000` 兜底）：整路预算是整路的，
  每块拿满额会让 2 块变 2 倍码率 —— 正好把分块换来的帧率用带宽还回去。
- **分块时一律带内**：`_codecDescription` 是第 0 块的参数集，各块 SPS/PPS 可能不同（宽度不同）
  ⇒ 拿第 0 块的 avcC 去 configure 其他块 = 花屏。

### 15.5 发送侧：序号、丢帧、Fatal

- 🔥 **同一批块共享同一个 `Sequence`**：前端每块各有独立抖动缓冲、靠序号判"断没断"；
  每块各自 +1 会让每块都以为"每帧跳了 N 个号"⇒ 每帧重建解码器⇒**画面永远停在关键帧**。
- 🔥 **但序号必须"懒惰取"**（`_batchSequence`，进批时清 0，打包完成＋闸门已过才
  `Interlocked.Increment(ref _sequence)`）：提前取号会让**丢帧也占一个号**，
  而序号的语义是"真的发出去了第几帧"，那个洞没人消费 ⇒ 只是噪音。
  （我第一版就是提前取的，`RemoteStreamOversizeTests` 的首帧 `Sequence==1` 直接把我钉住。）
- **`FrameSendResult { Sent, Dropped, Fatal }` 是枚举不是 bool**："丢帧"与"链路断了"
  下一步动作**相反**；合成一个 bool 只能都当丢帧，于是断线会话空转烧 CPU。
- **丢一块 ≠ 丢整帧**：每块独立码流独立解码器，丢一块只让"那一条区域"停在上一帧。
  所以 `Dropped` 只记账、**继续发下一块**，不 `break`；只有 `Fatal` 才 `return`。
- 抽 `SendOneAsync(tile)` 让单块与分块**走同一段**打包/闸门/写管道：
  分块那条路自己再写一遍，就会出现"带外时只有第一块能解"。

### 15.6 托盘：**两轮协商**，顺序不能反

方案 §1.5 的顺序是 **换格式 → 降采样 → 分块**：

- 第一轮完全按"换格式优先"（**M5.4 的行为一字不变**）；
- 第二轮只在**所有格式都因尺寸建不起来**（`GpuPipelineStage.Encode` ＋ `sizeFailure`）后才给
  `maxTileWidth = H264MaxDimension(4096) / HevcMaxDimension(8192)`；
- **MJPEG 挪到集合最后一格**：3 块 4K 硬编比整幅软编好得多，给它之前应先给分块一次机会。
  落到 MJPEG 时打一句"集合里本来就有它，但它排在硬编后面 —— 硬编（含分块）都试过了"。

顺序反了会把"3×4K 该走 HEVC 单路"打成 3 块 H.264（更差且更贵）。

### 15.7 前端：按块拼接 ＋ 视口上报

- `videoBuffers: Map<tileIndex, RemoteVideoBuffer>`、`lastPaintedSeqByTile`、
  `tileGeometry: Map<tileIndex,{x,y,frameWidth,frameHeight}>`；`videoBuffer` 恒指第 0 块（统计那一格）。
- 🔴 `drawImage` **四参数**：两参数版会把每块拉满整个画布（"几条画面互相覆盖"，看起来像编码坏了）。
- **块数变化 ⇒ 全部缓冲作废**（`stopAllVideoBuffers()`），否则新码流第一帧去接旧流状态 ⇒ 花屏到下一个关键帧。
- 视口：`measureViewport()` 用 `canvas.getBoundingClientRect()` 与滚动容器 `.rd-viewport` 求交，
  再按 `canvas.width / rect.width` 换算成**画面像素**（`fit` 模式下拿 CSS 像素发出去 ⇒ 视口偏小且位置全错）。
- **300 ms 去抖只在前端做一次**：一次视口变化 = 一次编码器重建；两处各去抖一次变成 600 ms
  （现场感觉是"滚完还卡一下"）。
- **退出观看必须强制关视口**（`reportViewport(true)`），不能按 `displayMode` 判 —— 退出时它可能还是 `actual`。

### 15.8 验证状态

- 全解决方案 **0 警告 0 错误**；`vue-tsc` 0 错；`vite build` 出 `wwwroot`。
- **Core 903 / Agent 242（+10 = `EncodeRegionLayoutTests`）/ 前端 98 全通过**；
  Integration 595/598 —— 那 3 条是 §14 末尾已定案的 `0x80070005`（环境）。
- 🔴 **真机待验**：①托盘 `[Remote] 分块编码生效：… 切成了 N 块（…）`；②`Describe()` 的"编码区域：N 块"；
  ③`CodecApiNote`；④页面滚动后视口切换（`QualityNote` ＋ 观测到 ≈0.3 s 空窗后恢复）；
  ⑤4K60 ≤100 ms、8K 全链路。**ROI/分块只在 GPU 硬编那条路上有效**，落到 MJPEG 时
  `Session.SetViewport` 会如实回 Failure（"ROI 只在 GPU 硬编那条路上有效"），不会假装成功。

---

## 16. 色彩转换用例：从"看桌面脸色"改成"自造纹理"（10-18）

### 16.1 病因：**断言输入不受控**

`D3D11ColorConvertSmokeTests` 原先拿"当前桌面"当输入，于是同一条阈值昨晚过、今晚红：

- `flatErr < 0.2`：同一份代码 **0.22 过 / 0.86 红**；
- 每像素残差界 `≤5`：实测被违到 **15.3 / 21.0** —— 因为驱动的灰度去饱和**不跟** `|BT.601 − BT.709|` 走。

**输入不受控的断言，红和绿一样没有信息量。** 这类用例最坏的地方不是误报，是它把
"真出问题"的那次也一起报成"跟昨天一样"。

### 16.2 解法：`CreateTexture2D` 合成 → `CopyResource` → 转

```
D3D11_USAGE_STAGING / CPU_ACCESS_WRITE / BindFlags = 0      ← 只为 Map 写像素
        │  Map(WRITE)，按 RowPitch 逐行填
        ▼
D3D11_USAGE_DEFAULT / BindFlags = SHADER_RESOURCE           ← Texture2DDesc.PipelineInput
        │  CopyResource
        ▼
TryConvert(...)  →  NV12 / BGRA 读回
```

- 🔥 `BindFlags = 0`（`Copyable`）**能** `CopyResource`，但**建不出 `VideoProcessorInputView`**。
  这就是新增 `Texture2DDesc.PipelineInput(width, height, format)`（`BindFlags = BindShaderResource`）的原因；
  报错文案还会误导成"建不出输入视图"，看起来像参数错了。

### 16.3 新用例 `Grayscale_keeps_luma_on_a_known_pattern`

图案：8 条竖带 = 4 条中性灰（32 / 96 / 160 / 224）＋ 3 条纯色 ＋ 1 条 4 px 黑白棋盘。
**只取每条带内部、离边界 ≥4 px 的像素**（转换器边界会混邻带）。

| 断言 | 界限 | 实测 |
| --- | --- | --- |
| 彩色帧 UV 标准差 | `> 1`（证明图案真有颜色） | — |
| 灰度帧 UV 极差 | `≤ 1` | 128..128 |
| 中性带亮度差（99 分位） | `≤ 8` | 6 / 1 / 3 / 1 |
| 棋盘格间跳变 | `> 120` | 219 |
| 彩色 Y 的有限范围 | 期望 98.4/153.4/208.4，容差 2 | 98 / 153 / 208 |
| `\|gray − color\|` | `≤ 8` | 见上 |

- 中性带 RGB 32/96/160/224 → Y **43 / 98 / 153 / 208**，BT.709 limited-range 期望 43.5 / 98.4 / 153.4 / 208.4。
- 灰度 luma 是**每带一致地**偏 +6 / −1 / −3 / −1（不是抖动）⇒ 这是驱动的往返误差，
  **界限定 8 而不是不可能的 1**，实测数字写进注释存档。
- 老的 `Nv12_conversion_matches_the_declared_color_space` 里 `flatErr<0.2`、`colorErr<12`、
  `maxExcess≤5`、`maxLumaDiff≤8` **全部降级为只打印**，只留一条很松的 `meanDiff < 8` 当冒烟，注释指向新用例。
- `CharacterizeLumaShift` 增 `FlatMax` / `MaxExcess` / `WorstExcess`；`MaxExcess` 用 `Math.Abs(observed − matrixGap)`
  —— 否则"矩阵根本没生效"时差值取到负数，最危险的场景反而显示"超额最小"。

### 16.4 状态

Integration **600 → 601**（新用例已在 TRX 里核对确实执行，不是被静默跳过）；
全量 **914 / 248 / 601 / 前端 98**，全解决方案 **0 警告 0 错误**。

---

## 17. 三个前端入口的浏览器实证（`--panels`，10-18）

`deploy/smoke/remote-e2e.py --panels` → 复用了整套"起中心 / 起客户机 / 起托盘 / 登录 /
建会话 / 等首帧"的架子，只加一节 `verify_panels`。**41/41 全过**（含指针那节）。

### 17.1 为什么单开一节

这三块（抓哪块屏 / 质量档 / 剪贴板）的判据全在**渲染与交互**上：
`vue-tsc` 只管类型、`vitest` 只覆盖纯函数（`remotePlayback` / `webCodecsDecoder` /
`tabState`…），**一行模板都没跑到**。此前记录的"仅类型＋产物验证，浏览器未实跑"
就是这段空白 —— 而空白里正好藏着两个真缺陷（见 17.3）。

### 17.2 判据（31 项，全部落在页面文字 / 后端返回值 / 宿主剪贴板三处交叉）

| 组 | 判据（要点） |
| --- | --- |
| 抓哪块屏 | 下拉在；**第一项是「自动」且默认选中**（写成主屏 ⇒ 用户以为拼接坏了）；项数 ≥2；除自动外每项带 `N×M`；点「重读屏」后 25 s 内回到可点且项数不变 |
| 质量档 | 四档顺序 = 自动/低/均衡/高；streaming 时可点；badge「当前：X」**等于后端 `quality`**；点「低」⇒ 后端 `quality=low`；再切回 ⇒ 后端回原值（双向）；**切档后 `decoded` 仍在增长**（编码器重建没把流弄死） |
| 剪贴板 | 两个按钮分开在；点读立刻有提示；宿主写已知文本 ⇒ 点读 ⇒ **页面经 `ClipboardChanged` 拿到同一串**；草稿点发 ⇒ **宿主剪贴板真的变成那段**；页面那一格同步；20050 字 ⇒ 提示"只发了前 20000 字"且剪贴板里**正好 20000** |

两条实测得来的写法：

- 🔥 **别"点完立刻读 `p.notice`"**：提示只有**一个槽**，上一步切质量档那句还挂 6 秒，
  第一次跑就是这样假红的 —— 要 `wait_for` 到**包含关键词的那条**。
- 🔥 **读宿主剪贴板必须声明 `restype/argtypes`**：`GetClipboardData` / `GlobalLock`
  默认按 `c_int`（32 位）返回，而 64 位下它们是**指针**。句柄被截断后 `wstring_at`
  读野内存 ⇒ 症状是"被控端日志明明写着已写入 20000 字符，我们读出来是 `''` 或 `None`"，
  看起来像"写入没生效"（我为此多跑了一轮）。

### 17.3 顺带挖出的两个真缺陷

**① 🔴 `Ok(null)` ⇒ 204 ⇒ `undefined` ⇒ 整个渲染函数崩**

- 服务端 `GET /api/remote/sessions/{id}/clipboard` 是 `return Ok(TryGetClipboard(...))`；
  ASP.NET Core 默认的 `HttpNoContentOutputFormatter` 把 **null 值改成 204 No Content**；
- 前端 `unwrap()` 对 204 返回 `undefined as T`；
- 面板 `loadClipboard()` 把 `undefined` 直接塞进 `clipboard`，模板接着读
  `clipboard.text` ⇒ **TypeError ⇒ 渲染函数抛异常 ⇒ 面板一片空白**（连画面都不出，
  因为 `decoded` 那些卡片也在同一个渲染函数里）。

现场症状极具迷惑性：`[FAIL] 解码器有产出 :: 实得 None` —— 看着像"被控端不推流"，
而客户机日志、协商、`framesSent` 全都正常；控制台里那 135 条
`Cannot read properties of undefined (reading 'text')` 才是真因。

修法（三处，各留注释）：
1. `api.ts` 的 `getRemoteClipboard` 末尾 `.then(dto => dto ?? null)` —— 把"没有内容"
   收敛成**一种**语义；
2. `loadClipboard()` / `onClipboardChanged()` 都 `?? null`（SignalR 也可能投递 `undefined`）；
3. 模板判"还没读过"用 **`!clipboard`**，不是 `=== null`。

**② 🔴 冒烟跑的是"内嵌的旧包"（改完前端怎么都不生效）**

- `Program.cs` 的逻辑是：**cwd 下有 `wwwroot` 目录就读磁盘，否则回退
  `ManifestEmbeddedFileProvider`**（绿色版/单文件用）。
- 冒烟的 `cwd` = 仓库根，那里**没有** `wwwroot` ⇒ 读的是编译期嵌进 dll 的那一份。
- 所以 `vite build` 之后**必须** `dotnet build src/ControlServer.Server` 重新嵌入，
  否则整轮验的是上一个版本 —— 而构建、产物、源码三处全绿。

为此在冒烟里加了前置判据 **「服务端发的前端产物 = 磁盘上刚构建的那一份」**
（比 `index.html` 里引用的 `assets/index-*.js` 文件名）。它把"改完没反应"
从"怀疑自己改错了"变成一句直接可读的红。同理，`--panels` 那一节的第一条
PASS 本身就是这个判据 —— 没有它，后面 30 条判据可能都在验旧包。

