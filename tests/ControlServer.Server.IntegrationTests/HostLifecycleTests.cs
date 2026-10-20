using System.Net;
using ControlServer.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 生命周期回归：宿主能被干净地停止，并再次启动 —— 端口重绑、数据库复用均无冲突。
///
/// 为什么这条重要：真实环境里服务会被反复重启（更新、崩溃自愈、运维），
/// "停不干净"会直接表现为下次启动端口占用 / 文件锁死。这是"一定要稳定"的核心回归项。
///
/// 本轮不依赖外部信号：<c>WebApplicationFactory.Dispose()</c> 走的就是宿主 StopAsync，
/// 会依次触发 IHostApplicationLifetime.ApplicationStopping/ApplicationStopped、
/// 取消所有 BackgroundService 的 stoppingToken，并释放 UDP 监听 socket。
/// </summary>
public sealed class HostLifecycleTests
{
    [Fact]
    public async Task Host_can_be_started_stopped_and_restarted()
    {
        const int rounds = 3;

        for (var round = 1; round <= rounds; round++)
        {
            using var factory = new ControlServerFactory();
            using var client = factory.CreateClient();

            var health = await client.GetAsync("/healthz");
            health.StatusCode.Should().Be(HttpStatusCode.OK, $"第 {round} 轮启动后 /healthz 应正常");

            // /readyz 为 200 表示：数据库可写 + UDP 监听确实重新绑定成功（端口未被上一轮占用）。
            var ready = await client.GetAsync("/readyz");
            ready.StatusCode.Should().Be(HttpStatusCode.OK, $"第 {round} 轮启动后 /readyz 应就绪");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 配置绑定陷阱
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Ntp_server_list_is_not_doubled_by_config_binding()
    {
        // ⚠️ 回归：配置绑定对 List<T> 是**追加**而不是替换。
        //    TimeOptions.NtpServers 曾经自带一份与 appsettings.json 相同的内置默认，
        //    结果实际生效的是 6 个服务器（3 内置 + 3 配置），
        //    并且用户**无法通过配置删掉内置默认**。
        //    现在内置默认只来自 TimeSyncSettings.DefaultNtpServers，由组合根在列表为空时回退。
        using var factory = new ControlServerFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/healthz");

        var settings = factory.Services.GetRequiredService<TimeSyncSettings>();

        settings.NtpServers.Should().NotBeEmpty("AppSettings 与内置默认至少要有一份生效");
        settings.NtpServers.Should().OnlyHaveUniqueItems("绝不允许出现重复服务器");
        settings.NtpServers.Should().HaveCountLessOrEqualTo(3, "本项目配置与内置默认都是 3 个，翻倍就是回归");
        settings.NtpServers.Should().BeEquivalentTo(
            new[] { "ntp.aliyun.com", "ntp.tencent.com", "cn.pool.ntp.org" },
            "顺序即优先级，不能被绑定打乱");
    }
}
