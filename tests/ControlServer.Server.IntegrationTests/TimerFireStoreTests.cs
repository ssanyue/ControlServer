using ControlServer.Core.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 定时触发记录表（<c>timer_fires</c>）的持久化契约回归。
/// <para>
/// 这里测的是<b>真 SQLite</b> —— 因为这些保证是靠数据库的唯一约束给的，
/// 内存替身（<c>FakeTimerFireStore</c>）仿真不出"重启后依然记得"与"并发只有一个赢"。
/// </para>
/// <para>核心不变量：同一 <c>(RefId, FireDate, FireTime)</c> 在整张表的生命周期里只会被抢到一次。</para>
/// </summary>
public sealed class TimerFireStoreTests
{
    private const string RefId = "button-1";

    // ═══════════════════════════════════════════════════════════
    // 幂等：同一触发点只能抢到一次
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Claiming_the_same_point_twice_only_succeeds_once()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();
        var entry = Entry("2026-07-21", "08:00:00");

        (await store.TryClaimAsync(entry)).Should().BeTrue("第一次应当抢到");
        (await store.TryClaimAsync(entry)).Should().BeFalse("第二次必须被唯一约束挡住");
    }

    [Fact]
    public async Task The_same_time_on_different_dates_is_independent()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();

        (await store.TryClaimAsync(Entry("2026-07-21", "08:00:00"))).Should().BeTrue();
        (await store.TryClaimAsync(Entry("2026-07-22", "08:00:00"))).Should().BeTrue("换一天就是另一个触发点");
    }

    [Fact]
    public async Task The_same_date_and_time_for_different_sources_is_independent()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();

        (await store.TryClaimAsync(Entry("2026-07-21", "08:00:00", refId: "btn:plan-1"))).Should().BeTrue();
        (await store.TryClaimAsync(Entry("2026-07-21", "08:00:00", refId: "btn:plan-2"))).Should().BeTrue("不同计划的 RefId 不同");
    }

    [Fact]
    public async Task Claims_survive_a_host_restart()
    {
        using var first = new ControlServerFactory();
        using (var client = first.CreateClient())
        {
            await client.GetAsync("/healthz");
            var store = first.Services.GetRequiredService<ITimerFireStore>();

            (await store.TryClaimAsync(Entry("2026-07-21", "08:00:00"))).Should().BeTrue();
        }

        // 同一个数据目录重新起一个宿主 —— 落的是 SQLite 而不是内存。
        using var second = new ControlServerFactory(first.DataDirectory);
        using var secondClient = second.CreateClient();
        await secondClient.GetAsync("/healthz");

        var restarted = second.Services.GetRequiredService<ITimerFireStore>();

        (await restarted.TryClaimAsync(Entry("2026-07-21", "08:00:00")))
            .Should().BeFalse("重启后重放同一触发点必须被挡住 —— 这正是把幂等交给数据库的原因");
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_claims_wins()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => store.TryClaimAsync(Entry("2026-07-21", "08:00:00")))));

        results.Count(ok => ok).Should().Be(1, "唯一约束必须把并发抢点收敛成恰好一个赢家");
    }

    // ═══════════════════════════════════════════════════════════
    // 查询
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task List_returns_only_the_requested_day()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();

        await store.TryClaimAsync(Entry("2026-07-20", "23:59:59"));
        await store.TryClaimAsync(Entry("2026-07-21", "08:00:00"));
        await store.TryClaimAsync(Entry("2026-07-21", "12:00:00"));
        await store.TryClaimAsync(Entry("2026-07-22", "00:00:01"));

        var day = await store.ListAsync(new DateOnly(2026, 7, 21));

        day.Should().HaveCount(2);
        day.Select(e => e.FireTime).Should().Equal("08:00:00", "12:00:00");
    }

    [Fact]
    public async Task List_preserves_the_source_and_fired_at_fields()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();
        var firedAt = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.FromHours(8));

        await store.TryClaimAsync(new TimerFireEntry("btn:plan-9", "2026-07-21", "08:00:00", "MonthlyPlan", firedAt));

        var entry = (await store.ListAsync(new DateOnly(2026, 7, 21))).Single();

        entry.RefId.Should().Be("btn:plan-9");
        entry.Source.Should().Be("MonthlyPlan");
        entry.FiredAt.ToUniversalTime().Should().Be(firedAt.ToUniversalTime());
    }

    // ═══════════════════════════════════════════════════════════
    // 保留期裁剪
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Prune_deletes_strictly_older_dates_and_keeps_the_boundary()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();

        await store.TryClaimAsync(Entry("2026-07-19", "08:00:00"));
        await store.TryClaimAsync(Entry("2026-07-20", "08:00:00"));
        await store.TryClaimAsync(Entry("2026-07-21", "08:00:00"));

        var deleted = await store.PruneAsync(new DateOnly(2026, 7, 20));

        deleted.Should().Be(1, "只删严格早于保留线的那些天");

        (await store.ListAsync(new DateOnly(2026, 7, 19))).Should().BeEmpty();
        (await store.ListAsync(new DateOnly(2026, 7, 20))).Should().ContainSingle("边界当天必须保留");
        (await store.ListAsync(new DateOnly(2026, 7, 21))).Should().ContainSingle();
    }

    [Fact]
    public async Task Prune_returns_zero_when_nothing_is_old_enough()
    {
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var store = factory.Services.GetRequiredService<ITimerFireStore>();
        await store.TryClaimAsync(Entry("2026-07-21", "08:00:00"));

        (await store.PruneAsync(new DateOnly(2026, 7, 21))).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════

    private static TimerFireEntry Entry(string date, string time, string refId = RefId, string source = "Daily")
        => new(refId, date, time, source, new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.FromHours(8)));
}
