using System.Diagnostics;
using System.Net.Sockets;
using ControlServer.Agent;
using ControlServer.Agent.Audio;
using ControlServer.Agent.Config;
using ControlServer.Agent.Remote;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;

// ═══════════════════════════════════════════════════════════════════════════
// 客户端Server · 托盘伴侣（M4.1）
//
// ⚠️ 2026-10-16：产品显示名由「中控客户端」改为「**客户端Server**」——
//    它是**显示名**，必须与安装包 AppName（ControlServer.Agent.iss 的 #define AppName）
//    和服务显示名（install-agent.ps1 的 -DisplayName）**同名**，否则用户会看到三个名字。
//    托盘里所有"露脸"的地方统一走下面的 `ProductName` 常量，不要再各写各的字面量。
//
// 为什么必须单独一个进程：客户端服务跑在 **Session 0**，那里没有 explorer、
// 没有通知区域 —— 服务进程自己**不可能**显示托盘图标（这是会话模型，不是实现问题）。
// 所以托盘放在用户会话里，由这个小进程负责。
//
// 它和服务之间有**两条**通信链路，各自分工明确：
//
//   1) 状态文件（%ProgramData%\ControlServer.Agent\status.json）—— 服务写、托盘读。
//      M4.1 那会儿只有这一条；它是"低频只读"的，为它引入双向协议并不划算。
//      ⚠️ 状态文件里**没有 agentKey**（见 AgentStatusFile 注释），所以托盘看不到凭据。
//
//   2) 会话侧命名管道（M4.5，SessionPipeProtocol）—— 服务连、托盘收。
//      它存在的理由是**有些事只能在这个进程里做**：Session 0 里启动的程序用户看不见、
//      改的音量用户听不到。所以「延时启动程序」与「音量 / 静音」由服务发指令到这里执行。
//      方向刻意是"服务主动连托盘"：托盘是普通用户权限、还可能根本没在运行，
//      反过来要求它知道"服务在哪、用什么凭据"是不合理的。
//
// 托盘刻意"职责做小"。
//
// 2026-10-14 之后菜单只有 4 项（需求②：仅保留 客户端服务：运行中 与 打开本机配置）：
//   1. 显示"客户端服务在不在运行"（信息行，走 SCM 查）
//   2. 打开本机配置页面（**这台机器自己的**任务、UDP 端口，以及它与中心的关系）
//   3. 打开本地数据目录
//   4. 打开日志目录
//
// 从菜单里**移出去**的（有意为之，不是漏做）：
//   · 显示"连没连上中心"、"连的是谁"、"会话侧通道" —— 前两者挪进了本机配置页的
//     「服务端」卡片（那里能显示版本、本机在中心里的名字 / ID、最后通信时间）；
//     悬停提示里仍有一句"客户端连没连上"。
//   · 「打开服务端网页」—— 同上，挪进本机配置页的「服务端」卡片。
//   · 「启动 / 停止客户端服务」—— 托盘不再是启停入口（用 services.msc / sc）。
//   · 「退出托盘」—— 要停只能 taskkill（不带 /F），那条路仍是**优雅停机**：
//     平台层收到 WM_CLOSE 会翻译成 Exit 命令（见 WindowsTrayIconHost 的 WM_CLOSE 分支）。
//
//   （外加 M4.5 的一件被动职责：在用户会话里替服务执行"启动程序 / 调音量"。）
//
// 启动方式：安装脚本为**当前登录用户**写 HKCU\...\Run，全程不需要管理员权限。
// ═══════════════════════════════════════════════════════════════════════════

const string DefaultServiceName = "ControlServerAgent";

// ── 产品显示名（唯一来源）──
//
// ⚠️ 不要再写"中控客户端"这种字面量：这个名字在 4 个地方各有一份
//    （托盘气泡 / 悬停提示 / 出错弹窗 / 本机配置页标题），
//    改名时漏掉任何一处，用户就会看到"两个产品"。
const string ProductName = "客户端Server";

var serviceName = GetArgument("--service-name") ?? DefaultServiceName;

// ⚠️ 状态目录必须与**服务侧**解析出同一个值，否则托盘永远显示"未连接"
//    而服务其实连着 —— 那是最容易误判为"客户端坏了"的一种情形。
var stateDirectory = AgentPaths.ResolveStateDirectory(GetArgument("--state-dir"));
var logDirectory = AgentPaths.LogDirectory(stateDirectory);

Log($"启动：服务名={serviceName} 状态目录={stateDirectory}");

if (!OperatingSystem.IsWindows())
{
    Log("非 Windows 平台，退出。");
    return;
}

// ── DPI 感知（M5.1）──
//
// 🔥 **必须在创建任何窗口之前**，因为 Windows 在第一个 HWND 出现时就会锁定
//    这个进程的 DPI 感知模式，之后再也改不了（且调用只是"静默失败"）。
//
// 为什么托盘需要它：远控要抓的是**真实像素**。非 DPI 感知的进程会被 Windows
// 按缩放比虚拟化 —— 150% 缩放的机器上抓到的是缩小且模糊的画面，
// 而 M5.2 的鼠标注入会**整体偏移一个缩放比**，那种偏差在 100% 缩放的机器上
// 完全不出现，是最容易漏测的一类 bug。
Log($"DPI 感知：{WindowsProcessDpi.TryEnablePerMonitorV2()}");

