using ControlServer.Core.Abstractions;
using ControlServer.Core.Services;
using ControlServer.Server.Options;
using Microsoft.Extensions.Options;

namespace ControlServer.Server.HostedServices;

/// <summary>
/// 文件传输的后台维护（M4.4 / M7.1）：回收过期上传会话、清理陈旧的分发任务、
/// 以及对账<b>库 ↔ 盘</b>（孤儿文件 / 断链记录）。
/// <para>
/// <b>为什么必须有</b>：分块上传会在暂存目录里留 <c>.part</c> 文件。客户端断线、取消、
/// 或者干脆机器被搬走，那些半截文件就永远不会被收尾 —— 没有清理，暂存目录会随着时间无限膨胀
/// （而这是运维不会主动去看的地方）。
/// </para>
/// <para>
/// <b>对账（M7.1）为什么也在这</b>：<c>FileRepositoryService.DeleteAsync</c> 的注释写着
/// 「磁盘上那个孤儿文件由清理任务兜底」，而在此之前<b>全仓没有这个任务</b>。
/// 注释承诺与实际行为不一致比"没有兜底"更坏 —— 它会让人不再去看那个目录。
/// </para>
/// <para>
/// 半小时一轮：清理是删旧东西，早半小时晚半小时没有区别，而更勤只会白耗 IO。
/// </para>
/// <para>⚠️ 与其余后台任务一致：任何异常都必须接住并记录，绝不允许它冒出去杀死进程。</para>
/// </summary>
public sealed class FileTransferHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(30);

    /// <summary>断链一次最多报几条 —— 报总数就够定位了，别让日志被同一件事刷满。</summary>
    private const int MaxLostReported = 5;

    private readonly FileRepositoryService _files;
    private readonly RepositoryGroomer _groomer;
    private readonly IDistributionStore _distributions;
    private readonly ControlSettingsService _settings;
    private readonly IDataDirectory _dataDir;
    private readonly LogService _log;
    private readonly IOptions<ServerOptions> _options;

    public FileTransferHostedService(
        FileRepositoryService files,
        RepositoryGroomer groomer,
        IDistributionStore distributions,
        ControlSettingsService settings,
        IDataDirectory dataDir,
        LogService log,
        IOptions<ServerOptions> options)
    {
        _files = files;
        _groomer = groomer;
        _distributions = distributions;
        _settings = settings;
        _dataDir = dataDir;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时先等一小会儿：别和"服务刚起来"那堆初始化抢 IO / 抢写锁。
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // 三件事各自独立 try/catch：清理会话失败不该让任务清理也停摆。
        try
        {
            var purged = await _files.PurgeExpiredSessionsAsync(cancellationToken).ConfigureAwait(false);

            if (purged > 0) _log.Info("Files", $"已回收 {purged} 个过期上传会话及其暂存文件。");
        }
        catch (Exception ex)
        {
            _log.Error("Files", "回收过期上传会话失败（下一轮继续）", ex);
        }

        // 对账排在上面之后是刻意的：过期会话回收里"删了记录但文件没删掉"的半成功，
        // 会在这一步变成"无主 .part"，从而被真正兜住。
        try
        {
            await GroomRepositoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Files", "文件仓库对账失败（下一轮继续）", ex);
        }

        // 图片目录与文件仓库是两套判据（一个对着库里的记录，一个对着配置里的引用），
        // 但清理节奏应当一致 —— 分两个独立的 try 是为了"一边失败不拖累另一边"。
        try
        {
            await GroomImagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Files", "图片目录对账失败（下一轮继续）", ex);
        }

        try
        {
            var days = Math.Max(1, _options.Value.Files.TaskRetentionDays);
            var cutoff = BeijingTime.Now.AddDays(-days);
            var removed = await _distributions.PurgeTerminalTasksAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (removed > 0) _log.Info("Files", $"已清理 {removed} 个超过 {days} 天的已完成分发任务。");
        }
        catch (Exception ex)
        {
            _log.Error("Files", "清理陈旧分发任务失败（下一轮继续）", ex);
        }
    }

    /// <summary>
    /// 对账并回收孤儿。
    /// <para>
    /// 断链（库里有、盘上没有）<b>只报不删</b>：那是用户的文件真的丢了，
    /// 删掉记录等于替用户决定放弃，而且会让这件事从此看不见。
    /// </para>
    /// </summary>
    private async Task GroomRepositoryAsync(CancellationToken cancellationToken)
    {
        var survey = await _groomer.SurveyAsync(cancellationToken).ConfigureAwait(false);

        if (survey.Lost.Count > 0)
        {
            var sample = string.Join(
                "；",
                survey.Lost.Take(MaxLostReported).Select(x => $"id={x.Id} {x.Name}（{FormatBytes(x.Size)}）"));

            var more = survey.Lost.Count > MaxLostReported ? $"……还有 {survey.Lost.Count - MaxLostReported} 条" : string.Empty;

            // ⚠️ 用 Warn 而不是 Info：断链意味着"仓库里列着、下载却会失败"，
            //    这是一件必须有人去看备份的事，不该淹没在 Info 里。
            _log.Warn(
                "Files",
                $"文件仓库断链 {survey.Lost.Count} 条（库里有记录、磁盘上没有文件）：{sample}{more}。"
                + "记录不会自动删除 —— 请从备份里找回 files/repo/ 下的同名文件，或由管理员确认后手工删除记录。");
        }

        if (survey.UnexpectedDirectories.Count > 0)
        {
            _log.Warn(
                "Files",
                $"仓库目录下出现子目录（{string.Join("、", survey.UnexpectedDirectories.Take(MaxLostReported))}）："
                + "对账只处理顶层文件，这些目录里的一切都不会被回收。若它们不是手工放的，请联系维护者。");
        }

        if (survey.Orphans.Count == 0 && survey.StaleParts.Count == 0) return;

        var result = await _groomer.CollectAsync(survey, cancellationToken).ConfigureAwait(false);

        if (result.AnyDeleted)
        {
            _log.Info(
                "Files",
                $"文件仓库对账：回收孤儿 {survey.Orphans.Count} 个 + 无主暂存 {survey.StaleParts.Count} 个，"
                + $"实际删掉 {result.DeletedFiles} 个，腾出 {FormatBytes(result.DeletedBytes)}。"
                + $"（宽限期 {_options.Value.Files.OrphanGraceHours} 小时）");
        }

        foreach (var failure in result.Failures.Take(MaxLostReported))
        {
            _log.Warn("Files", $"孤儿文件删不掉（{failure}）—— 下一轮继续尝试");
        }
    }

    /// <summary>
    /// 图片目录（<c>images/</c>）的对账与回收。
    ///
    /// <para>
    /// 🔥 <b>它补的是一个"看不见"的洞</b>：M7.1 只对账了 <c>files/</c>，而图片目录从来没有人管 ——
    /// 用户换图、删按钮、上传后没保存配置，都会在 <c>images/</c> 里留下永远不会被引用的文件。
    /// 这个目录不会报错、页面也正常，只是在慢慢变大。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>断链只报不删</b>：配置里引用了、盘上却没有的图，前端会<b>静默</b>回退成纯色瓦片。
    /// 删引用等于替用户放弃，而且会让"图丢了"这件事从此看不见 —— 正确处置是报警 + 人去找备份。
    /// </para>
    /// </summary>
    private async Task GroomImagesAsync(CancellationToken cancellationToken)
    {
        var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        // 🔴 配置损坏时**不判孤儿**：那时"没人引用"只说明我们没读懂配置，
        //    不能推出"可以删"。断链同样无从判断（引用表本身不可信）。
        //    `Missing` 可以判：默认配置里确实一张图都不引用。
        var settings = view.State == ConfigLoadState.Corrupt ? null : view.Settings;

        var graceHours = Math.Max(1, _options.Value.Files.OrphanGraceHours);

        var survey = ImageRepositoryGroomer.Scan(
            settings,
            _dataDir.ImagesDirectory,
            BeijingTime.Now.UtcDateTime,
            TimeSpan.FromHours(graceHours));

        if (view.State == ConfigLoadState.Corrupt)
        {
            _log.Warn(
                "Files",
                "配置损坏，本轮不判图片孤儿（读不懂配置就无法知道哪张图还有人用）；"
                + "残留 .tmp 仍会清理。请先修复配置（界面会提示，或从备份恢复）。");
        }

        if (survey.Lost.Count > 0)
        {
            var sample = string.Join(
                "；",
                survey.Lost.Take(MaxLostReported)
                    .Select(x => $"{x.Name}（被 {string.Join("、", x.Referrers.Take(2))} 引用）"));

            var more = survey.Lost.Count > MaxLostReported ? $"……还有 {survey.Lost.Count - MaxLostReported} 张" : string.Empty;

            // ⚠️ Warn 而不是 Info：这些图在页面上是**静默**变成纯色瓦片的，不会自己报错。
            _log.Warn(
                "Files",
                $"图片断链 {survey.Lost.Count} 张（配置里引用了、images/ 下没有文件）：{sample}{more}。"
                + "页面不会报错 —— 那几处会显示成纯色瓦片。请从备份里找回同名文件，"
                + "或重新上传后在界面上重新选择。");
        }

        if (survey.UnexpectedDirectories.Count > 0)
        {
            _log.Warn(
                "Files",
                $"images/ 下出现子目录（{string.Join("、", survey.UnexpectedDirectories.Take(MaxLostReported))}）："
                + "对账只处理顶层文件，这些目录里的一切都不会被回收。");
        }

        if (survey.Orphans.Count == 0 && survey.StaleTemps.Count == 0) return;

        var result = ImageRepositoryGroomer.Collect(survey, _dataDir.ImagesDirectory);

        if (result.AnyDeleted)
        {
            _log.Info(
                "Files",
                $"图片目录对账：回收孤儿 {survey.Orphans.Count} 张 + 残留临时文件 {survey.StaleTemps.Count} 个，"
                + $"实际删掉 {result.DeletedFiles} 个，腾出 {FormatBytes(result.DeletedBytes)}。"
                + $"（宽限期 {graceHours} 小时；引用判据与删除端点完全一致）");
        }

        foreach (var failure in result.Failures.Take(MaxLostReported))
        {
            _log.Warn("Files", $"孤儿图片删不掉（{failure}）—— 下一轮继续尝试");
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d, mb = kb * 1024, gb = mb * 1024;

        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.##} MB";
        if (bytes >= kb) return $"{bytes / kb:0.##} KB";
        return $"{bytes} B";
    }
}
