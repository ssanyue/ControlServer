using ControlServer.Core;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 配置持久化与**乐观锁**回归。
/// <para>
/// 这些用例覆盖的是"旧版不存在、服务版必须解决"的问题：
/// 多浏览器同时编辑同一份配置会丢更新。M2 方案 §4.3 要求「后写者拿到 409」。
/// </para>
/// 每个测试独占一个宿主（含独立数据目录）。
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private T Get<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    // ═══════════════════════════════════════════════════════════
    // 首次读取
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task First_read_reports_missing_at_version_zero()
    {
        var service = Get<ControlSettingsService>();

        var view = await service.GetAsync();

        view.Version.Should().Be(0);
        view.State.Should().Be(ConfigLoadState.Missing);
        view.Settings.Buttons.Should().BeEmpty();
        view.Settings.AppName.Should().Be("中控");
    }

    [Fact]
    public async Task Reading_does_not_create_a_document()
    {
        var service = Get<ControlSettingsService>();
        var store = Get<IConfigDocumentStore>();

        await service.GetAsync();
        await service.GetAsync();

        (await store.ListKeysAsync()).Should().NotContain(ConfigKeys.Settings, "只读操作不应产生副作用");
    }

    // ═══════════════════════════════════════════════════════════
    // 版本推进
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Save_from_version_zero_yields_version_one()
    {
        var service = Get<ControlSettingsService>();
        var view = await service.GetAsync();

        var version = await service.SaveAsync(view.Settings, view.Version, "tester");

        version.Should().Be(1);

        var reloaded = await service.GetAsync();
        reloaded.Version.Should().Be(1);
        reloaded.State.Should().Be(ConfigLoadState.Ok);
    }

    [Fact]
    public async Task Version_increases_monotonically_on_each_save()
    {
        var service = Get<ControlSettingsService>();

        var v0 = (await service.GetAsync()).Version;
        var v1 = await service.SaveAsync(new ControlSettings { AppName = "一" }, v0, "tester");
        var v2 = await service.SaveAsync(new ControlSettings { AppName = "二" }, v1, "tester");

        v1.Should().Be(1);
        v2.Should().Be(2);

        var final = await service.GetAsync();
        final.Version.Should().Be(2);
        final.Settings.AppName.Should().Be("二");
    }

    // ═══════════════════════════════════════════════════════════
    // 乐观锁冲突
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Save_with_stale_version_throws_concurrency_exception()
    {
        var service = Get<ControlSettingsService>();
        var stale = await service.GetAsync();

        // 会话 A 先改。
        await service.SaveAsync(new ControlSettings { AppName = "A改的" }, stale.Version, "A");

        // 会话 B 拿着旧版本再改 → 必须冲突，而不是静默覆盖 A。
        var act = async () => await service.SaveAsync(new ControlSettings { AppName = "B改的" }, stale.Version, "B");

        var ex = await act.Should().ThrowAsync<ConfigConcurrencyException>();
        ex.Which.Key.Should().Be(ConfigKeys.Settings);
        ex.Which.ExpectedVersion.Should().Be(stale.Version);
        ex.Which.ActualVersion.Should().Be(1);

        // A 的修改必须保住。
        (await service.GetAsync()).Settings.AppName.Should().Be("A改的");
    }

    [Fact]
    public async Task Save_without_expected_version_skips_optimistic_check()
    {
        var service = Get<ControlSettingsService>();
        await service.SaveAsync(new ControlSettings { AppName = "x" }, 0, "seed");

        // expectedVersion = null → 内部调用路径，不做校验。
        var version = await service.SaveAsync(new ControlSettings { AppName = "y" }, null, "system");

        version.Should().Be(2);
    }

    [Fact]
    public async Task Store_rejects_stale_write_and_keeps_version_unchanged()
    {
        var store = Get<IConfigDocumentStore>();

        var v1 = await store.WriteAsync(ConfigKeys.Settings, "{\"appName\":\"one\"}", null, "seed");
        v1.Should().Be(1);

        var act = async () => await store.WriteAsync(ConfigKeys.Settings, "{\"appName\":\"two\"}", 0, "stale");
        await act.Should().ThrowAsync<ConfigConcurrencyException>();

        var snapshot = await store.ReadAsync(ConfigKeys.Settings);
        snapshot.Version.Should().Be(1);
        snapshot.Value.Should().Contain("one");
    }

    [Fact]
    public async Task Only_one_of_two_concurrent_writers_wins()
    {
        var store = Get<IConfigDocumentStore>();
        await store.WriteAsync(ConfigKeys.Settings, "{\"appName\":\"seed\"}", null, "seed");

        var snapshot = await store.ReadAsync(ConfigKeys.Settings);

        async Task<bool> TryWrite(string payload, string who)
        {
            try
            {
                await store.WriteAsync(ConfigKeys.Settings, payload, snapshot.Version, who);
                return true;
            }
            catch (ConfigConcurrencyException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => TryWrite("{\"appName\":\"A\"}", "A")),
            Task.Run(() => TryWrite("{\"appName\":\"B\"}", "B")));

        results.Count(ok => ok).Should().Be(1, "同一版本上只能有一个写入成功");
        (await store.ReadAsync(ConfigKeys.Settings)).Version.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    // 变更事件
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Save_raises_config_changed_event()
    {
        var service = Get<ControlSettingsService>();
        var events = new List<ConfigChangedEvent>();
        service.Changed += e => events.Add(e);

        var view = await service.GetAsync();
        await service.SaveAsync(view.Settings, view.Version, "tester");

        events.Should().ContainSingle();
        events[0].Key.Should().Be(ConfigKeys.Settings);
        events[0].Version.Should().Be(1);
        events[0].ChangedBy.Should().Be("tester");
    }

    // ═══════════════════════════════════════════════════════════
    // 细粒度操作：仍走整份读改写 + 排序归一化
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Adding_items_assigns_order_and_normalizes_immediately()
    {
        var service = Get<ControlSettingsService>();

        await service.AddButtonAsync(new ButtonConfig { Label = "按钮1" }, null, "tester");
        await service.AddSubButtonSetAsync(new SubButtonSet { Label = "集1" }, null, "tester");
        await service.AddButtonAsync(new ButtonConfig { Label = "按钮2" }, null, "tester");

        var view = await service.GetAsync();

        view.Settings.Buttons.Select(b => b.Order).Should().Equal(0, 2);
        view.Settings.SubButtonSets.Select(s => s.Order).Should().Equal(1);
        view.Version.Should().Be(3);
    }

    [Fact]
    public async Task Reorder_mixes_buttons_and_sets()
    {
        var service = Get<ControlSettingsService>();

        var v1 = await service.AddButtonAsync(new ButtonConfig { Label = "按钮1" }, null, "tester");
        var buttonId = v1.Settings.Buttons[0].Id;

        var v2 = await service.AddSubButtonSetAsync(new SubButtonSet { Label = "集1" }, null, "tester");
        var setId = v2.Settings.SubButtonSets[0].Id;

        // 把集合提到按钮前面。
        var v3 = await service.ReorderAsync(new List<string> { setId, buttonId }, null, "tester");

        v3.Settings.SubButtonSets[0].Order.Should().Be(0);
        v3.Settings.Buttons[0].Order.Should().Be(1);
    }

    [Fact]
    public async Task Duplicating_button_creates_independent_copy_with_fresh_ids()
    {
        var service = Get<ControlSettingsService>();

        var view = await service.AddButtonAsync(
            new ButtonConfig
            {
                Label = "原始",
                PairingCode = "P1",
                Actions = { new ButtonAction { Name = "动作", DataContent = "hi" } },
            },
            null,
            "tester");

        var originalId = view.Settings.Buttons[0].Id;
        var originalActionId = view.Settings.Buttons[0].Actions[0].Id;

        var after = await service.DuplicateButtonAsync(originalId, null, "tester");

        after.Settings.Buttons.Should().HaveCount(2);
        after.Settings.Buttons[1].Id.Should().NotBe(originalId);
        after.Settings.Buttons[1].Actions[0].Id.Should().NotBe(originalActionId);
        after.Settings.Buttons[1].PairingCode.Should().Be("P1");
        // 复制出来的按钮必须紧跟原按钮。
        after.Settings.Buttons[1].Order.Should().Be(after.Settings.Buttons[0].Order + 1);
    }

    [Fact]
    public async Task Factory_reset_clears_everything()
    {
        var service = Get<ControlSettingsService>();

        await service.AddButtonAsync(new ButtonConfig { Label = "按钮" }, null, "tester");
        await service.AddSubButtonSetAsync(
            new SubButtonSet { Label = "集", SubButtons = { new SubButton { Label = "子" } } },
            null,
            "tester");

        var after = await service.FactoryResetAsync(null, "tester");

        after.Settings.Buttons.Should().BeEmpty();
        after.Settings.SubButtonSets.Should().BeEmpty();
        after.Settings.AppName.Should().Be("中控");

        var counts = await service.GetCountsAsync();
        counts.Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Clearing_custom_images_only_touches_image_fields()
    {
        var service = Get<ControlSettingsService>();

        var view = await service.AddButtonAsync(
            new ButtonConfig { Label = "按钮", ButtonImage = "a.png", PairingCode = "KEEP" },
            null,
            "tester");

        var after = await service.ClearCustomImagesAsync(null, "tester");

        after.Settings.Buttons[0].ButtonImage.Should().BeEmpty();
        after.Settings.Buttons[0].PairingCode.Should().Be("KEEP", "清图片不应动其它字段");
        after.Settings.DefaultButtonImage.Should().BeEmpty();
    }

    [Fact]
    public async Task Config_survives_host_restart()
    {
        var service = Get<ControlSettingsService>();
        await service.AddButtonAsync(new ButtonConfig { Label = "重启前" }, null, "tester");

        // 同一个数据目录重新起一个宿主，配置必须还在（落到了 SQLite 而不是内存）。
        using var second = new ControlServerFactory(_factory.DataDirectory);
        using var client = second.CreateClient();
        await client.GetAsync("/healthz");

        var restarted = second.Services.GetRequiredService<ControlSettingsService>();
        var view = await restarted.GetAsync();

        view.Settings.Buttons.Should().ContainSingle();
        view.Settings.Buttons[0].Label.Should().Be("重启前");
        view.Version.Should().Be(1);
    }
}