// ── 单实例：**一个服务名一个图标** ──
//
// 🔴 互斥体名里必须带**服务名**，不能是一个写死的全局名（M7.4 §11，2026-10-19 实测）：
//    本机装了正式客户端之后，验证用的那套（`--service-name=…` ＋ 独立状态目录）
//    会在这里被判成"已有一个托盘在运行"直接退出，**日志只有一句、进程安静消失**。
//    于是冒烟脚本看到的现象是"托盘起不来"（能力=不可用、建会话无人应答），
//    而真因是机器上装了别的客户端 —— 正是铁律 12 那类"红的是机器上装了什么"。
//
//    🔥 判据是**服务名**，因为它就是"一条链"的标识：服务侧按它找状态文件、
//       托盘按它开指令管道。两个服务名 ⇒ 两条互不相干的链，本来就该各有一个图标。
//       ⚠️ 代价说清楚：同机故意配两个不同服务名，通知区域里会出现两个图标 ——
//       那是**如实反映**（真的有两套在跑），而不是"重复启动"。
using var singleInstance = WindowsInteractiveHost.TryAcquireSingleInstance(
    $@"Local\ControlServer.Agent.Tray.SingleInstance.{Sanitize(serviceName)}");

if (singleInstance is null)
{
    Log($"已有一个托盘在运行（服务名={serviceName}），本次退出。");
    return;
}

// 互斥体名里不能有反斜杠（`Local\` 之后的部分会被当成层级）：服务名允许的字符收窄到
// 字母数字与 `-`/`_`/`.`，其余一律换成 `_`。两个不同服务名撞成同一个名字的概率极低，
// 且撞了也只是回到"只能起一个"的旧行为 —— 不会比以前更糟。
static string Sanitize(string name)
{
    var buffer = new char[name.Length];

    for (var i = 0; i < name.Length; i++)
    {
        var c = name[i];

        buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
    }

    return new string(buffer);
}

// 🔥 与**服务侧** `Program.PipeScope` 同一条规则：正式那套给 null，其余给服务名。
//    两处各写一份是刻意的（托盘与服务的组合根互不引用），但**判据只有一条**，
//    改这里必须同步改那边 —— 见 SessionPipeProtocol.PipeNameForSession 的说明。
static string? PipeScope(string? name)
    => string.IsNullOrWhiteSpace(name)
        || string.Equals(name.Trim(), "ControlServerAgent", StringComparison.OrdinalIgnoreCase)
            ? null
            : name.Trim();

// ── 会话侧指令通道（服务 ↔ 托盘的命名管道，M4.5）──
//
// 这是托盘**唯一**需要"主动提供服务"的能力，也是它从"只读状态灯"变成"能干活的伙伴"的那一步：
//   · 「延时启动程序」—— 服务在 Session 0 里启动的程序用户看不见，只能由这里启动；
//   · 「音量 / 静音」—— 同理，Session 0 里没有用户桌面的音频端点。
//
// ⚠️ 管道名带**本进程的会话编号**：多用户同时登录时会有多个托盘，
//    服务要能敲对"控制台那个人"的门（它找的是控制台会话，见 WindowsSessionLocator）。
//    托盘跑在 Session 0（有人拿 SYSTEM 身份直接拉起它）时名字是 `…Session.0`，
//    与服务的预期对不上 —— 那种情况下通道不会工作，但它本来也就不该工作（那里没有桌面）。
var sessionId = Process.GetCurrentProcess().SessionId;

// 🔥 与**服务侧**同一个判据（M7.4 §11）：正式那套（服务名 = 默认值）不加后缀，
//    管道名与老版一个字符都不差；改了服务名的另一套带上自己的名字，两套才能并存。
//    ⚠️ 两侧判据不一致的症状是"会话侧一律不可用"，而它和"托盘没起来"完全同形。
var sessionPipeName = SessionPipeProtocol.PipeNameForSession(sessionId, PipeScope(serviceName));

var audio = OperatingSystem.IsWindows()
    ? new WindowsHostAudioController()
    : (IAudioController)new UnsupportedAudioController("当前平台未实现音量控制。");

// ── M5.5 编码器调优：手段 2（SFE 切片）＋ 手段 10（关预处理）──
//
// 🔥 **为什么这两档必须由本进程决定**：编码器就建在这个进程里，
//    而"这台机器有几个 NVENC 引擎"只有本机能探（中心看不到被控端的 GPU）。
//    ⚠️ 这与服务进程那份 **能力上报** 不是同一件事：那份报给中心看；
//    这份是"我们据此怎么建编码器"，而且必须在**托盘**里算 ——
//    服务在 Session 0，那里连桌面都没有，探出来的引擎数与真实推流无关。
//
// ⚠️ 探测失败不阻塞启动：拿不到引擎数 ⇒ 按"单引擎"处理（不切）并把话说清楚。
//    猜成双引擎会让每帧多切几片，收益为零、代价还在。
var encoderTuning = await ResolveEncoderTuningAsync();

if (encoderTuning.Note is not null) Log($"[Remote] {encoderTuning.Note}");

