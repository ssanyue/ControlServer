using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <see cref="DistributionService"/> 的用例（M4.4）：建任务、记进度、汇总总状态、推事件。
/// <para>
/// 这组的重点是几个<b>"错了就静默"</b>的语义：
/// 进度只增不减、终态幂等、归属校验、总状态由明细汇总。
/// 它们错了都不会抛异常，只会让页面上的数字慢慢变得不可信。
/// </para>
/// </summary>
public sealed class DistributionServiceTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeFileRepositoryStore _files = new();
    private readonly FakeDistributionStore _store = new();
    private readonly NullNotifier _notifier = new();

    private readonly FileRepositoryService _fileService;
    private readonly DistributionService _svc;

    public DistributionServiceTests()
    {
        var log = new LogService(new NoopDataDirectory(), flushIntervalMs: 60_000);

        _fileService = new FileRepositoryService(_files, _dir, log, new FileRepositoryOptions());
        _svc = new DistributionService(_store, _fileService, _notifier, log);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>造目标清单：一批 id，都不带目录（用各机的默认目录）。</summary>
    private static IReadOnlyList<DistributionTargetRequest> Targets(params int[] ids)
        => ids.Select(id => new DistributionTargetRequest(id)).ToList();

    /// <summary>造目标清单：每台带一个目录。</summary>
    private static IReadOnlyList<DistributionTargetRequest> Dirs(params (int Id, string? Dir)[] specs)
        => specs.Select(s => new DistributionTargetRequest(s.Id, s.Dir)).ToList();

    private async Task<int> SeedFileAsync(string name = "pkg.zip", long size = 1000, string kind = FileKinds.Normal)
    {
        var saved = await _files.AddAsync(new FileRepoItem(
            Id: 0,
            Name: name,
            StoredName: "f_seed.bin",
            Size: size,
            Sha256: new string('a', 64),
            Kind: kind,
            UploadedBy: "admin",
            UploadedAt: BeijingTime.Now,
            OriginAgentId: null,
            Note: null));

        return saved.Id;
    }

    // ═══════════════════════════════════════════════════════════
    // 建任务
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_push_fails_when_the_file_is_missing()
    {
        var result = await _svc.CreatePushAsync(999, Targets(1), "admin");

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("notfound");
        _store.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_push_fails_without_any_valid_target()
    {
        var fileId = await SeedFileAsync();

        (await _svc.CreatePushAsync(fileId, Array.Empty<DistributionTargetRequest>(), "admin"))
            .Status.Should().Be("invalid");
        (await _svc.CreatePushAsync(fileId, Targets(0, -3), "admin")).Status
            .Should().Be("invalid", "非正数 id 不是合法目标");
        (await _svc.CreatePushAsync(fileId, null, "admin")).Status
            .Should().Be("invalid", "没给目标等于没选机器");
        _store.Tasks.Should().BeEmpty();
    }

    /// <summary>
    /// 一次性为每个目标建满明细（含离线机器）—— 任务总览才与"后台还惦记着几台"一致。
    /// 重复与非法 id 在这里就被挡掉，明细行不会出现"同一台机器两行"。
    /// </summary>
    [Fact]
    public async Task Create_push_creates_one_item_per_unique_target()
    {
        var fileId = await SeedFileAsync(size: 4096);

        var result = await _svc.CreatePushAsync(
            fileId,
            Dirs((3, null), (3, null), (0, null), (5, null), (-1, null)),
            "admin");

        result.Ok.Should().BeTrue();
        var taskId = result.Task!.Id;

        var items = await _svc.ListItemsAsync(taskId);
        items.Should().HaveCount(2, "3 与 5 各一行；重复与非法 id 被去掉");
        items.Select(i => i.AgentId).Should().Equal(new[] { 3, 5 });
        items.Should().OnlyContain(i => i.State == DistributionItemStates.Pending);
        items.Should().OnlyContain(i => i.TotalBytes == 4096, "下发时总大小已知（文件大小）");
    }

    [Fact]
    public async Task Create_push_records_a_snapshot_of_the_file_and_the_target_directory()
    {
        var fileId = await SeedFileAsync(name: "setup.exe", size: 2048);

        var result = await _svc.CreatePushAsync(fileId, Dirs((1, "  D:\\inbox  ")), "alice");

        var task = result.Task!;
        task.Direction.Should().Be(DistributionDirections.Push);
        task.FileName.Should().Be("setup.exe", "任务列表要显示文件名，不能每次去 join 仓库");
        task.FileSize.Should().Be(2048);
        task.TargetDir.Should().Be("D:\\inbox", "全部目标共用一个目录（旧形状）时记到任务级；两端空白要去掉");
        task.CreatedBy.Should().Be("alice");
        task.State.Should().Be(DistributionStates.Pending);
    }

    /// <summary>
    /// 🔥 每台可以各自指定落地目录（2026-10-13）：目录要落在<b>各自的明细</b>上。
    /// <para>
    /// 这是本需求最容易做错的一处 —— 顺手把任务级目录回填到每行，就悄悄退化成"共用一个目录"，
    /// 而页面上仍然显示"每台一个目录"，看不出任何异常。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Create_push_keeps_a_directory_per_target()
    {
        var fileId = await SeedFileAsync();

        var result = await _svc.CreatePushAsync(
            fileId,
            Dirs((1, "D:\\data"), (2, "E:\\share"), (3, null)),
            "admin");

        var items = await _svc.ListItemsAsync(result.Task!.Id);

        items.Single(i => i.AgentId == 1).TargetDir.Should().Be("D:\\data");
        items.Single(i => i.AgentId == 2).TargetDir.Should().Be("E:\\share");
        items.Single(i => i.AgentId == 3).TargetDir.Should().BeNull("没给就留空 = 用那台机器的默认目录");
    }

    /// <summary>
    /// 任务级目录是<b>列表页的摘要</b>：全部相同才有值，各自不同就必须是 null
    /// —— 让它显示成"第一个目标的目录"是最误导人的做法。
    /// </summary>
    [Fact]
    public async Task Create_push_only_records_a_task_level_directory_when_all_targets_share_one()
    {
        var fileId = await SeedFileAsync();

        var same = (await _svc.CreatePushAsync(fileId, Dirs((1, "D:\\a"), (2, "d:\\A")), "admin")).Task!;
        same.TargetDir.Should().Be("D:\\a", "忽略大小写地相同 ⇒ 就是同一个目录");

        var differ = (await _svc.CreatePushAsync(fileId, Dirs((1, "D:\\a"), (2, "E:\\b")), "admin")).Task!;
        differ.TargetDir.Should().BeNull("各自不同时不能挑一个冒充全局");

        var noneGiven = (await _svc.CreatePushAsync(fileId, Targets(1, 2), "admin")).Task!;
        noneGiven.TargetDir.Should().BeNull("都没给 ⇒ 没有任何任务级目录可说");
    }

    /// <summary>
    /// <see cref="DistributionItem.EffectiveTargetDir"/> 的优先级：<b>明细优先，任务级兜底</b>。
    /// <para>派发与补领两条路都调它，分叉的表现是"在线时落到 A、离线补领时落到 B"。</para>
    /// </summary>
    [Fact]
    public void Effective_target_directory_prefers_the_item_and_falls_back_to_the_task()
    {
        var item = new DistributionItem(1, 1, 1, DistributionItemStates.Pending, 0, 0, null, null, null, null);

        item.EffectiveTargetDir("D:\\from-task").Should().Be("D:\\from-task", "明细没指定就退回任务级（旧形状）");

        var perTarget = item with { TargetDir = "E:\\from-item" };
        perTarget.EffectiveTargetDir("D:\\from-task").Should().Be("E:\\from-item", "明细上的优先");
        perTarget.EffectiveTargetDir(null).Should().Be("E:\\from-item");
    }

    [Fact]
    public async Task Create_collect_requires_a_source_path()
    {
        (await _svc.CreateCollectAsync(new[] { 1 }, null, null, "admin")).Status.Should().Be("invalid");
        (await _svc.CreateCollectAsync(new[] { 1 }, "   ", null, "admin")).Status.Should().Be("invalid");
        (await _svc.CreateCollectAsync(Array.Empty<int>(), "C:\\log.txt", null, "admin")).Status.Should().Be("invalid");
    }

    [Fact]
    public async Task Create_collect_has_an_unknown_size_until_the_client_reports_it()
    {
        var result = await _svc.CreateCollectAsync(new[] { 2, 4 }, @"..\..\logs\app.log", "排查用", "admin");

        result.Ok.Should().BeTrue();

        var task = result.Task!;
        task.Direction.Should().Be(DistributionDirections.Collect);
        task.FileId.Should().Be(0, "回传没有源仓库文件 id");
        task.FileSize.Should().Be(0, "要等客户端量出源文件大小才知道");
        task.FileName.Should().Be("app.log", "展示名只取最后一段");
        task.SourcePath.Should().Be(@"..\..\logs\app.log", "源路径原样保留（由客户端按白名单校验）");

        var items = await _svc.ListItemsAsync(task.Id);
        items.Should().OnlyContain(i => i.TotalBytes == 0);
    }

    // ═══════════════════════════════════════════════════════════
    // 记进度（幂等、不倒退、归属）
    // ═══════════════════════════════════════════════════════════

    private async Task<(int TaskId, int ItemId)> SeedPushAsync(int agentId = 1, long size = 1000)
    {
        var fileId = await SeedFileAsync(size: size);
        var task = (await _svc.CreatePushAsync(fileId, Targets(agentId), "admin")).Task!;
        var item = (await _svc.ListItemsAsync(task.Id)).Single();

        return (task.Id, item.Id);
    }

    [Fact]
    public async Task Report_progress_moves_the_item_to_running_and_records_bytes()
    {
        var (taskId, itemId) = await SeedPushAsync();

        var updated = await _svc.ReportProgressAsync(itemId, 1, 250, 1000, null);

        updated!.State.Should().Be(DistributionItemStates.Running);
        updated.ReceivedBytes.Should().Be(250);
        updated.StartedAt.Should().NotBeNull("首次收到进度就记下开始时刻");

        (await _svc.FindTaskAsync(taskId))!.State.Should().Be(DistributionStates.Running);
    }

    /// <summary>
    /// 🔥 已记字节<b>只增不减</b>：乱序到达的旧进度会把进度条往回拽。
    /// <para>100+ 台机器并发上报、外加断线重试，乱序是常态而不是异常。</para>
    /// </summary>
    [Fact]
    public async Task Report_progress_never_lets_the_recorded_bytes_go_backwards()
    {
        var (_, itemId) = await SeedPushAsync();

        await _svc.ReportProgressAsync(itemId, 1, 600, 1000, null);
        var stale = await _svc.ReportProgressAsync(itemId, 1, 300, 1000, null);

        stale!.ReceivedBytes.Should().Be(600, "迟到的旧进度不能把已收字节改小");
    }

    /// <summary>
    /// 🔥 归属校验：客户机只能动自己那一行。
    /// <para>少了这一步，任何一台拿到 AgentKey 的机器都能把别人的传输标成"完成"或"失败"。</para>
    /// </summary>
    [Fact]
    public async Task Report_progress_from_another_agent_is_ignored()
    {
        var (_, itemId) = await SeedPushAsync(agentId: 1);

        (await _svc.ReportProgressAsync(itemId, 99, 500, 1000, null)).Should().BeNull();
        (await _svc.ReportProgressAsync(itemId, 1, 0, 0, null)).Should().NotBeNull("自己那台照常");

        var item = await _store.FindItemAsync(itemId);
        item!.ReceivedBytes.Should().Be(0, "别的机器报的进度不该落到这一行");
    }

    [Fact]
    public async Task Progress_arriving_on_a_finished_item_is_ignored()
    {
        var (_, itemId) = await SeedPushAsync();

        await _svc.CompleteItemAsync(itemId, 1, ok: true, receivedBytes: 1000, sha256: new string('b', 64), error: null);

        var late = await _svc.ReportProgressAsync(itemId, 1, 10, 1000, null);

        late!.State.Should().Be(DistributionItemStates.Completed, "重试重复上报不能把「已完成」改回「传输中」");
        late.ReceivedBytes.Should().Be(1000);
    }

    // ═══════════════════════════════════════════════════════════
    // 最终结果
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Complete_success_marks_the_item_and_summarizes_the_task()
    {
        var (taskId, itemId) = await SeedPushAsync();

        var sha = new string('c', 64);
        var item = await _svc.CompleteItemAsync(itemId, 1, ok: true, receivedBytes: 1000, sha256: sha, error: null);

        item!.State.Should().Be(DistributionItemStates.Completed);
        item.ReceivedBytes.Should().Be(1000);
        item.Sha256.Should().Be(sha);
        item.CompletedAt.Should().NotBeNull();

        (await _svc.FindTaskAsync(taskId))!.State.Should().Be(DistributionStates.Completed);
    }

    [Fact]
    public async Task Complete_failure_records_the_reason()
    {
        var (taskId, itemId) = await SeedPushAsync();

        var item = await _svc.CompleteItemAsync(itemId, 1, ok: false, receivedBytes: 100, sha256: null, error: "目标路径不在本机白名单内");

        item!.State.Should().Be(DistributionItemStates.Failed);
        item.Error.Should().Be("目标路径不在本机白名单内");
        (await _svc.FindTaskAsync(taskId))!.State.Should().Be(DistributionStates.Failed);
    }

    [Fact]
    public async Task Complete_failure_without_a_reason_says_so_explicitly()
    {
        var (_, itemId) = await SeedPushAsync();

        var item = await _svc.CompleteItemAsync(itemId, 1, ok: false, receivedBytes: 0, sha256: null, error: "   ");

        item!.Error.Should().Be("客户端未说明失败原因。", "页面上不能出现一个空的失败原因");
    }

    [Fact]
    public async Task Complete_is_idempotent_and_the_first_result_wins()
    {
        var (taskId, itemId) = await SeedPushAsync();

        await _svc.CompleteItemAsync(itemId, 1, ok: true, receivedBytes: 1000, sha256: null, error: null);

        // 客户端重连后重复上报"失败"不该把已完成翻成失败。
        var again = await _svc.CompleteItemAsync(itemId, 1, ok: false, receivedBytes: 0, sha256: null, error: "网络断了");

        again!.State.Should().Be(DistributionItemStates.Completed, "终态一旦定下就不再改写");
        (await _svc.FindTaskAsync(taskId))!.State.Should().Be(DistributionStates.Completed);
    }

    [Fact]
    public async Task Complete_from_another_agent_is_ignored()
    {
        var (_, itemId) = await SeedPushAsync(agentId: 1);

        (await _svc.CompleteItemAsync(itemId, 42, ok: true, receivedBytes: 1000, sha256: null, error: null))
            .Should().BeNull();

        (await _store.FindItemAsync(itemId))!.State.Should().Be(DistributionItemStates.Pending);
    }

    /// <summary>
    /// 一台成、一台败 ⇒ 总状态 <c>partial</c>（需要人看一眼），而不是"completed"。
    /// </summary>
    [Fact]
    public async Task Task_state_is_recomputed_from_all_items()
    {
        var fileId = await SeedFileAsync(size: 500);
        var task = (await _svc.CreatePushAsync(fileId, Targets(1, 2), "admin")).Task!;
        var items = await _svc.ListItemsAsync(task.Id);

        await _svc.CompleteItemAsync(items[0].Id, items[0].AgentId, ok: true, receivedBytes: 500, sha256: null, error: null);

        // 还有一台没动（pending）⇒ 任务还没结束。
        (await _svc.FindTaskAsync(task.Id))!.State.Should().Be(DistributionStates.Pending);

        await _svc.CompleteItemAsync(items[1].Id, items[1].AgentId, ok: false, receivedBytes: 0, sha256: null, error: "拒绝");

        (await _svc.FindTaskAsync(task.Id))!.State.Should().Be(DistributionStates.Partial);
    }

    // ═══════════════════════════════════════════════════════════
    // 推送载荷
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Progress_notifications_carry_a_rounded_percentage()
    {
        var (taskId, itemId) = await SeedPushAsync(size: 1000);

        await _svc.ReportProgressAsync(itemId, 1, 333, 1000, null);

        var last = _notifier.Transfers.Last();
        last.TaskId.Should().Be(taskId);
        last.ItemId.Should().Be(itemId);
        last.AgentId.Should().Be(1);
        last.State.Should().Be(DistributionItemStates.Running);
        last.ProgressPct.Should().Be(33.3, "百分比保留一位小数，与页面显示一致");
    }

    [Fact]
    public async Task Progress_notification_reports_zero_percent_while_the_total_is_unknown()
    {
        // 回传任务：客户端还没量出源文件大小。
        var task = (await _svc.CreateCollectAsync(new[] { 1 }, "C:\\log.txt", null, "admin")).Task!;
        var item = (await _svc.ListItemsAsync(task.Id)).Single();

        await _svc.ReportProgressAsync(item.Id, 1, 4096, 0, null);

        _notifier.Transfers.Last().ProgressPct.Should().Be(0, "total 未知时是「还不知道」，不是 100");
    }

    [Fact]
    public async Task A_collect_item_picks_up_the_total_reported_by_the_client()
    {
        var task = (await _svc.CreateCollectAsync(new[] { 1 }, "C:\\log.txt", null, "admin")).Task!;
        var item = (await _svc.ListItemsAsync(task.Id)).Single();

        var updated = await _svc.ReportProgressAsync(item.Id, 1, 500, 2000, null);

        updated!.TotalBytes.Should().Be(2000, "客户端量出大小后回填");
        _notifier.Transfers.Last().ProgressPct.Should().Be(25);

        // 之后再报一个 total=0（客户端一时没量到）不该把已知的总大小抹掉。
        var again = await _svc.ReportProgressAsync(item.Id, 1, 1000, 0, null);
        again!.TotalBytes.Should().Be(2000, "已知总大小只增不减，不能被后续的 0 覆盖");
    }
}
