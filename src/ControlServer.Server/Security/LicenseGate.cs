using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ControlServer.Server.Security;

/// <summary>授权闸门对某个端点的分类结果。</summary>
public enum LicenseGateAccess
{
    /// <summary>未授权时<b>仍然放行</b>。</summary>
    Allowed,

    /// <summary>未授权时<b>拦截</b>（<c>403 license-required</c>）。</summary>
    Gated,
}

/// <summary>
/// 授权闸门的<b>路由分类表</b>（纯静态，无状态、可单测）。
///
/// <para>
/// <b>为什么闸门不是"一律 403"</b>：服务版的"中控价值"在于
/// <b>动作执行 / 对接码监听 / 定时触发</b>。把整个 <c>/api</c> 都拦掉会让现场
/// <b>连授权文件都导不进去</b>（导入授权本身也是 <c>/api</c>），于是陷入
/// "没授权 → 不能导入授权" 的死结，只能去手工改文件或往数据目录拷文件 —— 这比不做授权更糟。
/// 因此闸门只卡价值点，登录 / 只读 / 授权导入 / 用户管理 / 配置写入一律放行。
/// </para>
///
/// <para>
/// <b>fail-closed</b>：表里<b>查不到</b>的路由按 <see cref="LicenseGateAccess.Gated"/> 处理。
/// 新增端点若忘了归类，先是被 <c>LicenseGateTests.Every_api_endpoint_is_explicitly_classified</c>
/// 在测试阶段直接判失败，真漏到生产也只是"这个新端点被拦"而不是"新端点白送"。
/// </para>
///
/// <para>
/// <b>为什么按"方法 + 路由模板"而不是按路径前缀</b>：<c>/api/actions</c> 下
/// <c>execute</c>（要拦）与 <c>preview</c>（纯计算，放行）语义相反，
/// 前缀匹配会把两者一锅端。层级更深的 <c>/api/timers/{refId}/fire</c> 同理
/// （<c>GET /api/timers</c> 是只读总览，必须放行）。
/// </para>
/// </summary>
public static class LicenseGate
{
    /// <summary>响应体里的机器可读错误码。</summary>
    public const string DeniedCode = "license-required";

    /// <summary>拦截时的用户可读说明。</summary>
    public const string DeniedMessage =
        "本机尚未获得授权（或授权已过期）：动作执行、对接码监听与定时触发已停用。"
        + "请到「授权」页导入授权文件，或联系供应方获取。";

    /// <summary>闸门<b>只</b>作用于这个前缀之下；探活端点与 SignalR 不在其中。</summary>
    private const string ApiPrefix = "api/";

    private static readonly IReadOnlyDictionary<string, LicenseGateAccess> Routes = BuildRoutes();

    /// <summary>路由是否已在分类表里显式归类。</summary>
    public static bool IsClassified(HttpMethod? method, string? routeTemplate)
        => TryClassify(method, routeTemplate, out _);

    /// <summary>取分类结果；未归类按 fail-closed 返回 <see cref="LicenseGateAccess.Gated"/>。</summary>
    public static LicenseGateAccess Classify(HttpMethod? method, string? routeTemplate)
        => TryClassify(method, routeTemplate, out var access) ? access : LicenseGateAccess.Gated;

    /// <summary>尝试归类。返回 <c>false</c> 表示"没归类"（而不是"放行"）。</summary>
    public static bool TryClassify(
        HttpMethod? method,
        string? routeTemplate,
        out LicenseGateAccess access)
    {
        access = LicenseGateAccess.Gated;

        if (string.IsNullOrWhiteSpace(routeTemplate)) return false;

        return Routes.TryGetValue(Key(method, routeTemplate), out access);
    }

    /// <summary>
    /// 当前请求是否命中被拦的分类。
    /// <para>
    /// 只看 <c>/api</c> 之下：<c>/healthz</c>、<c>/readyz</c> 被闸会让服务被运维判死；
    /// <c>/hubs/events</c> 被闸会让"授权页"上的实时日志断掉，看起来像服务挂了。
    /// </para>
    /// </summary>
    public static bool IsGated(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) return false;