// ── 远控推流（M5.1）──
//
// 🔥 它**只能在这个进程里**跑：抓屏与注入都要求"当前进程有桌面"。
//    服务在 Session 0，那里没有桌面 —— 这也正是 M4.5 那条会话管道存在的理由。
//
// ⚠️ 抓屏器按需新建（每次开流一个），会话编号**在新建的那一刻取**：
//    取一次存起来看着更省事，但那样"托盘被换到另一个会话"时就会带着旧编号抓屏
//    —— 而那正是 ScreenDescriptor.SessionId 这个字段要防的那类错。
await using var remoteStream = new RemoteStreamHost(
    () => new WindowsGdiScreenCapturer(Process.GetCurrentProcess().SessionId),

    // ══════════════════════════════════════════════════════════════════════
    // ── 视频编码器（M5.4-b：GPU 硬编优先 ＋ 自动回退 MJPEG）──
    //
    // 🔥 <b>为什么"选哪条路"这件事在这一层做，而不是在推流宿主里</b>：
    //    只有这里认识 `GpuPipelineVerdict` —— 那句"停在<b>哪一级</b>、为什么"是要进日志
    //    给现场看的。宿主只知道"我拿到了一个编码器"，它没有能力替我们判断
    //    "这次失败该安静换路，还是该报错"。
    //
    // 🔥 <b>回退链只有一级，而且刻意只有一级</b>：
    //    GPU 硬编（H.264）→ MJPEG。没有"GPU 建不起来就换 HEVC 再试"这种花活 ——
    //    H.264 建不起来几乎总是<b>环境</b>问题（屏被占 / 驱动不认 / 没有硬编单元），
    //    换一个编码格式解决不了它，只会让日志里多一条没人能解释的失败。
    //    ⇒ 而"这次失败是环境问题还是别的"由 `GpuPipelineVerdict.IsEnvironmentProblem` 说，
    //      我们在下面用它决定"安静换路"还是"把话说响"。
    //
    // 🔥 <b>M5.4-b 第 7 步在回退链<b>之前</b>插了一层，而且只有一层</b>：
    //    "中心给的可接受集合内换一个格式"（例如中心首选 H.264、而这块屏 5120 宽编不了）
    //    —— 然后才轮到 MJPEG。两层的判据完全不同，所以下面分开写：
    //      · **换格式**：只在 `Stage == Encode` 时做（那一级管的是"格式/尺寸配不配得上"，
    //        换一个格式真的可能成）；其余几级换格式解决不了；
    //      · **落 MJPEG**：换遍了都建不起来之后才走（它有自己独立的理由，见下面那行日志）。
    //    ⚠️ 为什么这次裁决必须由本机做：格式由**目标分辨率**决定，
    //      而分辨率只有在 `TryCreate` 挑完屏之后才知道 —— 中心看不到被控端的屏。
    //
    // ⚠️ 与抓屏那条工厂的区别：这个工厂**每次开流调用一次**，参数是
    //    `VideoEncoderRequest`（画质 ＋ 帧率 ＋ 屏号 ＋ **可接受格式集合**）。屏号必须传进去 ——
    //    GPU 门面按它挑屏，而它挑屏的规则与内存路径<b>刻意不同</b>（见
    //    `RemoteStreamHost.VideoEncoderRequest` 的注释：拿不到屏号会变成"抓主屏"，
    //    而症状是"看着 A 的屏幕、实际操作 B"）。
    // ══════════════════════════════════════════════════════════════════════
    request =>
    {
        // 🔒 本机否决（与输入那条同款）：每次都问一次，所以"翻开关"对**下一个会话**立刻生效，
        //    不需要重启托盘。⚠️ 标记文件的路径要打进日志 —— 现场才知道该往哪放。
        if (AgentGpuPolicy.ForcesMjpegIn(stateDirectory))
        {
            Log($"[Remote] 编码器 = MJPEG（本机已强制：存在 {AgentGpuPolicy.ForceMjpegPath(stateDirectory)}）");

            return new GdiJpegFrameEncoder(quality: request.Quality);
        }

        // 中心给的可接受集合（**按偏好排序**，第 0 项 = 中心首选）。
        // ⚠️ 空表 = 中心没指定（老中心 / 老页面）⇒ 本机自选，本批 = H.264。
        var wanted = request.Codecs is { Count: > 0 }
            ? request.Codecs
            : new[] { RemoteCodecs.H264 };

        WindowsGpuFrameEncoder? gpu = null;
        GpuPipelineVerdict? verdict = null;
        var tried = new List<string>();
        var mjpegOffered = false;
        var sizeFailure = false;

        // ══════════════════════════════════════════════════════════════════
        // 🔥 **两轮**（M5.5 分块编码的落位）
        //
        //   第一轮：完全按"换格式优先"走 —— 这就是 M5.4 的全部行为，一个字都不变。
        //   第二轮：只有**每一个格式都因为尺寸建不起来**之后，才允许把画面切成若干竖条
        //          （每块都在这个格式的上限之内）。
        //
        //   ⚠️ 顺序是方案 §1.5 定的：**换格式 → 降采样 → 分块**。反过来做的后果是
        //   "3×4K 的屏本来该走 HEVC 单路，却被打成 3 块 H.264" —— 接口处有接缝、
        //   码流变成三份、浏览器要同时解三路，而用户根本不知道自己被降级了。
        // ══════════════════════════════════════════════════════════════════
        for (var pass = 0; pass < 2 && gpu is null; pass++)
        {
            var allowTiles = pass == 1;

            if (allowTiles)
            {
                if (!sizeFailure) break;

                Log("[Remote] 中心给的所有格式都因为**尺寸**建不起来 —— 再试一轮，"
                    + "这次允许把画面切成多块（分块编码：每块独立一路码流，块数按格式上限算）。");
            }

            foreach (var name in wanted)
            {
                // 🔥 MJPEG 是**集合里唯一不需要硬编**的那个（GDI+ 软编，永远可用）。
                //    ⚠️ 但它是**最后一格**：先跳过它，给分块一轮机会 ——
                //    "3 块 4K 横拼"那种屏切成几块硬编，比整幅软编 MJPEG 好得多
                //    （后者在那种像素量上只有个位数帧率，而且吃的是被控机的 CPU）。
                if (string.Equals(name, RemoteCodecs.Mjpeg, StringComparison.Ordinal))
                {
                    mjpegOffered = true;

                    continue;
                }

                if (!EncoderCapabilityMapper.TryParseWireCodec(name, out var codec))
                {
                    // ⚠️ 只记一句、继续试下一个：它可能来自一个更新的中心（新格式），
                    //    而"本机不认得"不等于"这台机器做不到" —— 别在这里就收场。
                    //    第二轮不再重复这句（同一句话打两遍只会让人以为出了两次事）。
                    if (!allowTiles)
                    {
                        Log($"[Remote] ⚠️ 中心给的格式「{name}」本机不认得，跳过它、试集合里的下一个。");
                    }

                    continue;
                }

                // 单块上限 = 这个格式的单边上限（H.264 4096 / HEVC 8192）。
                // ⚠️ 第一轮恒为 0（不切）：那是 M5.4 的行为，不能因为新功能就动它。
                var tileLimit = codec == VideoCodec.Hevc
                    ? WindowsGpuFrameEncoder.HevcMaxDimension
                    : WindowsGpuFrameEncoder.H264MaxDimension;

                gpu = WindowsGpuFrameEncoder.TryCreate(
                    Process.GetCurrentProcess().SessionId,
                    codec,
                    request.Fps,
                    out verdict,
                    screenId: request.ScreenId,
                    sliceCount: encoderTuning.SliceCount,
                    disablePreprocessing: encoderTuning.DisablePreprocessing,
                    maxTileWidth: allowTiles ? tileLimit : 0);

                tried.Add(name);

                if (gpu is not null)
                {
                    if (allowTiles)
                    {
                        Log($"[Remote] 分块编码生效：{gpu.Codec} 把画面切成了 "
                            + $"{gpu.Regions.Count} 块（{string.Join(" / ", gpu.Regions)}）—— "
                            + "每块是一路独立码流，观看端会按块拼接。"
                            + "块边界上不跨块做运动估计，接缝处的画质可能略差。");
                    }

                    break;
                }

                // 🔥 **只有 `Encode` 一级才值得换格式**：那一级管的是"格式/尺寸配不配得上"
                //    （H.264 硬编封顶 4096、HEVC 封顶 8192），换一个格式真的可能做成。
                //    其余几级（Screen / Capture / ColorConvert / Platform）都是**与格式无关**的 ——
                //    屏被别的抓屏程序占着、驱动不认、这台机器没有硬编单元。
                //    在那里继续试，只会让日志里多几条没人能解释的失败。
                if (verdict is not { Stage: GpuPipelineStage.Encode })
                {
                    sizeFailure = false;

                    break;
                }

                sizeFailure = true;

                Log($"[Remote] 格式「{name}」在这块屏/这台机器上建不起来（停在哪一级：{verdict}），"
                    + "按中心给的可接受集合试下一个格式…");
            }
        }

        if (gpu is not null)
        {
            var (width, height) = gpu.FrameSize;

            // ⚠️ 改判要说出来。现场看到的是"中心可能说 h264、码流却是 hevc"，
            //    没有这一行就完全无法判断是哪一侧做的决定。
            if (tried.Count > 1)
            {
                Log($"[Remote] ⚠️ 编码格式**改判**：中心首选 {tried[0]}，本机实际用了 {gpu.Codec}"
                    + $"（依次试过 {string.Join(" → ", tried)}）。"
                    + "⚠️ 它**没有越出**中心给的可接受集合，所以观看端的浏览器一定解得了。");
            }

            Log($"[Remote] 编码器 = GPU 硬编：{gpu.Codec}｜{width}×{height}"
                + $"｜屏={gpu.ScreenName}({gpu.ScreenId})｜目标 {request.Fps} fps");

            return gpu;
        }

        // ── 回退 ──
        //
        // ⚠️ 这一行必须把"停在哪一级"原样说出来。只说一句"GPU 不可用，已回退"
        //    会让现场无从判断"是我这台机器本来就该走 MJPEG"还是"本来能用、坏了"——
        //    而那两件事的处置完全相反（一个不用管，一个要去查抓屏占用/驱动）。
        var environmentProblem = verdict?.IsEnvironmentProblem ?? false;

        if (mjpegOffered)
        {
            // ⚠️ 这句话解释的是"为什么现在才落 MJPEG"：集合里本来就有它，
            //    但我们让硬编（含分块）先试过 —— 否则现场会以为"中心要 MJPEG、
            //    这台机器偏要试硬编"。
            Log("[Remote] 中心给的集合里本来就有 MJPEG，但它排在硬编后面 —— "
                + "硬编（含分块编码）都试过了，现在才落到它。");
        }

        Log($"[Remote] ⚠️ GPU 硬编不可用，已回退 MJPEG（中心给的可接受集合 = {string.Join(" > ", wanted)}"
            + $"，依次试过 {string.Join(" → ", tried)}）。停在哪一级：{verdict}"
            + (environmentProblem
                ? "（环境问题 ⇒ 这条回退是正常路径，不是故障：这台机器本来就走 MJPEG 档）"
                : "（**不是**环境问题 ⇒ 看起来是能力/配置层面的事，值得查一下）")
            + "。⚠️ MJPEG 不需要 WebCodecs，任何浏览器都显示得了，所以这次回退不会让观看端黑屏。");

        return new GdiJpegFrameEncoder(quality: request.Quality);
    },

    // ── 键鼠注入（M5.2）──
    //
    // 🔥 它与抓屏受同一条约束：**只能在用户会话里做**（服务在 Session 0，那里没有桌面，
    //    在那边注入等于往一个不存在的桌面发按键：返回成功、用户什么都看不到）。
    //    所以注入器建在这里、由这个进程执行。
    new WindowsInputInjector(),

    // ── 本机否决（M5.2）──
    //
    // 🔒 这台机器可以单方面说"我不接受远程键鼠"。它是**读文件**、不是读配置：
    //    删掉/建立那个标记文件即翻转，而且**不需要重启托盘**（每次开流问一次），
    //    也没法被中心覆盖（它不走任何下发路径）。
    //    ⚠️ 传的是委托而不是布尔值 —— 传值就等于"构造时定死"，那之后建的标记文件要等到
    //       下次重启托盘才生效，而现场会以为开关坏了。
    () => AgentInputPolicy.AllowsInputIn(stateDirectory),

    message => Log($"[Remote] {message}"),

    // ── 系统声音采集（M5.3）──
    //
    // 🔥 与抓屏、注入同一条约束：**只能在用户会话里做**（Session 0 没有音频端点，
    //    在那边采集拿到的要么是失败、要么是"一个不存在的桌面的声音"）。
    //
    // ⚠️ 每个会话新建一个（不用单例）：采集器内部握着一个 WASAPI 客户端与一条专用线程，
    //    跨会话复用会让"上一路的设备句柄"与这一次对不上 ——
    //    用户中途插耳机 / 切换默认输出设备时，这个问题会以"没有声音"的形式出现，且极难查。
    () => new WindowsWasapiLoopbackCapturer(),

    // ── Opus 编码（M5.3）──
    //
    // ⚠️ 同样是每次新建：编码器内部有残帧缓冲（WASAPI 按 10 ms 交数据、Opus 要 20 ms 一帧），
    //    复用会让上一个会话攒的半帧被编进下一个会话的第一帧 —— 听感上是"开头有一小声杂音"。
    () => new OpusAudioEncoder(),

    // ── 唤醒锁（M7.6）：有人看这一路时，别让这台机器自己关屏 ──
    //
    // 🔥 治的是那个被误判过两轮的形态：**画面全黑、被控端一动就好、两端日志全干净**。
    //    真因是没人碰的机器静置之后按电源计划关掉了显示器（或起了黑屏屏保），
    //    而 DDA 这时照样"抓得到" —— 抓到的是一整片黑。
    //
    // ⚠️ **必须共享一个实例**：持有状态挂在一个专用线程上，
    //    每个会话新建一个的话，前一个的线程就成了没人关得掉的孤儿（而且它还在替我们
    //    拦着"别关屏"，一直到进程退出为止）。
    displayWakeLock: new WindowsDisplayWakeLock());

