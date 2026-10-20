using ControlServer.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.Hubs;

/// <summary>
/// 实时事件通道（/hubs/events）。
/// 服务端推送：LogAdded / StatusChanged / ActionResult（事件名见 ControlServer.Contracts.EventNames）。
/// 注意：SignalR 不依赖桌面会话，因此"无界面服务 + 浏览器实时"在 Session 0 下同样成立。
/// </summary>
public sealed class EventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", new
        {
            hub = nameof(EventsHub),
            serverTime = BeijingTime.Now,
        }).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
