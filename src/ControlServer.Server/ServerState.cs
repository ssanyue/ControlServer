using ControlServer.Core.Services;

namespace ControlServer.Server;

/// <summary>进程级运行状态（启动时刻 / 运行时长）。</summary>
public sealed class ServerState
{
    public DateTimeOffset StartedAt { get; } = BeijingTime.Now;

    public TimeSpan Uptime => BeijingTime.Now - StartedAt;
}