await using var sessionPipe = new SessionPipeServer(
    sessionPipeName,
    audio,
    ProgramLauncher.Launch,
    message => Log($"[Session] {message}"),
    remoteStream,

    // ── 剪贴板（M5.5）──
    //
    // 🔥 **必须在托盘进程里，不能在服务进程里**：Windows 剪贴板是**每会话**的资源，
    //    服务跑在 Session 0（没有交互桌面）⇒ 那里读到的永远是一块空剪贴板，
    //    写进去的东西用户也永远看不到。和"远控的手在托盘"是同一条理由。
    new WindowsClipboardAccess());

sessionPipe.Start();

// ⚠️ 启动这一行要如实说出"本机允不允许远程输入"：它此刻可能已经是"禁止"状态
//    （标记文件在），而这一行是现场判断"到底是没能力还是被自己关了"的第一手材料。
var inputStateAtStartup = AgentInputPolicy.AllowsInputIn(stateDirectory)
    ? "可用"
    : "已被本机禁止（remote-input.deny）";

// ⚠️ 音频同样要在启动这一行里如实说清 —— 而且**只探测一次，不建采集**。
//    采集器本身是每次开流才新建的（见上面那个工厂）；这里建一个只为回答
//    "这台机器有没有声音可采"，问完立刻放掉。
//    🔥 这一行是"没有声音"这一类问题的第一手材料：它把
//    "本机没有音频输出设备"与"有设备但服务侧没接上"在**启动那一刻**就分开了。
var audioStateAtStartup = ProbeAudioAtStartup();