        // 端点由 UseRouting 解析（WebApplication 会自动把 UseRouting 插到管线最前），
        // 因此这里拿得到路由模板 —— 比手工拼路径可靠得多。
        if (context.GetEndpoint() is not RouteEndpoint route) return false;

        var template = route.RoutePattern.RawText;
        if (string.IsNullOrWhiteSpace(template)) return false;
        if (!template.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;

        if (methods is null || methods.Count == 0)
        {
            // 没有方法约束（理论上控制器端点不会出现）⇒ 无法证明它"放行"，按 fail-closed 拦。
            return true;
        }

        foreach (var method in methods)
        {
            if (!TryClassify(new HttpMethod(method), template, out var access)) return true;
            if (access == LicenseGateAccess.Gated) return true;
        }

        return false;
    }

    private static string Key(HttpMethod? method, string routeTemplate)
        => $"{(method?.Method ?? "*").ToUpperInvariant()} {routeTemplate.ToLowerInvariant()}";

    /// <summary>
    /// 全量分类表。**新增 <c>/api</c> 端点必须在这里补一行**，否则
    /// <c>LicenseGateTests</c> 的守门用例会失败（fail-closed 的第一道闸）。
    /// </summary>
    private static IReadOnlyDictionary<string, LicenseGateAccess> BuildRoutes()
    {
        // ⚠️ 键统一小写、比较器忽略大小写：RoutePattern.RawText **保留声明时的大小写**
        //    （`{fileName}`、`{refId}`），而框架的路由匹配本身是大小写不敏感的。
        //    不归一化就会出现一种极其隐蔽的失败 —— 只有"参数名带大写"的那几个端点查不到，
        //    于是它们被 fail-closed 一视同仁地拦掉：带 `{refId}` 的手动触发被拦是"对的"，
        //    但 `GET api/config/images/{fileName}` 一并被拦就纯属误伤。
        //    这个坑由 LicenseGateTests.Every_api_endpoint_is_explicitly_classified 抓到过。
        var map = new Dictionary<string, LicenseGateAccess>(StringComparer.OrdinalIgnoreCase);

        void Add(LicenseGateAccess access, params string[] keys)
        {
            foreach (var key in keys) map[key.ToLowerInvariant()] = access;
        }

        // ─────────────────────────────────────────────────────────
        // ⛔ 未授权时拦截：中控的"价值点"
        //
        // 判据是"会不会真的对外产生作用"：发报文、执行动作、触发按钮。
        // 只读查询与纯计算（preview）不在此列。
        // ─────────────────────────────────────────────────────────
        Add(LicenseGateAccess.Gated,
            "POST api/actions/execute",      // 执行一组动作（核心价值点）
            "POST api/timers/{refId}/fire",  // 手动触发定时点 —— 同样是执行按钮动作
            "POST api/test/udp",             // 向任意目标发 UDP 报文
            "POST api/test/loopback",        // 自环：把报文打进对接码链路（会真的触发派发）

            // ── 客户端 Agent（M4.1）──
            // 只有"远程重启 Agent"真的作用到别的机器上；客户机列表 / 台账 / 安装包下载全部放行 ——
            // 理由与"授权导入必须通"一致：未授权时现场仍然要能看到设备、能拿到安装包去装机。
            "POST api/agents/{id:int}/reboot",

            // ── 文件分发（M4.4）──
            // 判据仍是"会不会真的作用到别的机器上"：**建立分发任务**就是让一批客户机去
            // 下载/上传文件，与"远程重启"同类，因此归 Gated。
            // ⚠️ 反过来，仓库本身（上传 / 列表 / 下载 / 删除）全是**服务端本地**的事，
            //    一条都没离开这台机器，所以放行（否则未授权时连"把安装素材放进仓库"都做不了）。
            "POST api/distributions",
            "POST api/distributions/collect",

            // ── 客户机配置与指令（M4.5）──
            // 判据仍然是"会不会真的作用到别的机器上"：
            //   · 电源 / 音量 —— 直接对某台机器下手（关机、改音量），与"远程重启"同类。
            //   · 保存配置 / 要求重拉 —— 配置一旦被应用，客户机就会按它**发报文、启动程序**，
            //     效果与"建立分发任务"完全一样（都是"让一批机器去做事"）。
            // ⚠️ 反过来，**读**配置（GET）放行：那是纯读取，而且未授权时客户机仍要能拉到
            //    自己的配置才能正常工作 —— 与"在线表放行"是同一条判据。
            // 本机告知"我要换中心了"（2026-10-15）：**只写日志、不改任何状态**，
            // 而且只能报到自己名下 ⇒ 与"读自己的配置"同一判据，放行。
            "POST api/agents/{id:int}/server-change",
            // WoL（2026-10-15）：给一台**关着的**机器发开机包 —— 同样"作用到别的机器上"。
            "POST api/agents/{id:int}/wake",
            "POST api/agents/{id:int}/power",
            "POST api/agents/{id:int}/volume",
            // M4.6：经 UDP 通道代发指令 —— 与上面两条同类（都是"对某台机器下关机令"），
            // 只是走的是 UDP 而非 SignalR。判据不变：会不会真的作用到别的机器上。
            "POST api/agents/{id:int}/udp-command",
            "PUT api/agents/{id:int}/config",
            "POST api/agents/{id:int}/config/redeliver",

            // ── 远程桌面（M5.0）──
            // 判据仍是"会不会真的作用到别的机器上"，而且这里的"作用"是本方案里最重的一类：
            //   · 建会话 —— 让被控端**开始抓屏并推流**（耗 GPU、耗带宽），与"建立分发任务"同类；
            //   · 灰度开关 —— 改变被控端**编码参数**（决策 14）；
            //   · 输入回传 —— **直接操作别人的鼠标键盘**。这一档的风险性质与前两者不同，
            //     M5 也据此把"能看"与"能控"拆成两期；但在授权判据上它们同属 Gated。
            //   · 允许控制的闸门（M5.2）—— 它本身**不碰网络、只改中心内存里那一位**，
            //     看起来像"服务端本地的事"、该放行。但它打开的是**上面那条输入通路**，
            //     按"会不会真的作用到别的机器上"这条判据，它是那条通路的总开关 ——
            //     放行等于"未授权时也能把远程控制打开"。所以归 Gated。
            //   · 质量档位（M5.5）—— 改的是被控端的**编码参数**，与灰度同类；
            //   · 写剪贴板（M5.5）—— **改被控机的全局状态**（所有程序的粘贴都会变），
            //     与"点别人的鼠标"同一判据：会不会真的作用到别的机器上；
            //   · 读剪贴板 —— 看起来只是"读"，但它读的是**别人机器上的内容**，
            //     可能是一段密码。按"能不能看到别人的东西"归 Gated，与"只读观看"同一档。
            //   · 视口（M5.5 ROI）—— 改的是被控端的**编码区域**（重建编码器、改变它编什么），
            //     与质量档位同类：都会真的改变那台机器上发生的事。
            //   （M7.2 的隐私屏已在 2026-10-19 整体移除，见 docs/M7.9 与铁律 19：
            //     删功能必须同时删掉这里的分类项 —— 留一条不存在的路由等于给自己一个假的安全感。）
            "POST api/remote/sessions",
            "POST api/remote/sessions/{sessionId}/grayscale",
            "POST api/remote/sessions/{sessionId}/quality",
            "POST api/remote/sessions/{sessionId}/viewport",
            "POST api/remote/sessions/{sessionId}/input",
            "POST api/remote/sessions/{sessionId}/input/enabled",
            "POST api/remote/sessions/{sessionId}/clipboard",
            "POST api/remote/sessions/{sessionId}/clipboard/read");

        // ─────────────────────────────────────────────────────────
        // ✅ 未授权时放行
        // ─────────────────────────────────────────────────────────
        Add(LicenseGateAccess.Allowed,
            // ── 认证 ──
            // 不放行连"导入授权"都做不了；改密闸门是另一条独立闸门，不在这里重复。
            "POST api/auth/login",
            "POST api/auth/logout",
            "GET api/auth/me",
            "POST api/auth/change-password",

            // ── 授权自身：现场唯一的自救通道，必须通 ──
            "GET api/license",
            "GET api/license/request",
            "POST api/license/import",
            "POST api/license/trial",
            "DELETE api/license",

            // ── 编码预览：纯计算，不产生任何副作用 ──
            "POST api/actions/preview",

            // ── 客户机路径记忆（2026-10-14）──
            // 判据同"文件仓库"：它是**服务端本地**的一条记录（把某个目录存进自己的库），
            // 不碰任何客户机、也不产生任何对外作用。拦它只会让未授权时连"少点几次鼠标"都做不到。
            "PUT api/agents/{id:int}/path-memory",

            // ── 指令库 ──
            "GET api/commands",
            "POST api/commands",
            "PUT api/commands/{id}",
            "DELETE api/commands/{id}",

            // ── 配置：现场可能需要先配好，待授权后即用（前端另行提示）──
            "GET api/config",
            "PUT api/config",
            "POST api/config/reset",
            "GET api/config/export",
            "POST api/config/import",
            "GET api/config/images/{fileName}",
            "POST api/config/images",
            "DELETE api/config/images/{fileName}",

            // ── 只读：让现场能看到"为什么不能用" ──
            "GET api/logs/recent",
            "GET api/status",
            "GET api/traffic/recent",
            "GET api/traffic/count",
            "DELETE api/traffic",
            "GET api/timers",

            // ── 系统与运维：不该被授权卡住 ──
            "GET api/system/machine-code",
            "GET api/system/info",
            "POST api/system/timesync",
            "GET api/system/address",
            "POST api/system/address",

            // ── 用户管理 ──
            "GET api/users",
            "GET api/users/roles",
            "POST api/users",
            "PUT api/users/{id:int}",
            "POST api/users/{id:int}/password",
            "DELETE api/users/{id:int}",

            // ── 客户端 Agent（M4.1）──
            // 在线表与台账是"看"，安装包下载是现场自救链路（没它就没法装机）。
            // 这些都不该被设备授权卡住；真正的"作用于别的机器"的操作已在上面归类为 Gated。
            "GET api/agents",
            "GET api/agents/{id:int}",
            "PUT api/agents/{id:int}",
            "DELETE api/agents/{id:int}",
            "POST api/agents/{id:int}/block",
            "POST api/agents/{id:int}/unblock",
            "POST api/agents/register",
            "GET api/downloads/agent-installer/info",
            "GET api/downloads/agent-installer",

            // ── 客户端遥测（M4.2）──
            // 历史曲线 / 开关机记录 / 硬件清单三件全是**只读**：
            // 它们不向任何机器发出东西，与"在线表放行"是同一条判据（见上面 Gated 段的注释）。
            // ⚠️ 别因为"这是核心功能"就把它们归到 Gated —— 未授权时现场恰恰最需要看到
            //    "这台机器为什么老异常关机"，把它藏起来只会让人以为软件坏了。
            // 这些端点漏归类会被 fail-closed 拦掉，而失败形态是"有授权时一切正常、
            // 没授权时详情页一片空白"，极难联想到授权闸门 —— 由
            // LicenseGateTests.Every_api_endpoint_is_explicitly_classified 钉住。
            "GET api/agents/{id:int}/metrics",
            "GET api/agents/{id:int}/power-events",
            "GET api/agents/{id:int}/inventory",

            // ── 文件仓库与传输（M4.4）──
            // 仓库的增删查下载全在服务端本地完成（字节不出这台机器），放行。
            // 客户机那三个端点（补领 / 进度 / 完成）是**机器对自身任务的回报**，
            // 只有任务真的建起来了才会有流量 —— 而建任务已经在上面归 Gated 了，
            // 因此它们本身放行不会绕开闸门。
            "GET api/files",
            "POST api/files/uploads",
            "GET api/files/uploads/{uploadId}",
            "PUT api/files/uploads/{uploadId}",
            "POST api/files/uploads/{uploadId}/complete",
            "DELETE api/files/uploads/{uploadId}",
            "GET api/files/{id:int}/download",
            "DELETE api/files/{id:int}",
            "GET api/distributions",
            "GET api/distributions/{id:int}",
            "GET api/agents/transfers",
            "POST api/agents/transfers/{itemId:int}/progress",
            "POST api/agents/transfers/{itemId:int}/complete",

            // ── 客户机目录浏览（M4.4+）──
            // 🗂 它确实"伸到了别的机器上"，但**什么都没让那台机器做**：只是读一个目录列表
            // （与"读配置""读遥测"同一条判据：只读查询不产生作用）。
            // ⚠️ 真正"作用到别的机器"的是**建回传任务**本身，它已经在上面归 Gated ——
            //    所以放行浏览不会绕开闸门：未授权时看得见文件，但仍然取不回来。
            // 把它归 Gated 的代价则是"未授权时连挑路径都做不到"，而那时现场恰恰需要
            // 用这个下拉框来避免手输路径出错（这也是本条需求的原话）。
            "GET api/agents/{id:int}/browse",

            // ── 客户机「接收到的数据」（2026-10-13）──
            // 🗂 与目录浏览同一条判据：**纯读取，什么都没让那台机器做**。
            // 它读的是客户机内存里的一份接收记录（"我收到了什么、判成了什么"），
            // 唯一的用途是排查"指令不生效"—— 而这恰恰是未授权/试用期最可能发生的时段，
            // 归 Gated 等于"最需要诊断的时候偏偏看不了"。真正的动作（发指令、下发文件）
            // 全部另有闸门，这里放行不会绕开任何一条。
            "GET api/agents/{id:int}/received",

            // ── 客户机配置：读取（M4.5）──
            // 客户机启动 / 收到变更通知后要拉自己的配置来执行任务；这是纯读取，
            // 不向任何机器发出东西。把它归 Gated 会导致"未授权时客户机拉不到配置、
            // 连本地任务都跑不了" —— 而未授权最该保证的恰恰是"现场能继续运转"。
            "GET api/agents/{id:int}/config",

            // ── 远程桌面：读取与"只做减法"（M5.0）──
            // 能力探测是纯读取（与"读硬件清单"同一条判据）。
            "GET api/remote/capabilities/{agentId:int}",

            // 屏清单（M5.5）：纯枚举，不改变任何状态。它回答的是"这台机器有几块屏、
            // 各多大"—— 与能力探测同性质，而且**必须**在未授权时可读：
            // 否则页面连"你能选哪块屏"都画不出来。
            "GET api/remote/screens/{agentId:int}",

            "GET api/remote/sessions",
            "GET api/remote/sessions/{sessionId}",
            "GET api/remote/sessions/{sessionId}/clipboard",

            // 🔥 DELETE（结束会话）**刻意放行** —— 它是"只做减法"：让被控端**停止**抓屏。
            //    归 Gated 会造成一个很难受的后果 —— **未授权时中断不了正在进行的远控**。
            //    与 `POST api/agents/{id:int}/unblock`（同样是只做减法）被归在 Allowed 同理：
            //    闸门卡的是"让别的机器干活"，不是"让它停下来"。
            "DELETE api/remote/sessions/{sessionId}");

        return map;
    }
}
