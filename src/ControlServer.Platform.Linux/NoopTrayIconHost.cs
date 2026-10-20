using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 的空实现：一律不可用。
/// <para>
/// 存在的意义是让上层<b>不需要任何 <c>if (Linux)</c> 分支</b> —— 调用处只判
/// <see cref="IsAvailable"/>，跨平台代码完全同构（铁律 #2：平台差异只落在 Platform.*）。
/// </para>
/// </summary>
public sealed class NoopTrayIconHost : ITrayIconHost
{
    public bool IsAvailable => false;

    public string? UnavailableReason => "当前平台无桌面托盘（Linux / headless 场景不提供通知区域）。";

    /// <summary>永不触发 —— 用自定义访问器显式表达"这个事件没有订阅者"。</summary>
    public event Action<string>? CommandInvoked
    {
        add { }
        remove { }
    }

    public Task<bool> ShowAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task UpdateAsync(TrayIconOptions options, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ShowBalloonAsync(string title, string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