Log($"会话侧指令通道已启动：{sessionPipeName}｜音量可用={audio.IsAvailable}｜远控推流=可用"
    + $"｜键鼠注入={inputStateAtStartup}｜音频采集={audioStateAtStartup}");

var controller = new WindowsServiceController(serviceName);

await using var tray = new WindowsTrayIconHost();

if (!tray.IsAvailable)
{
    Log($"托盘不可用：{tray.UnavailableReason}");

    WindowsInteractiveHost.ShowError(
        $"{ProductName} · 托盘",
        $"无法显示托盘图标：{tray.UnavailableReason}{Environment.NewLine}{Environment.NewLine}"
        + $"（服务仍在运行；可用 services.msc 或 `sc stop {serviceName}` 操作。）");

    return;
}

// ⚠️ 先取初始状态再挂命令处理器：处理器里的 Refresh 要捕获 lastSignature，
//    而 C# 不允许引用"声明在之后"的局部变量。
var initialSnapshot = AgentStatusFile.Read(stateDirectory);
var initialService = controller.QueryStatus();
var lastSignature = BuildSignature(initialSnapshot, initialService);

Log($"初始：服务={initialService.Describe()}，客户端={initialSnapshot?.Status ?? "(尚无状态文件)"}");

var exitSignal = new TaskCompletionSource();

tray.CommandInvoked += commandId =>
{
    try
    {
        switch (commandId)
        {
            // ⚠️ 2026-10-14 菜单收敛：下面这三条命令**已经不在菜单里**，所以这里不再处理：
            //   · 「打开服务端网页」—— 挪进了本机配置页的「服务端」卡片
            //     （那边能顺带显示版本、本机在中心里的名字 / ID、最后通信时间，比托盘上孤立一行地址有用）；
            //   · 「启动 / 停止客户端服务」—— 按需求从菜单移除。
            //     ⚠️ 这意味着托盘不再是"启停服务的入口"，服务本身仍可用 services.msc / sc 操作。
            //   枚举值**保留**在 Platform 层：服务端托盘（ControlServer.TrayAgent）还在用它们。

            case TrayCommands.OpenLocalConfig:
                OpenLocalConfigPage();
                break;

            case TrayCommands.OpenDataDirectory:
                OpenInExplorer(stateDirectory);
                break;

            case TrayCommands.OpenLogDirectory:
                OpenInExplorer(logDirectory);
                break;

            case TrayCommands.Exit:
                // ⚠️ 菜单里已经没有「退出托盘」这一项了，但这条分支**不能删**：
                //    平台层收到 WM_CLOSE（`taskkill` 不带 /F 就是发它）时会翻译成这条命令
                //    （见 WindowsTrayIconHost 的 WM_CLOSE 分支）。少了它，外部关闭请求就没有
                //    优雅停机的路径，进程只能硬杀。
                Log("收到退出请求（菜单项已移除；来源多半是 taskkill）。");
                exitSignal.TrySetResult();
                break;

            case TrayCommands.StatusInfo:
            case TrayCommands.OpenConsole:
            case TrayCommands.CopyAddress:
            case TrayCommands.ShowStatus:
            default:
                // 纯信息行（不可点）或对本托盘无意义的命令：忽略。
                break;
        }
    }
    catch (Exception ex)
    {
        Log($"处理命令 {commandId} 时异常：{ex}");
    }

    Refresh(force: true);
};

