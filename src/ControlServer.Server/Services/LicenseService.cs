using System.Runtime.InteropServices;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Licensing;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Infrastructure.Persistence.Entities;
using ControlServer.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.Services;

/// <summary>
/// 设备授权的运行时服务：状态缓存、导入、试用生成、审计、变更推送。
///
/// <para>
/// <b>为什么是 Singleton + 进程级缓存</b>：授权是<b>设备级</b>状态，
/// 一次变更对所有在线会话同时生效。每个请求各自读盘会让"谁看到的是新状态"变得不确定，
/// 而闸门中间件在每个请求上都要问一次"能不能执行动作" —— 那里绝不能做文件 IO。
/// 因此：状态常驻内存（<see cref="State"/> 供闸门同步读取），
/// 由本类在变更后与后台定时刷新时更新它。
/// </para>
///
/// <para>
/// <b>刷新策略</b>：变更后立即刷新；后台每 <see cref="RefreshInterval"/> 刷新一次
/// （为了让"试用到点"在运行中也能被发现，而不是等重启）；每次读接口时也刷新一次
/// （单文件几百字节，代价可忽略，换来"手工把授权文件拷进数据目录却毫无反应"这类动作能被立刻看到）。
/// </para>
///
/// <para>
/// <b>与 RBAC 的关系</b>：授权判的是<b>设备</b>，不是<b>人</b>。两者的闸门相互独立 ——
/// 一个是"这台机器有没有被授权"，一个是"这个人有没有权限"，语义不同，白名单也不同。
/// </para>
/// </summary>
public sealed class LicenseService
{
    /// <summary>后台刷新间隔。60 秒足以让"试用到期"在分钟级被发现。</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly ILicenseStore _store;
    private readonly ILicenseSignatureVerifier _verifier;
    private readonly ILicenseTimeSource _timeSource;
    private readonly IMachineCodeProvider _machineCodeProvider;
    private readonly INotifier _notifier;
    private readonly LogService _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LicenseOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>刷新串行化：导入与后台刷新可能同时发生，不能让两个结果互相覆盖。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// 本机机器码。缓存一次即可 —— 它由硬件决定，进程生命周期内不会变，
    /// 而 Windows 实现要走 WMI 查询，每个请求算一遍纯属浪费。
    /// </summary>
    private readonly Lazy<string> _machineCode;

    private volatile LicenseState _state = LicenseState.Damaged("授权状态尚未初始化。");
    private volatile LicenseDto? _cachedDto;

    /// <summary>
    /// 本机试用记录。
    /// <para>
    /// <c>volatile</c>：<see cref="EffectiveEnforcement"/> 会读它，而那个属性被 HTTP 请求线程
    /// 与后台刷新线程同时读取（闸门中间件在每个请求上都要问一次），必须保证可见性。
    /// </para>
    /// </summary>
    private volatile LocalTrialRecord? _trial;

    public LicenseService(
        ILicenseStore store,
        ILicenseSignatureVerifier verifier,
        ILicenseTimeSource timeSource,
        IMachineCodeProvider machineCodeProvider,
        INotifier notifier,
        LogService log,
        IServiceScopeFactory scopeFactory,
        IOptions<ServerOptions> options,
        TimeProvider timeProvider)
    {
        _store = store;
        _verifier = verifier;
        _timeSource = timeSource;
        _machineCodeProvider = machineCodeProvider;
        _notifier = notifier;
        _log = log;
        _scopeFactory = scopeFactory;
        _options = options.Value.License;
        _timeProvider = timeProvider;

        _machineCode = new Lazy<string>(() =>
        {
            try
            {
                return _machineCodeProvider.GetMachineCode();
            }
            catch (Exception ex)
            {
                // 拿不到机器码 = 任何授权都绑不上。明确报出来，不要给出一个假的机器码
                // （那样会签发出一份"永远绑不上"的授权，返工成本远高于当场失败）。
                _log.Warn("License", $"读取机器码失败：{ex.Message}");
                return string.Empty;
            }
        });
    }

    /// <summary>本机机器码（<c>XXXX-XXXX-XXXX-XXXX</c>）。</summary>
    public string MachineCode => _machineCode.Value;

