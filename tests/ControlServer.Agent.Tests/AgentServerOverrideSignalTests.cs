using ControlServer.Agent;
using ControlServer.Agent.Config;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 「本机页改了服务端地址」的<b>唤醒信号</b>（<see cref="AgentServerOverride"/>）。
///
/// <para>
/// 🔥 <b>为什么这条要单独测</b>：现场实测（2026-10-20 09:20:09 → 09:20:57）
/// 地址改对之后，客户端<b>等了 48 秒</b>才真的去连 —— 那一刻主循环正睡在 60 秒退避里。
/// 用户的判断是"改了没用"，于是会再改一次。这个信号就是让"等退避"变成
/// "等退避或等变更"，改完立刻重连。
/// </para>
///
/// <para>
/// ⚠️ 它只负责<b>叫醒</b>：醒来之后主循环仍然要重新读 <see cref="AgentServerOverride.Url"/>、
/// 并且走一次 <see cref="AgentServerOverride.ConsumeChangePending"/>（那里还要作废旧凭据）。
/// 所以这里同时钉住"叫醒"与"待处理标志"是<b>两条独立信息</b>。
/// </para>
/// </summary>
public class AgentServerOverrideSignalTests
{
    [Fact]
    public async Task Setting_an_address_wakes_a_pending_backoff_wait()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-override-" + Guid.NewGuid().ToString("N"));

        try
        {
            var log = new LogService(new TestDataDirectory(root), capacity: 100, flushIntervalMs: 600_000);

            try
            {
                var sut = new AgentServerOverride(new AgentOptions { StateDirectory = root }, log);

                var waiting = sut.WaitForChangeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

                waiting.IsCompleted.Should().BeFalse("没人改地址时它就是一次普通退避");

                sut.Set("http://10.0.0.9:8899");

                (await waiting).Should().BeTrue("改完地址必须当场打断退避，否则最长要等 60 秒");

                sut.ChangePending.Should().BeTrue("叫醒 ≠ 已处理：作废凭据那一步还没做");
                sut.ConsumeChangePending().Should().BeTrue();

                (await sut.WaitForChangeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
                    .Should().BeFalse("已经处理过的变更不该让下一次退避立刻返回");
            }
            finally
            {
                await log.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响断言结论。
            }
        }
    }

    [Fact]
    public async Task Repeated_saves_do_not_stack_up_signals()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-override-" + Guid.NewGuid().ToString("N"));

        try
        {
            var log = new LogService(new TestDataDirectory(root), capacity: 100, flushIntervalMs: 600_000);

            try
            {
                var sut = new AgentServerOverride(new AgentOptions { StateDirectory = root }, log);

                // 连续保存三次：容量 1 的信号不该溢出（溢出会抛 SemaphoreFullException，把保存请求打崩）。
                sut.Set("http://10.0.0.9:8899");
                sut.Set("http://10.0.0.10:8899");
                sut.Set(null);

                (await sut.WaitForChangeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
                    .Should().BeTrue("至少有一次叫醒，然后主循环自己去读当前地址");

                // 第一次唤醒已经吃掉唯一的信号，之后必须是普通等待。
                (await sut.WaitForChangeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
                    .Should().BeFalse();
            }
            finally
            {
                await log.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 同上。
            }
        }
    }
}