// ── 显示图标 ──
if (!await tray.ShowAsync(BuildOptions(initialSnapshot, initialService)))
{
    Log($"托盘图标显示失败：{tray.UnavailableReason}");

    WindowsInteractiveHost.ShowError(
        $"{ProductName} · 托盘",
        $"托盘图标显示失败：{tray.UnavailableReason}");

    return;
}

// 首次提示一次：让用户知道图标在哪、这台机器有没有连上中心。
_ = tray.ShowBalloonAsync(
    ProductName,
    $"托盘已启动 · {DescribeClient(initialSnapshot)}");

// ── 轮询：状态文件（服务写）+ SCM（服务运行态）。状态真变时才刷菜单 ──
var tick = TimeSpan.FromSeconds(3);

while (true)
{
    var finished = await Task.WhenAny(exitSignal.Task, Task.Delay(tick));
    if (finished == exitSignal.Task) break;

    Refresh();
}

await tray.DisposeAsync();
Log("客户端托盘已退出（服务不受影响）。");

// ═══════════════════════════════════════════════════════════════════════════
// 本地函数
// ═══════════════════════════════════════════════════════════════════════════

static string? GetArgument(string name)
{
    var args = Environment.GetCommandLineArgs();

    for (var i = 1; i < args.Length; i++)
    {
        // 同时接受 "--key value" 与 "--key=value"。
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        var prefix = name + "=";
        if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return args[i][prefix.Length..];
        }
    }

    return null;
}

/// <summary>一句话概括"客户端连没连上"（托盘提示与气泡共用）。</summary>
static string DescribeClient(AgentStatusSnapshot? snapshot)
{
    if (snapshot is null) return "客户端尚未上报状态";

    var name = string.IsNullOrWhiteSpace(snapshot.DisplayName)
        ? (snapshot.AgentId is { } id ? $"#{id}" : "未命名")
        : snapshot.DisplayName;

    return snapshot.Connected
        ? $"已连接（{name}）"
        : snapshot.Status;
}

/// <summary>
/// 刷新判据：只有这些字段变化才值得重画菜单（避免每 3 秒都更新一次图标）。
/// <para>
/// ⚠️ 它必须与 <see cref="BuildOptions"/> <b>实际用到的东西对齐</b>。2026-10-14 菜单收敛之后，
/// 渲染出来的只剩两样：信息行（服务运行态）与悬停提示（客户端连没连上）。
/// 判据里留着已经不再显示的字段，只会让菜单在没人看得见的地方白刷一遍，
/// 将来还会变成"它为什么老闪"的疑案 —— 所以 <c>ServerUrl</c> 在这一轮被移出了判据。
/// </para>
/// </summary>
static string BuildSignature(AgentStatusSnapshot? snapshot, WindowsServiceStatus service)
    => string.Join(
        '|',
        service.State.ToString(),
        snapshot?.Status ?? "-",
        snapshot?.Connected.ToString() ?? "-",
        snapshot?.DisplayName ?? "-");

TrayIconOptions BuildOptions(AgentStatusSnapshot? snapshot, WindowsServiceStatus service)
{
    // ═══════════════════════════════════════════════════════════════════════
    // 菜单（2026-10-14 收敛：需求②「仅保留 客户端服务：运行中 与 打开本机配置 两个选项」）
    //
    // 现在只有 4 项：一条信息行 + 三个入口。
    //   · 信息行 = 客户端服务在不在跑（用户点名要保留的那一条）；
    //   · 「打开本机配置」= 唯一的功能入口 —— 这台机器与中心的关系（地址 / 版本 /
    //     本机在中心里的名字与 ID / 最后通信时间 / 配置同步状态）**全都搬进了那个页面**
    //     的「服务端」卡片，所以菜单里不必再各占一行；
    //   · 数据目录 / 日志目录 —— 排障第一步，按决定**刻意保留**。
    //
    // 移出去的：「打开服务端网页」「启动 / 停止客户端服务」「退出托盘」以及三条状态行。
    //   ⚠️ 后果要说清：托盘不再是"启停客户端服务"的入口（用 services.msc / sc 操作），
    //      也没有"退出托盘"菜单项（要停只能 taskkill，那条路仍是优雅停机 —— 见 WM_CLOSE 分支）。
    // ═══════════════════════════════════════════════════════════════════════
    var menu = new List<TrayMenuCommand>();

    menu.Add(service.State == WindowsServiceState.NotInstalled
        ? new TrayMenuCommand("客户端服务：未安装", TrayCommands.StatusInfo, Enabled: false)
        : new TrayMenuCommand($"客户端服务：{service.Describe()}", TrayCommands.StatusInfo, Enabled: false));

    menu.Add(TrayMenuCommand.Separator);
    menu.Add(new TrayMenuCommand("打开本机配置", TrayCommands.OpenLocalConfig));
    menu.Add(TrayMenuCommand.Separator);
    menu.Add(new TrayMenuCommand("打开数据目录", TrayCommands.OpenDataDirectory));
    menu.Add(new TrayMenuCommand("打开日志目录", TrayCommands.OpenLogDirectory));

    return new TrayIconOptions(
        ToolTip: $"{ProductName} · {DescribeClient(snapshot)}",
        Menu: menu,

        // 双击图标 = 打开本机配置页。**固定**，不再"有中心就开中心"——
        // 收敛之后菜单里已经没有服务端网页这一项了，默认项再指向它就成了个隐形入口。
        DefaultCommandId: TrayCommands.OpenLocalConfig);
}

