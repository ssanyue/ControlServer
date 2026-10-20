using ControlServer.Core.Services;

namespace ControlServer.Agent.Transfer;

/// <summary>
/// 客户端<b>默认下发目录</b>与短标识候选的<b>唯一解析实现</b>（M4.4）。
/// <para>
/// <b>为什么必须只有一份</b>：启动日志要报"本机默认落到哪个目录"，传输时要拿它解析
/// 空值与短标识。若两处各写一遍，迟早出现"日志里说 A、实际却落到 B"——
/// 而那是排障时最误导人的一种不一致（"日志明明写着没问题"）。
/// </para>
/// <para>
/// ⚠️ <b>2026-10-20 起它不再是"可写白名单"</b>：绝对路径直接采用，本表只用于
/// "没指定目标目录"和短标识（<c>inbox</c> / <c>logs</c>）的匹配。
/// 见 <see cref="TransferOptions"/> 类注释里的代价说明。
/// </para>
/// </summary>
public static class TransferPaths
{
    /// <summary>
    /// 默认下发目录候选。<b>默认目录排在最前</b>，它同时是"任务没指定目标目录"时的落点。
    /// <para>不配置就落到数据目录下的 <c>inbox</c>。</para>
    /// </summary>
    public static IReadOnlyList<string> InboxWhitelist(TransferOptions options, string stateDirectory)
    {
        var raw = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.DefaultInboxDirectory))
            raw.Add(options.DefaultInboxDirectory);

        raw.AddRange(options.InboxDirectories);

        if (raw.Count == 0) raw.Add(Path.Combine(stateDirectory, "inbox"));

        return FileTransferMath.NormalizeDirectories(raw);
    }

    /// <summary>
    /// 本机可访问的根集合（"此电脑"那一层，即所有盘符，含未就绪的光驱 / 网络盘）。
    /// <para>
    /// 用途有两个：① 「浏览」的顶层；② 上报给中心，当作回传源路径输入框的<b>候选提示</b>。
    /// </para>
    /// <para>
    /// 🔥 <b>只留这一份实现</b>（2026-10-15）：从前浏览用 <c>DriveInfo.GetDrives()</c>、
    /// 回传硬编码一个 outbox 目录，两处各写一遍 —— 结果就是"浏览里能选中、回传必被拒"那次事故。
    /// 两处分散实现迟早再分叉一次，所以收进这里。
    /// </para>
    /// <para>
    /// 未就绪的盘照样列出来：列出来只是"名字能看见"，真要去读它自然会失败并给出原因，
    /// 而把它过滤掉反而会让用户以为"这台机器根本没有 D 盘"。
    /// </para>
    /// </summary>
    /// <param name="onError">枚举失败时的回报口（调用方各自决定记日志还是静默）。</param>
    public static IReadOnlyList<string> DriveRoots(Action<string>? onError = null)
    {
        var list = new List<string>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Name 形如 `C:\`；它既是"名字"也是"路径"，最省事也最不会错。
                if (!string.IsNullOrWhiteSpace(drive.Name)) list.Add(drive.Name);
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke($"枚举盘符失败：{ex.Message}");
        }

        return list;
    }
}