    /// <summary>
    /// 当前授权状态。<b>同步、免 IO</b> —— 闸门中间件在每个请求上读它。
    /// </summary>
    public LicenseState State => _state;

    /// <summary>设备当前是否可用（走白名单，见 <see cref="LicenseState.IsUsable"/>）。</summary>
    public bool IsUsable => _state.IsUsable;

    /// <summary>配置里写的执行档位。</summary>
    public LicenseEnforcement ConfiguredEnforcement => _options.Enforcement;

    /// <summary>
    /// <b>实际生效</b>的执行档位。
    ///
    /// <para>
    /// 与配置值的差异只有一处，但它很关键：<b>配置为 <see cref="LicenseEnforcement.Warn"/> 且本机试用记录已存在时，
    /// 升为 <see cref="LicenseEnforcement.Enforce"/></b>。
    /// </para>
    ///
    /// <para>
    /// 为什么：默认档位是 Warn，为的是"升级当天绝不锁死"。但若永远停在 Warn，
    /// 试用到期后核心功能照样能用，试用就成了一句提示而非约束。
    /// 本机试用记录的存在，恰好标志着"这台设备已经进入过授权周期" ——
    /// 此时收紧为 Enforce，既保住了升级安全，又让试用真正有期限。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 注意这里判断的是<b>记录是否存在</b>，而不是"试用是否有效"。
    /// 否则试用一过期，档位就会退回 Warn，拦截随之失效。
    /// </para>
    /// </summary>
    public LicenseEnforcement EffectiveEnforcement
        => _options.Enforcement switch
        {
            LicenseEnforcement.Off => LicenseEnforcement.Off,
            LicenseEnforcement.Enforce => LicenseEnforcement.Enforce,
            _ => _trial is not null ? LicenseEnforcement.Enforce : LicenseEnforcement.Warn,
        };

    /// <summary>时钟容差。</summary>
    public TimeSpan ClockTolerance => TimeSpan.FromSeconds(_options.ClockToleranceSeconds);

    /// <summary>
    /// 每次<b>重读过授权状态</b>、且状态（或试用记录的有无）确实发生变化后触发。
    ///
    /// <para>
    /// 触发源：启动引导、60 秒后台刷新、以及导入 / 生成试用 / 移除授权这三个显式动作
    /// （它们内部都会调用 <see cref="RefreshAsync"/>）。订阅方是授权闸门
    /// （<c>LicenseGateState</c>）—— 它据此<b>起停</b>对接码监听这类后台能力，
    /// 而不是各自轮询（轮询要么慢，要么一直在空转）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 事件在<b>刷新锁之外</b>触发，订阅方可以安全地做 IO。
    /// 但反过来说：订阅方<b>不要</b>在同一线程里再调 <see cref="RefreshAsync"/>，
    /// 那虽然不会死锁（锁已释放），却会让一次刷新变成无限递归。
    /// </para>
    /// </summary>
    public event Action? Refreshed;

    // ═══════════════════════════════════════════════════════════
    // 读
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 取当前授权状态（全量 DTO）。
    /// <para>
    /// 默认每次都重新读盘：授权文件只有几百字节，而每次读盘换来的是
    /// "直接把 <c>.lic</c> 拷进数据目录"这种运维动作能被立刻看到 ——
    /// 在没有前端可用的早期阶段，这是唯一的导入方式。不加节流是刻意的取舍。
    /// </para>
    /// </summary>
    public async Task<LicenseDto> GetAsync(bool forceRefresh = true, CancellationToken cancellationToken = default)
    {
        if (forceRefresh)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return _cachedDto ?? BuildDto(_state, null, _trial);
    }

    /// <summary>重新读盘并重新判定。状态变化时触发 <see cref="Refreshed"/>。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 变化检测用到的"旧值"。刻意放在锁外读：刷新本身是串行的，
        // 最坏情况只是多/少广播一次事件，不值得为它多占一次锁。
        var previousState = _state;
        var previousHadTrial = _trial is not null;
        var changed = false;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var trial = await _store.ReadTrialAsync(cancellationToken).ConfigureAwait(false);

            _trial = trial;

            _state = Evaluate(snapshot, trial);
            _cachedDto = BuildDto(_state, snapshot, trial);