void Refresh(bool force = false)
{
    var snapshot = AgentStatusFile.Read(stateDirectory);
    var service = controller.QueryStatus();
    var signature = BuildSignature(snapshot, service);

    if (!force && signature == lastSignature) return;

    lastSignature = signature;
    _ = tray.UpdateAsync(BuildOptions(snapshot, service));

    if (!string.IsNullOrWhiteSpace(service.ErrorMessage))
    {
        Log($"服务状态查询告警：{service.ErrorMessage}");
    }
}

/// <summary>
/// 打开<b>本机配置页面</b>（2026-10-14）。
/// <para>
/// 地址优先取"服务写下的那份发现文件"，取不到再用默认端口兜底 ——
/// 端口被改过时托盘是猜不中的（它不读 appsettings.json）。
/// </para>
/// <para>
/// ⚠️ 打开之前先<b>探一下端口上有没有人在听</b>：页面没启用（或服务没起来）时
/// 直接把浏览器指向死地址，用户看到的是浏览器自己的「无法访问此网站」——
/// 那看起来像"客户端坏了"，而其实只是"这个功能没开"。这里替他分辨清楚。
/// </para>
/// <para>
/// 🔥 2026-10-14 起它是<b>菜单上唯一的功能入口</b>（托盘菜单收敛到 4 项），
/// 所以这段"替他分辨清楚"的逻辑比以前更要紧 —— 它是用户唯一会走到的那条路。
/// </para>
/// </summary>
void OpenLocalConfigPage()
{
    var url = AgentLocalUiDiscoveryFile.Read(stateDirectory)
              ?? $"http://127.0.0.1:{AgentLocalUiOptions.DefaultPort}/";

    if (!IsPortListening(url))
    {
        var service = controller.QueryStatus();

        var reason = !service.IsRunning
            ? "客户端服务没有在运行 —— 请先「启动客户端服务」。"
            : "客户端服务在运行，但本机配置页没有在监听。"
              + "常见原因：配置里关掉了它（Agent:LocalUi:Enabled=false），或那个端口被别的程序占用了。"
              + "详见客户端数据目录下的日志。";

        Log($"打开本机配置页失败（{url}）：{reason}");

        WindowsInteractiveHost.ShowInformation($"{ProductName} · 本机配置（{url}）", reason);

        return;
    }

    OpenUrl(url);
}

/// <summary>探一下这个地址上有没有人在听（400 毫秒）。只用来"别把浏览器指向一个死地址"。</summary>
static bool IsPortListening(string url)
{
    try
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        using var client = new TcpClient();

        return client.ConnectAsync(uri.Host, uri.Port)
                   .Wait(TimeSpan.FromMilliseconds(400))
               && client.Connected;
    }
    catch
    {
        // 连接被拒 / 超时 / 主机不可达 —— 都是"没人在听"。
        return false;
    }
}

static void OpenUrl(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"打开 URL 失败：{ex.Message}");
    }
}

static void OpenInExplorer(string path)
{
    try
    {
        if (!Directory.Exists(path))
        {
            Log($"目录不存在，无法打开：{path}");
            WindowsInteractiveHost.ShowError($"{ProductName} · 托盘", $"目录不存在：{path}");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"打开目录失败：{ex.Message}");
    }
}

