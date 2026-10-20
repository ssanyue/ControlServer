using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// M2-B 顺带落地的两个支撑组件：指令库读写（<c>commands</c> 文档）与收发日志环形缓冲。
/// 指令库用**内存版 <see cref="IConfigDocumentStore"/>** 驱动，不碰数据库。
/// </summary>
public sealed class CommandLibraryServiceTests
{
    [Fact]
    public async Task Resolve_is_case_insensitive_and_trims()
    {
        var service = NewService(out _, new CommandItem { Name = "OpenLight", DataType = DataTypes.Hex, Data = "41" });

        (await service.ResolveAsync("openlight"))!.Name.Should().Be("OpenLight");
        (await service.ResolveAsync("  OPENLIGHT  "))!.Name.Should().Be("OpenLight");
    }

    [Fact]
    public async Task Resolve_returns_null_for_blank_or_unknown_name()
    {
        var service = NewService(out _, new CommandItem { Name = "A" });

        (await service.ResolveAsync(null)).Should().BeNull();
        (await service.ResolveAsync("   ")).Should().BeNull();
        (await service.ResolveAsync("B")).Should().BeNull();
    }

    [Fact]
    public async Task Missing_document_yields_empty_library()
    {
        var service = NewService(out _);

        (await service.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_then_resolve_round_trips()
    {
        var service = NewService(out var store);

        var version = await service.SaveAsync(
            new[]
            {
                new CommandItem { Name = "开灯", Protocol = TransportProtocols.Tcp, DataType = DataTypes.Utf8, Data = "ON" },
                new CommandItem { Name = "关灯", Protocol = TransportProtocols.Udp, DataType = DataTypes.Hex, Data = "43 44" },
            },
            expectedVersion: null,
            updatedBy: "test");

        version.Should().Be(1);
        store.Documents.Should().ContainKey(ConfigKeys.Commands);

        var all = await service.GetAllAsync();
        all.Should().HaveCount(2);

        var resolved = await service.ResolveAsync("关灯");
        resolved!.Protocol.Should().Be(TransportProtocols.Udp);
        resolved.Data.Should().Be("43 44");
    }

    [Fact]
    public async Task Concurrent_save_throws_when_expected_version_is_stale()
    {
        var service = NewService(out _);

        await service.SaveAsync(new[] { new CommandItem { Name = "A" } }, expectedVersion: null, updatedBy: "t1");

        var act = async () => await service.SaveAsync(
            new[] { new CommandItem { Name = "B" } }, expectedVersion: 0, updatedBy: "t2");

        await act.Should().ThrowAsync<ConfigConcurrencyException>();
    }

    [Fact]
    public async Task Corrupt_document_degrades_to_empty_instead_of_throwing()
    {
        var service = NewService(out var store);
        store.Seed(ConfigKeys.Commands, "{ this is not json", version: 3);

        var act = async () => await service.GetAllAsync();

        await act.Should().NotThrowAsync();
        (await service.GetAllAsync()).Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    // M2-E3：指令库 CRUD
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Add_appends_the_item_and_bumps_the_version()
    {
        var service = NewService(out var store);

        var result = await service.AddAsync(NewCommand("开灯", DataTypes.Utf8, "ON"), "tester");

        result.Ok.Should().BeTrue();
        result.Status.Should().Be(CommandMutationStatus.Ok);
        result.Version.Should().Be(1, "空库首次写入即版本 1");
        result.Items.Should().ContainSingle();
        store.Documents.Should().ContainKey(ConfigKeys.Commands);

        (await service.ResolveAsync("开灯")).Should().NotBeNull();
    }

    [Fact]
    public async Task Add_rejects_a_name_that_differs_only_in_case()
    {
        var service = NewService(out _, NewCommand("OpenLight"));
        var existing = (await service.GetAllAsync())[0];

        var result = await service.AddAsync(NewCommand("openlight"), "tester");

        result.Ok.Should().BeFalse();
        result.Status.Should().Be(CommandMutationStatus.DuplicateName);
        result.ConflictId.Should().Be(existing.Id, "要把冲突的那条指出来，前端才能高亮它");
        result.Items.Should().ContainSingle("被拒时不能写库");
    }

    [Fact]
    public async Task Add_rejects_a_blank_name()
    {
        var service = NewService(out var store);

        var result = await service.AddAsync(NewCommand("   "), "tester");

        result.Status.Should().Be(CommandMutationStatus.Invalid);
        result.Items.Should().BeEmpty();
        store.Documents.Should().NotContainKey(ConfigKeys.Commands, "校验失败不该产生任何写入");
    }

    [Fact]
    public async Task Add_refuses_a_command_that_points_back_at_the_command_library()
    {
        // 指令库里的指令把数据类型写成「指令库」= 解析时无限套娃，必须在写入口就挡掉。
        var service = NewService(out _);

        var result = await service.AddAsync(
            NewCommand("套娃", DataTypes.CommandLibrary, "别的指令"), "tester");

        result.Status.Should().Be(CommandMutationStatus.Invalid);
        result.Message.Should().Contain("指令库");
    }

    [Fact]
    public async Task Add_normalizes_protocol_and_data_type_casing()
    {
        var service = NewService(out _);

        var result = await service.AddAsync(
            NewCommand("X", dataType: "utf-8", protocol: "tcp"), "tester");

        result.Ok.Should().BeTrue();
        result.Item!.Protocol.Should().Be(TransportProtocols.Tcp);

        // 「utf-8」= 大小写归一 + 旧别名合并（2026-10-08 起 UTF-8 并入 String）一步到位。
        result.Item.DataType.Should().Be(
            DataTypes.String,
            "「utf-8」要归一成「String」—— 两者编码结果完全相同，库里只保留一个取值");
    }

    [Theory]
    [InlineData("WoL")]   // 指令库不含 WoL（与旧版的 CommandItem 一致）
    [InlineData("ICMP")]
    public async Task Add_rejects_a_protocol_the_library_does_not_support(string protocol)
    {
        var service = NewService(out _);

        var result = await service.AddAsync(NewCommand("X", protocol: protocol), "tester");

        result.Status.Should().Be(CommandMutationStatus.Invalid);
        result.Message.Should().Contain(protocol);
    }

    [Fact]
    public async Task Add_assigns_a_fresh_id_when_the_supplied_one_collides()
    {
        var service = NewService(out _);
        var first = NewCommand("甲");
        await service.AddAsync(first, "t");

        var cloned = NewCommand("乙");
        cloned.Id = first.Id;   // 模拟前端「复制一条再新增」

        var result = await service.AddAsync(cloned, "t");

        result.Ok.Should().BeTrue();
        result.Item!.Id.Should().NotBe(first.Id, "「新增」不能变成「悄悄覆盖」");
        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_replaces_the_item_in_place_and_keeps_the_route_id()
    {
        var service = NewService(out _);
        var created = (await service.AddAsync(NewCommand("旧名", DataTypes.Utf8, "OLD"), "t")).Item!;

        var edited = NewCommand("新名", DataTypes.Hex, "41 42");
        edited.Id = "前端瞎填的";   // Id 一律以路由为准

        var result = await service.UpdateAsync(created.Id, edited, "t");

        result.Ok.Should().BeTrue();
        result.Item!.Id.Should().Be(created.Id);
        result.Items.Should().ContainSingle();
        (await service.ResolveAsync("新名"))!.Data.Should().Be("41 42");
        (await service.ResolveAsync("旧名")).Should().BeNull();
    }

    [Fact]
    public async Task Update_returns_not_found_for_an_unknown_id()
    {
        var service = NewService(out var store);

        var result = await service.UpdateAsync("不存在的 id", NewCommand("X"), "t");

        result.Status.Should().Be(CommandMutationStatus.NotFound);
        store.Documents.Should().NotContainKey(ConfigKeys.Commands);
    }

    [Fact]
    public async Task Update_allows_renaming_to_its_own_name()
    {
        // 重名校验必须排除自身：只改协议不改名，不该被判重名。
        var service = NewService(out _);
        var created = (await service.AddAsync(NewCommand("甲", protocol: TransportProtocols.Udp), "t")).Item!;

        var edited = NewCommand("甲", protocol: TransportProtocols.Tcp);
        var result = await service.UpdateAsync(created.Id, edited, "t");

        result.Ok.Should().BeTrue();
        result.Item!.Protocol.Should().Be(TransportProtocols.Tcp);
    }

    [Fact]
    public async Task Update_rejects_taking_another_commands_name()
    {
        var service = NewService(out _);
        var a = (await service.AddAsync(NewCommand("甲"), "t")).Item!;
        var b = (await service.AddAsync(NewCommand("乙"), "t")).Item!;

        var result = await service.UpdateAsync(a.Id, NewCommand("乙"), "t");

        result.Status.Should().Be(CommandMutationStatus.DuplicateName);
        result.ConflictId.Should().Be(b.Id);
    }

    [Fact]
    public async Task Delete_removes_the_item_and_bumps_the_version()
    {
        var service = NewService(out _);
        var a = (await service.AddAsync(NewCommand("甲"), "t")).Item!;
        await service.AddAsync(NewCommand("乙"), "t");

        var result = await service.DeleteAsync(a.Id, "t");

        result.Ok.Should().BeTrue();
        result.Items.Should().ContainSingle();
        result.Version.Should().Be(3, "新增两次 + 删除一次 = 三次写入");
        (await service.ResolveAsync("甲")).Should().BeNull();
        (await service.ResolveAsync("乙")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_id()
    {
        var service = NewService(out _);

        var result = await service.DeleteAsync("nope", "t");

        result.Status.Should().Be(CommandMutationStatus.NotFound);
    }

    [Fact]
    public async Task A_rejected_mutation_does_not_touch_the_store()
    {
        var service = NewService(out _);
        await service.AddAsync(NewCommand("甲"), "t");
        var before = await service.GetVersionAsync();

        var rejected = await service.AddAsync(NewCommand("甲"), "t");

        rejected.Ok.Should().BeFalse();
        (await service.GetVersionAsync()).Should().Be(before, "被拒的请求不写库");
    }

    [Fact]
    public async Task Mutation_refuses_to_overwrite_a_corrupt_document()
    {
        // 读路径退化为空库继续服务；写路径**必须停下** ——
        // 否则用户点一次「新增」就把还没抢救的原始内容永久覆盖了。
        var service = NewService(out var store);
        store.Seed(ConfigKeys.Commands, "{ 这不是 JSON", version: 4);

        var result = await service.AddAsync(NewCommand("甲"), "t");

        result.Status.Should().Be(CommandMutationStatus.StorageCorrupt);
        result.Version.Should().Be(4, "被拒时不写库，版本保持不动");
        store.Documents[ConfigKeys.Commands].Should().Be("{ 这不是 JSON", "原始内容一字不动，等人来处置");
    }

    [Fact]
    public async Task Concurrent_adds_do_not_lose_updates()
    {
        // 「锁内读改写」的守门用例：若哪天被改成「先读 → 改 → 再写」，10 路并发只会活下来最后一条。
        var service = NewService(out _);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => service.AddAsync(NewCommand($"指令{i}"), "t"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.Ok);
        (await service.GetAllAsync()).Should().HaveCount(10, "每一条并发新增都必须存活");
        (await service.GetVersionAsync()).Should().Be(10, "10 次写入各自 +1");
    }

    private static CommandItem NewCommand(
        string name,
        string dataType = DataTypes.Utf8,
        string data = "ON",
        string protocol = TransportProtocols.Udp)
        => new() { Name = name, Protocol = protocol, DataType = dataType, Data = data };

    private static CommandLibraryService NewService(out InMemoryConfigStore store, params CommandItem[] items)
    {
        store = new InMemoryConfigStore();

        if (items.Length > 0)
        {
            store.Seed(ConfigKeys.Commands, ControlJson.Serialize(items.ToList()), version: 1);
        }

        return new CommandLibraryService(store, new LogService(new NoopDataDirectory(), flushIntervalMs: 60_000));
    }

}

/// <summary>收发日志环形缓冲（M2-B 最小实现）。</summary>
public sealed class TrafficLogServiceTests
{
    [Fact]
    public void Recent_returns_entries_in_chronological_order()
    {
        var log = new TrafficLogService();
        log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.1", 1, "A", true));
        log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.2", 2, "B", true));

        log.Recent(10).Select(e => e.Data).Should().Equal("A", "B");
    }

    [Fact]
    public void Recent_returns_only_the_last_count_entries()
    {
        var log = new TrafficLogService();
        for (var i = 0; i < 10; i++) log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.1", 1, i.ToString(), true));

        log.Recent(3).Select(e => e.Data).Should().Equal("7", "8", "9");
    }

    [Fact]
    public void Ring_buffer_drops_the_oldest_entry_at_capacity()
    {
        var log = new TrafficLogService(capacity: 3);
        for (var i = 0; i < 5; i++) log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.1", 1, i.ToString(), true));

        log.Count.Should().Be(3);
        log.Recent(10).Select(e => e.Data).Should().Equal("2", "3", "4");
    }

    [Fact]
    public void Write_raises_the_change_event()
    {
        var log = new TrafficLogService();
        var captured = new List<TrafficLogEntry>();
        log.TrafficLogAdded += captured.Add;

        log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.1", 1, "A", true));

        captured.Should().ContainSingle().Which.Data.Should().Be("A");
    }

    [Fact]
    public void Subscriber_exception_does_not_escape_to_the_caller()
    {
        var log = new TrafficLogService();
        log.TrafficLogAdded += _ => throw new InvalidOperationException("订阅方炸了");

        var act = () => log.Write(TrafficLogEntry.Sent("UDP", "10.0.0.1", 1, "A", true));

        act.Should().NotThrow();
        log.Count.Should().Be(1);
    }

    [Fact]
    public void Sent_and_Received_fill_direction_and_timestamp()
    {
        var sent = TrafficLogEntry.Sent("UDP", "10.0.0.1", 8080, "A", true);
        sent.Direction.Should().Be(TrafficDirections.Send);
        sent.Time.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        sent.Time.Offset.Should().Be(BeijingTime.Offset, "全应用时间戳统一北京时间");

        var received = TrafficLogEntry.Received("UDP", "10.0.0.2", 9999, "B");
        received.Direction.Should().Be(TrafficDirections.Receive);
        received.Success.Should().BeTrue("接收方向恒为成功（与旧版一致）");
    }

    [Fact]
    public void TruncateData_keeps_short_values_and_clips_long_ones()
    {
        TrafficLogEntry.TruncateData("短").Should().Be("短");
        TrafficLogEntry.TruncateData(null).Should().BeEmpty();

        var longText = new string('x', TrafficLogEntry.MaxDataLength + 10);
        var clipped = TrafficLogEntry.TruncateData(longText);
        clipped.Should().HaveLength(TrafficLogEntry.MaxDataLength + 1);
        clipped.Should().EndWith("…");
    }
}
