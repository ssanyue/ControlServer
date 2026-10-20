using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using ControlServer.Server.Services;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 授权宿主：首次启动自动生成本机试用 + 周期刷新授权状态。
///
/// <para>
/// <b>为什么自动试用写在 <see cref="StartAsync"/> 而不是后台循环里</b>：
/// 放在后台循环会让"宿主已就绪"与"试用已生成"之间出现一个竞态窗口 ——
/// 在窗口里读状态会看到"未授权"，而试用的生成是**一瞬间**的事，这种间歇性差异
/// 在排查时极其误导（时有时无）。写在 <see cref="StartAsync"/> 里，宿主就绪即状态确定。
/// </para>
///
/// <para>
/// 代价是首次启动要多等一次网络取时（默认最多 3 个 NTP 服务器 × 超时）。
/// 这是<b>一次性</b>成本：试用记录落盘后，后续每次启动都直接跳过。
/// 而且它只发生在"既无授权也无试用"的全新机器上 —— 正是最不影响生产的一刻。
/// </para>
///
/// <para>
/// <b>为什么需要周期刷新</b>：授权状态是<b>时间相关</b>的。试用在运行中到期、
/// 或者有人直接把授权文件拷进数据目录，这些都不该等到下次重启才被发现。
/// </para>
/// </summary>
public sealed class LicenseHostedService : IHostedService, IDisposable
{
    private readonly LicenseService _license;
    private readonly ILicenseStore _store;
    private readonly IOptions<ServerOptions> _options;
    private readonly ILogger<LicenseHostedService> _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public LicenseHostedService(
        LicenseService license,
        ILicenseStore store,
        IOptions<ServerOptions> options,
        ILogger<LicenseHostedService> logger)
    {
        _license = license;
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// ⚠️ 取的是 <see cref="ServerOptions"/> 而不是 <see cref="LicenseOptions"/>。
    /// <c>IOptions&lt;LicenseOptions&gt;</c> 会从配置根（<c>License:</c>）绑定，
    /// 而实际位置是 <c>Server:License:</c> —— 那样绑出来永远是默认值，
    /// 且不会报任何错（"配置不生效"的最安静形态）。
    /// </summary>
    private LicenseOptions LicenseOptions => _options.Value.License;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await BootstrapAsync(cancellationToken).ConfigureAwait(false);

        _loopCts = new CancellationTokenSource();
        _loop = RunRefreshLoopAsync(_loopCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 停机路径绝不能再抛异常（超时/取消都视作已停）。
            }
        }
    }

    public void Dispose()
    {
        _loopCts?.Dispose();
        _loopCts = null;
    }

    // ═══════════════════════════════════════════════════════════

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _license.RefreshAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var trial = await _store.ReadTrialAsync(cancellationToken).ConfigureAwait(false);

            // 只有"既没有授权文件、也没有试用记录"才自动生成 —— 也就是真正的首次启动。
            // 绝不能"每次启动都补一份"：那会让试用变成永远刷新的免费授权，比不做授权更糟。
            var isFirstRun = !snapshot.Exists && trial is null;

            if (!isFirstRun || !LicenseOptions.AutoTrial)
            {
                LogStatus("授权状态");
                return;
            }

            _logger.LogInformation(
                "首次启动：未发现授权文件与试用记录，自动生成本机试用 {Days} 天。",
                LicenseOptions.TrialDays);

            var result = await _license
                .GenerateTrialAsync(ActorContext.System, cancellationToken)
                .ConfigureAwait(false);

            if (result.Ok)
            {
                _logger.LogInformation("{Message}", result.Message);
                LogStatus("授权状态");
            }
            else
            {
                // 常见原因：要求强制联网但联网不可得。这只影响试用生成，
                // 服务本身照常可用（档位仍是 Warn）—— 所以是 Warning 而不是 Fatal。
                _logger.LogWarning("自动生成试用未成功：{Message}", result.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 授权初始化失败绝不能阻止服务启动（授权是"锦上添花"级别的约束，
            // 不是服务能否运行的前提）。铁律 #6：后台异常必须被捕获并记录。
            _logger.LogWarning(ex, "授权初始化失败（已忽略 —— 授权不可用不该阻止服务启动）。");
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(LicenseService.RefreshInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _license.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "授权状态刷新失败（已忽略，下次节拍重试）。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
        catch (Exception ex)
        {
            // PeriodicTimer 理论上不抛，但铁律 #6：任何后台异常都不允许静默逃逸。
            _logger.LogError(ex, "授权刷新循环异常退出。");
        }
    }

    private void LogStatus(string prefix)
        => _logger.LogInformation(
            "{Prefix}：{Status}（执行档位 {Enforcement}）。",
            prefix,
            _license.State.StatusDisplayName,
            _license.EffectiveEnforcement);
}
