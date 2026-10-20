namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 托盘菜单项的<b>稳定标识</b>。
/// <para>
/// 这些值会从 <c>ITrayIconHost.CommandInvoked</c> 里回传，是"宿主"与"托盘实现"之间唯一的约定。
/// Server（绿色版内建托盘）与 TrayAgent（服务版托盘助手）<b>共用同一套 id</b>，
/// 因此新增菜单项时只改这里，不要在各处写字面量。
/// </para>
/// </summary>
public static class TrayCommands
{
    /// <summary>打开管理界面（默认项 / 双击图标同效）。</summary>
    public const string OpenConsole = "open-console";

    /// <summary>
    /// 打开<b>中心服务端</b>的网页管理界面（仅客户端托盘用）。
    /// <para>
    /// 与 <see cref="OpenConsole"/> 的区别：后者打开的是<b>本机</b>的管理界面（服务端托盘），
    /// 这里打开的是<b>本机所连的那台中心服务</b>的界面 —— 客户端本机没有网页可开，
    /// 但"这台机器连的是谁、为什么掉线"往往要跳过去看。
    /// </para>
    /// </summary>
    public const string OpenServerPage = "open-server-page";

    /// <summary>把访问地址复制到剪贴板。</summary>
    public const string CopyAddress = "copy-address";

    /// <summary>在资源管理器中打开数据目录。</summary>
    public const string OpenDataDirectory = "open-data-directory";

    /// <summary>
    /// 打开<b>本机配置页面</b>（仅客户端托盘用，2026-10-14）。
    /// <para>
    /// 客户机自己开的那个小网页（<c>127.0.0.1</c>），用来在没有中心服务端时也能改
    /// 这台机器的延时 / 循环 / 定时 / 启动程序 / UDP 端口。
    /// 与 <see cref="OpenServerPage"/> 恰好相反：那个打开的是<b>别人的</b>界面，这个是<b>自己的</b>。
    /// </para>
    /// </summary>
    public const string OpenLocalConfig = "open-local-config";

    /// <summary>在资源管理器中打开日志目录。</summary>
    public const string OpenLogDirectory = "open-log-directory";

    /// <summary>弹出运行状态（监听地址 / 收发条数 / 数据目录）。</summary>
    public const string ShowStatus = "show-status";

    /// <summary>启动服务（仅服务形态的托盘助手用）。</summary>
    public const string StartService = "start-service";

    /// <summary>停止服务（仅服务形态的托盘助手用）。</summary>
    public const string StopService = "stop-service";

    /// <summary>退出：绿色版 = 停止服务；托盘助手 = 仅退出助手本身。</summary>
    public const string Exit = "exit";

    /// <summary>
    /// 纯展示用的信息行（如"服务状态：运行中"）。
    /// <para>
    /// ⚠️ 存在的理由：<see cref="TrayMenuCommand.IsSeparator"/> 的判据是"<c>Id</c> 为 null"，
    /// 所以"不可点的文字行"**必须**给一个非 null 的 Id —— 否则会被当成分隔线渲染。
    /// 实现方对未识别的 Id 直接忽略即可（菜单项也会渲染成灰色，点不动）。
    /// </para>
    /// </summary>
    public const string StatusInfo = "status-info";
}

/// <summary>
/// 一个托盘菜单项。
/// <para><see cref="Id"/> 为 <c>null</c> 时表示<b>分隔线</b>（此时 <see cref="Text"/> 被忽略）。</para>
/// </summary>
/// <param name="Text">显示文本。</param>
/// <param name="Id">命令标识，见 <see cref="TrayCommands"/>；null = 分隔线。</param>
/// <param name="Enabled">是否可点（false 显示为灰）。</param>
/// <param name="IsDefault">是否为默认项（加粗）。双击图标触发 <see cref="TrayIconOptions.DefaultCommandId"/>。</param>
public sealed record TrayMenuCommand(
    string Text,
    string? Id = null,
    bool Enabled = true,
    bool IsDefault = false)
{
    /// <summary>分隔线。</summary>
    public static TrayMenuCommand Separator { get; } = new("-");

    public bool IsSeparator => Id is null;
}

/// <summary>托盘图标的内容：悬停提示 + 右键菜单。</summary>
/// <param name="ToolTip">鼠标悬停时的提示文本（实现会做长度截断，调用方可给全量）。</param>
/// <param name="Menu">右键菜单项，按顺序显示。</param>
/// <param name="DefaultCommandId">双击图标时触发的命令 id；null = 双击无效。</param>
public sealed record TrayIconOptions(
    string ToolTip,
    IReadOnlyList<TrayMenuCommand> Menu,
    string? DefaultCommandId = null);

/// <summary>
/// 通知区域（Windows 托盘 / 系统右下角）图标。
/// <para>
/// <b>可用性</b>：只有"用户交互式会话 + 有通知区域的平台"才可用。
/// Windows 服务跑在 <b>Session 0</b>（无交互桌面、无 explorer），Linux 无桌面 —— 二者都必须走
/// <c>NoopTrayIconHost</c>。<b>在 Session 0 里建窗口/加托盘是"无声失败"，排障极难</b>，
/// 所以调用方一律先判 <see cref="IsAvailable"/>，不要靠 try/catch 兜。
/// </para>
/// </summary>
public interface ITrayIconHost : IAsyncDisposable
{
    /// <summary>当前平台/会话是否支持托盘（<b>不代表</b>图标已经显示成功，那看 <see cref="ShowAsync"/> 的返回值）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用或显示失败的原因；可用且成功时为 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>创建并显示图标。返回是否成功。</summary>
    Task<bool> ShowAsync(TrayIconOptions options, CancellationToken cancellationToken = default);

    /// <summary>更新悬停提示与菜单（可在运行期调用）。实现应为"投递后立即返回"，不阻塞调用线程。</summary>
    Task UpdateAsync(TrayIconOptions options, CancellationToken cancellationToken = default);

    /// <summary>显示一条气泡通知（可选能力，失败静默）。</summary>
    Task ShowBalloonAsync(string title, string message, CancellationToken cancellationToken = default);

    /// <summary>用户点了某个菜单项（回传 <see cref="TrayMenuCommand.Id"/>）。</summary>
    event Action<string>? CommandInvoked;
}