            changed = !Equals(previousState, _state) || previousHadTrial != (trial is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 读盘失败（文件被占用 / 权限异常）绝不能把进程带崩，也不能静默降级成"未授权" ——
            // 那会让一次临时的 IO 抖动变成"授权突然失效"，现场无从判断。
            _log.Warn("License", $"读取授权状态失败：{ex.Message}");
            _state = LicenseState.Damaged($"读取授权状态失败：{ex.Message}");
            _cachedDto = BuildDto(_state, null, _trial);

            changed = !Equals(previousState, _state);
        }
        finally
        {
            _refreshGate.Release();
        }

        // ⚠️ 事件必须在锁外触发：订阅方（LicenseGateState → 起停 UDP 监听）会做 IO，
        //    塞进锁里会让"导入授权"这个请求一直握着锁等监听启停完成。
        if (changed) RaiseRefreshed();
    }

    // ═══════════════════════════════════════════════════════════
    // 写：导入 / 试用 / 移除
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 导入一份厂商授权的 Base64 文本。
    /// </summary>
    public async Task<LicenseMutationResult> ImportAsync(
        string? licenseText,
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        // ── 解析 ──
        if (!LicenseCodec.TryDecode(licenseText, out var document, out var parseError))
        {
            return await FailAsync(
                LicenseImportStatus.Malformed,
                parseError ?? "授权文本无法解析。",
                actor,
                "import-malformed",
                cancellationToken).ConfigureAwait(false);
        }

        // ── 验签 ──
        // ⚠️ 必须用**重算出来的载荷**验签（而不是原文里的那段 JSON 字节）：
        //    JSON 允许重复键，拿原文验证会给"在文件里塞第二个 mc 字段"留下空间。
        var signatureValid = false;
        string? signatureError = null;

        if (!_verifier.IsAvailable)
        {
            signatureError = _verifier.UnavailableReason ?? "验签器不可用。";
        }
        else
        {
            signatureValid = _verifier.Verify(
                LicenseCodec.BuildPayload(document!), document!.Sig);
        }

        // ── 业务规则 ──
        var existing = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var result = LicenseImportRules.Check(
            document!,
            signatureValid,
            MachineCode,
            now,
            existing.Binding,
            ClockTolerance);

        if (!result.Ok)
        {
            // 失败也必须记审计：反复导入同一份"机器码不符"的文件，是现场最典型的
            // "拿错了授权文件"信号，事后要靠这条日志还原。
            // 结果码统一转小写 —— 与 HTTP 响应里的 `status` 保持同一个词形，
            // 否则日志里写 `MachineMismatch`、接口里返回 `machinemismatch`，
            // 排查时要先做一次心算映射。
            await AuditAsync(
                    actor,
                    $"license-import-rejected:{result.Status.ToString().ToLowerInvariant()}",
                    result.Message,
                    cancellationToken)
                .ConfigureAwait(false);

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return new LicenseMutationResult(false, result.Status.ToString().ToLowerInvariant(), result.Message, _cachedDto!);
        }

        // ── 落盘 ──
        // 存的是**规范化重编码**后的文本，而不是用户粘贴的原文：
        // 粘贴过程中常见的折行、缩进、URL-safe 变形都会在这里被抹平，
        // 落盘的文件永远与 LicenseCodec 的规范形态一致（签名覆盖的是规范化载荷，所以依然有效）。
        await _store
            .WriteAsync(LicenseCodec.Encode(document!), result.Binding, cancellationToken)
            .ConfigureAwait(false);

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        var message = result.Binding is null
            ? $"授权导入成功：{LicenseKind.DisplayName(document!.Lt)}。"
            : $"通用授权导入成功，已绑定本机机器码 {MachineCode}。";

        await AuditAsync(actor, "license-imported", $"{message} 文件绑定：{DescribeBinding(document!)}", cancellationToken)
            .ConfigureAwait(false);

        await NotifyAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("License", $"{message}（操作者 {actor.Name}）");

        return new LicenseMutationResult(true, "ok", message, _cachedDto!);
    }

    /// <summary>
    /// 生成本机试用（首次启动自动调用，也可由管理员手动触发）。
    /// </summary>
    public async Task<LicenseMutationResult> GenerateTrialAsync(
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);

        // 已有厂商授权（含已过期）时不允许再生成试用 —— 与既有实现的门控分级一致：
        // "正式授权过期"只给"导入"这一条路，不给"退回试用"这条路。
        if (snapshot.Exists && snapshot.Document is not null)
        {
            const string message = "本机已导入厂商授权，无需生成试用。";
            return new LicenseMutationResult(false, "already-licensed", message, _cachedDto!);
        }

        var existingTrial = await _store.ReadTrialAsync(cancellationToken).ConfigureAwait(false);

        // 试用仍在有效期内时不允许重刷 —— 否则"重新生成"就成了永久免费的按钮。
        if (existingTrial is not null && _state.Status == LicenseStatus.Trial)
        {
            var message = $"本机试用仍在有效期内（剩余 {_state.RemainingDays} 天），无需重新生成。";
            return new LicenseMutationResult(false, "trial-active", message, _cachedDto!);
        }

        // 机器码拿不到就没法绑定，试用了也认不出本机。
        if (string.IsNullOrWhiteSpace(MachineCode))
        {
            const string message = "无法读取本机机器码，试用生成失败。";
            return new LicenseMutationResult(false, "machine-code-unavailable", message, _cachedDto!);
        }

        // ── 起点时间 ──
        var anchor = await ResolveAnchorAsync(cancellationToken).ConfigureAwait(false);

        if (anchor is null)
        {
            // 走到这里只可能是 RequireNetworkTime=true 且联网不可得。
            const string message = "无法获取网络时间，暂时不能生成试用。请检查网络后重试，或联系供应方获取正式授权。";
            return new LicenseMutationResult(false, "no-network-time", message, _cachedDto!);
        }

        var (anchorUtc, anchorSource) = anchor.Value;

        // ⚠️ 减 1，不是笔误：`ed` 的语义是"**当日末刻**才到期"（见 LicenseEvaluator.ResolveExpiry，
        //    客户直觉里"11 月 7 日到期"意味着 7 号一整天都还能用）。因此"起算日 + N 天"
        //    会让 30 天试用实际覆盖 31 个自然日（10-09 至 11-08 全含），
        //    而 1 天试用会跨到第二天 —— 徽章上也会直接显示成"试用 31 天"，与发放的 30 天自相矛盾。
        //    "N 天试用"= 含起算日在内的 N 个自然日，末刻落在第 N 天，故为 anchor + (N-1)。
        var expiresOn = BeijingTime
            .ToBeijing(anchorUtc)
            .AddDays(_options.TrialDays - 1)
            .ToString("yyyyMMdd");

        var record = new LocalTrialRecord(
            MachineCode,
            anchorUtc.ToUnixTimeMilliseconds(),
            expiresOn,
            _options.TrialDays,
            anchorSource);

        await _store.WriteTrialAsync(record, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        var summary = $"已生成本机试用：{_options.TrialDays} 天，至 {_state.ExpiresAt:yyyy-MM-dd}。";

        await AuditAsync(actor, "license-trial-generated", $"{summary} 起点来源：{anchorSource}", cancellationToken)
            .ConfigureAwait(false);

        await NotifyAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("License", $"{summary}（操作者 {actor.Name}，起点来源 {anchorSource}）");

        return new LicenseMutationResult(true, "ok", summary, _cachedDto!);
    }

    /// <summary>移除厂商授权（回到未授权 / 本机试用状态）。</summary>
    public async Task<LicenseMutationResult> RemoveAsync(
        ActorContext actor,
        CancellationToken cancellationToken = default)
    {
        await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        // ⚠️ 移除授权**不重置本机试用**（LicenseFileStore.DeleteAsync 刻意不删试用记录）：
        // 否则"移除授权 → 重启 → 又白拿 30 天"就成了一条免费通道。
        var message = _state.Status == LicenseStatus.Expired
            ? "已移除授权文件。本机试用期已结束，请导入新的授权文件。"
            : "已移除授权文件。";

        await AuditAsync(actor, "license-removed", message, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(cancellationToken).ConfigureAwait(false);

        _log.Info("License", $"{message}（操作者 {actor.Name}）");

        return new LicenseMutationResult(true, "ok", message, _cachedDto!);
    }

    /// <summary>构造"授权申请文本"：管理员发给供应方，对方据此签发绑定本机的授权。</summary>
    public LicenseRequestDto BuildRequest()
    {
        var now = BeijingTime.Now;

        var text = string.Join(Environment.NewLine, new[]
        {
            "中控服务 · 授权申请",
            "────────────────────────────────",
            $"机器码    ：{MachineCode}",
            $"计算机名  ：{Environment.MachineName}",
            $"申请时间  ：{BeijingTime.Format(now)}",
            $"运行平台  ：{RuntimeInformation.OSDescription}",
            "────────────────────────────────",
            "请将以上内容发送给供应方，以获取与本机绑定的授权文件。",
        });

        return new LicenseRequestDto(MachineCode, Environment.MachineName, now, text);
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    /// <summary>把落盘快照 + 本机试用记录交给纯函数评估器。</summary>
    private LicenseState Evaluate(LicenseSnapshot snapshot, LocalTrialRecord? trial)
    {
        // 文件在、但解析不出来 → 明确报错，且**不退到本机试用**（见 LicenseEvaluationInput.ReadError）。
        var readError = snapshot.Exists && snapshot.Document is null ? snapshot.ParseError : null;

        var signatureValid = false;
        string? signatureError = null;

        if (snapshot.Document is not null)
        {
            if (!_verifier.IsAvailable)
            {
                // 公钥加载失败是服务端自身的构建问题：必须让所有授权都判为不可用（fail-closed），
                // 而不是"验不了就当通过"。
                signatureError = _verifier.UnavailableReason ?? "验签器不可用。";
            }
            else
            {
                signatureValid = _verifier.Verify(
                    LicenseCodec.BuildPayload(snapshot.Document), snapshot.Document.Sig);

                if (!signatureValid) signatureError = "签名与内置的厂商公钥不匹配。";
            }
        }

        var input = new LicenseEvaluationInput(
            snapshot.Document,
            snapshot.Binding,
            signatureValid,
            signatureError,
            MachineCode,
            _timeProvider.GetUtcNow(),
            ClockTolerance)
        {
            LocalTrial = trial,
            ReadError = readError,
        };

        return LicenseEvaluator.Evaluate(input);
    }

    /// <summary>
    /// 取试用起点时间。
    /// <list type="bullet">
    /// <item><description>优先用<b>联网校时</b>得到的网络 UTC —— 这样试用的起点不受本机时钟影响。</description></item>
    /// <item><description>联网不可得且 <c>RequireNetworkTime=false</c> 时退回本机校正时钟，并如实记录来源为 <c>local</c>。</description></item>
    /// <item><description>联网不可得且 <c>RequireNetworkTime=true</c> 时返回 <c>null</c>，由调用方给出可读提示。</description></item>
    /// </list>
    /// </summary>
    private async Task<(DateTimeOffset AnchorUtc, string Source)?> ResolveAnchorAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();

        DateTimeOffset? networkTime = null;

        try
        {
            networkTime = await _timeSource.TryGetNetworkTimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("License", $"获取网络时间失败：{ex.Message}");
        }

        if (networkTime is not null)
        {
            // ⚠️ 夹到"不晚于本机此刻"：网络时间可能比本机校正时钟稍微超前（NTP 往返、
            //    双方微秒级差异），而锚点一旦落在未来，评估器的 "now + 容差 >= ivt"
            //    就会在容差之外把自己判成"时钟被回拨"。这个 clamp 消除的是一类自伤。
            var anchor = networkTime.Value <= nowUtc ? networkTime.Value : nowUtc;
            return (anchor, LocalTrialRecord.AnchorFromNetwork);
        }

        if (_options.RequireNetworkTime)
        {
            return null;
        }

        return (nowUtc, LocalTrialRecord.AnchorFromLocalClock);
    }

    private async Task<LicenseMutationResult> FailAsync(
        LicenseImportStatus status,
        string message,
        ActorContext actor,
        string auditAction,
        CancellationToken cancellationToken)
    {
        await AuditAsync(actor, auditAction, message, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        return new LicenseMutationResult(false, status.ToString().ToLowerInvariant(), message, _cachedDto!);
    }

    /// <summary>
    /// 广播"授权状态变了"。订阅方的异常一律吞掉 —— 闸门起停失败不能把授权刷新判成失败。
    /// </summary>
    private void RaiseRefreshed()
    {
        try
        {
            Refreshed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warn("License", $"授权状态变更广播失败：{ex.Message}");
        }
    }

    private async Task NotifyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_cachedDto is not null)
            {
                await _notifier.LicenseChangedAsync(_cachedDto, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // 推送失败不该影响导入结果本身（HTTP 也已经要返回成功了）。
            _log.Warn("License", $"授权变更推送失败：{ex.Message}");
        }
    }

    private async Task AuditAsync(
        ActorContext actor,
        string action,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.AuditLogs.Add(new AuditLog
            {
                Timestamp = BeijingTime.Now,
                User = string.IsNullOrWhiteSpace(actor.Name) ? "system" : actor.Name,
                Action = action,
                Detail = detail,
                RemoteIp = actor.RemoteIp,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 审计写失败不得影响业务结论 —— 但一定要留痕，否则"审计缺失"这件事本身无迹可查。
            _log.Warn("License", $"审计写入失败（{action}）：{ex.Message}");
        }
    }

    private LicenseDto BuildDto(LicenseState state, LicenseSnapshot? snapshot, LocalTrialRecord? trial)
    {
        var document = snapshot?.Document;

        LicenseDocumentDto? documentDto = null;

        if (document is not null)
        {
            var boundMachineCode = !string.IsNullOrWhiteSpace(document.Mc)
                ? document.Mc
                : snapshot?.Binding?.MachineCode ?? string.Empty;

            // 绑定时刻：文档里自带 mc 时用文档的 bt（签发方登记），
            // 否则用侧车的绑定时刻（通用授权在本机首次导入时写入）。
            var boundAtMs = string.IsNullOrWhiteSpace(document.Mc) && snapshot?.Binding is not null
                ? snapshot.Binding.BoundAtUtcMs
                : document.Bt;

            documentDto = new LicenseDocumentDto(
                document.Sn,
                document.Vi,
                document.Hn,
                FormatBeijingDate(document.Id),
                document.Ub == 1 && string.IsNullOrWhiteSpace(document.Mc),
                boundMachineCode,
                LicenseEvaluator.FormatUtcMs(boundAtMs),
                LicenseEvaluator.FormatUtcMs(document.Ivt),
                LicenseEvaluator.FormatUtcMs(document.Ud));
        }

        var trialDto = trial is null
            ? null
            : new LicenseTrialDto(
                LicenseEvaluator.FormatUtcMs(trial.IssuedAtUtcMs),
                FormatBeijingDate(trial.ExpiresOn),
                trial.Days,
                trial.AnchorSource);

        return new LicenseDto(
            (int)state.Status,
            state.Status.ToString(),
            state.StatusDisplayName,
            state.IsUsable,
            state.Kind,
            LicenseKind.IsValid(state.Kind) ? LicenseKind.DisplayName(state.Kind) : string.Empty,
            state.RemainingDays,
            state.ExpiresAt is null ? null : BeijingTime.ToBeijing(state.ExpiresAt.Value),
            state.Info,
            state.LastError,
            MachineCode,
            EffectiveEnforcement.ToString(),
            ConfiguredEnforcement.ToString(),
            state.IsLocalTrial,
            documentDto,
            trialDto);
    }

    private static string FormatBeijingDate(string? compactDate)
        => LicenseEvaluator.TryParseBeijingDate(compactDate, out var date)
            ? date.ToString("yyyy-MM-dd")
            : string.Empty;

    private static string DescribeBinding(LicenseDocument document)
        => string.Join(' ', new[]
        {
            string.IsNullOrWhiteSpace(document.Sn) ? "(无序列号)" : document.Sn,
            string.IsNullOrWhiteSpace(document.Hn) ? string.Empty : document.Hn,
            string.IsNullOrWhiteSpace(document.Mc) ? "通用授权" : document.Mc,
        }.Where(s => s.Length > 0));
}