/// <summary>
/// 启动时问一次"这台机器有没有系统声音可采"，只为**在日志里留一句实话**。
///
/// <para>
/// ⚠️ 它<b>只建一个临时采集器来问，不开始采集</b>：真正的采集是每次开流时另建的
/// （见 <c>RemoteStreamHost</c> 的音频工厂）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么值得在启动日志里占一格</b>：远控"没有声音"最常见的解释是
/// "这台机器根本没有音频输出设备"（虚拟机 / 无声卡工控机 / 声卡被禁用），
/// 而那与"有设备、但服务侧没接上"在现象上完全一样。把结论写在启动那一刻，
/// 现场排查的第一步就不用猜了。
/// </para>
/// </summary>
static string ProbeAudioAtStartup()
{
    IAudioCapturer? capturer = null;

    try
    {
        capturer = new WindowsWasapiLoopbackCapturer();

        if (!capturer.IsAvailable) return $"不可用（{capturer.AvailabilityNote}）";

        var format = capturer.Format;

        return format is null ? "状态不一致（报可用但取不到格式）" : $"可用（{format}）";
    }
    catch (Exception ex)
    {
        return $"探测失败（{ex.Message}）";
    }
    finally
    {
        try
        {
            capturer?.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }
}

/// <summary>
/// 决定这次的编码器调优档位（M5.5 手段 2 / 手段 10）。
///
/// <para>
/// 🔥 <b>"切几片"的判据是引擎数，不是"越大越好"</b>：单引擎机器上切 2 片
/// 只会多一次熵编码收尾（每一片都要自己收尾对齐），<b>一分收益都没有</b>。
/// 而双引擎机器上不切，就等于把另一半硬件放着不用 —— 那正是 8K60 卡在 30 fps 的原因。
/// </para>
/// </summary>
static async Task<EncoderTuning> ResolveEncoderTuningAsync()
{
    const string slicesEnvVar = "CONTROLSERVER_RD_SLICES";
    const string keepPrepEnvVar = "CONTROLSERVER_RD_KEEP_PREPROC";

    var notes = new List<string>();

    // ── ① 关预处理：默认关 ──
    //
    // 🔥 默认关，因为那些预处理（AQ / 时域降噪 / 心理视觉 / UHQ）**全是为自然图像设计的**：
    //    桌面的硬边矩形与纯色大面积不但不受益，还要让编码器每帧多跑一趟分析；
    //    而 UHQ 那个坑更狠 —— 它会让 8K 吞吐**变差**（方案 §0.5）。
    // ⚠️ 留了开关：真机上如果觉得文字糊了，设这个变量就能回到驱动默认。
    var keepPreproc = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keepPrepEnvVar));

    var disablePreprocessing = !keepPreproc;

    notes.Add(disablePreprocessing
        ? "预处理：关（手段 10；设 " + keepPrepEnvVar + " 可回到驱动默认）"
        : $"预处理：**保留**（{keepPrepEnvVar} 已设置 —— 本次不做手段 10）");

    // ── ② 切片数 ──
    var engines = await ProbeEncodeEnginesAsync().ConfigureAwait(false);

    if (int.TryParse(Environment.GetEnvironmentVariable(slicesEnvVar), out var forced) && forced >= 0)
    {
        notes.Add($"SFE：切片数被 {slicesEnvVar} 强制成 {forced}"
            + "（这是 A/B 诊断模式 —— 正常运行时不该看到这一行）");

        return new EncoderTuning(forced, disablePreprocessing, string.Join("；", notes));
    }

    var slices = engines is >= 2 ? 2 : 0;

    notes.Add(engines is null
        ? "SFE：未切（引擎数探不到 ⇒ 按单引擎处理，不冒「多切一片」的险）"
        : engines.Value >= 2
            ? $"SFE：每帧 {slices} 片（探测到 {engines.Value} 个编码引擎；"
              + "⚠️ 是否真的并行只能看实测帧率）"
            : $"SFE：未切（探测到 {engines.Value} 个编码引擎 —— 单引擎上切片只增开销）");

    return new EncoderTuning(slices, disablePreprocessing, string.Join("；", notes));
}

/// <summary>
/// 探测本机的编码引擎数；探不到返回 <c>null</c>。
///
/// <para>
/// ⚠️ <b><c>null</c> 与 <c>0</c> 必须分开</b>（同 <c>EncoderCapabilities.EncodeEngines</c> 的纪律）：
/// <c>0</c> = 明确没有硬编，<c>null</c> = 没测出来。这里两者在决策上都落到"不切"，
/// 但日志里那句话不同 —— 前者是硬件结论，后者是"我们没问到"。
/// </para>
/// </summary>
static async Task<int?> ProbeEncodeEnginesAsync()
{
    try
    {
        var report = await new WindowsEncoderCapabilityProbe()
            .ProbeAsync(CancellationToken.None)
            .ConfigureAwait(false);

        int? best = null;

        foreach (var adapter in report.Adapters)
        {
            // 软件光栅器报出来的"引擎"是假的（基础渲染器没有 NVENC/QSV）。
            if (adapter.IsSoftwareRenderer || adapter.EncodeEngines is not { } engines) continue;

            if (best is null || engines > best.Value) best = engines;
        }

        return best;
    }
    catch
    {
        // 探测失败不是错误：拿不到就按单引擎处理，别让托盘起不来。
        return null;
    }
}

/// <summary>
/// 极简文件日志。
/// <para>
/// 托盘是 WinExe（没有控制台），所以**必须**留下可查的痕迹 ——
/// 否则"托盘没出现"这类故障无从下手。写到 %TEMP% 而不是数据目录：
/// 数据目录由服务（SYSTEM）创建，普通用户往往没有写权限。
/// </para>
/// </summary>
static void Log(string message)
{
    try
    {
        var path = Path.Combine(Path.GetTempPath(), "ControlServer.Agent.Tray.log");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";
        File.AppendAllText(path, line);
    }
    catch
    {
        // 记日志失败不该影响托盘本身。
    }
}

/// <summary>
/// <b>M5.5 的编码器调优档位</b>：手段 2（SFE 切片）＋ 手段 10（关预处理）。
///
/// <para>
/// 🔥 <b>为什么这两档必须由托盘决定，而不是由中心下发</b>：
/// 编码器就建在<b>这个进程</b>里，而"这台机器有几个 NVENC 引擎"只有本机探得到
/// —— 中心看不见被控端的 GPU。同一块卡在展厅机与办公机上引擎数可能不同。
/// </para>
///
/// <para>
/// ⚠️ <b>两个环境变量是刻意的"诊断旋钮"</b>（<c>CONTROLSERVER_RD_SLICES</c> /
/// <c>CONTROLSERVER_RD_KEEP_PREPROC</c>）：真机上要把"切片到底有没有变成真并行"验出来，
/// 唯一的办法是 <b>A/B 两次实测帧率</b>，而改配置重启比重新编译快一个数量级。
/// 不设变量 = 完全按探测结果走（现有行为一个字都不改）。
/// </para>
///
/// <para>
/// ⚠️ 声明在文件<b>最末尾</b>，不是随手放的：这是顶层语句（top-level statements）文件，
/// 类型声明必须排在<b>所有语句与本地函数之后</b>，否则编译直接报
/// "Top-level statements must precede namespace and type declarations"。
/// </para>
/// </summary>
/// <param name="SliceCount">每帧切几片；0 = 不切。</param>
/// <param name="DisablePreprocessing">是否关掉编码器侧预处理。</param>
/// <param name="Note">要打进启动日志的那句话（<c>null</c> = 没什么可说的）。</param>
readonly record struct EncoderTuning(int SliceCount, bool DisablePreprocessing, string? Note);
