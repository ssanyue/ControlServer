using System.Text;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>这份配置是<b>谁写进来的</b>。</summary>
/// <remarks>
/// 存进文件里而不是只留内存：重启之后"最后一份配置是中心给的还是本机改的"必须还能答上来 ——
/// 现场看到"机器跑的和网页显示的不一样"时，第一个要问的就是这一句。
/// </remarks>
public static class AgentConfigOrigins
{
    /// <summary>来自中心服务端的下发（或降级路径上的中心快照）。</summary>
    public const string Server = "server";

    /// <summary>来自本机的「本机配置」页面（2026-10-14 起客户机可独立编辑）。</summary>
    public const string Local = "local";
}

/// <summary>
/// 落盘的<b>本机配置文档</b>：当前生效的那份配置 + 它是谁写的 + 有没有"还没推给中心"的改动。
/// </summary>
/// <param name="Config">当前生效的整份配置（含版本号与时间戳）。</param>
/// <param name="Origin">见 <see cref="AgentConfigOrigins"/>。</param>
/// <param name="HasUnsyncedLocalEdit">
/// 本机改过、还没推给中心（2026-10-14 起）。
/// <para>
/// 🔥 <b>必须落盘</b>：客户机"独立使用"是常态 —— 离线时在本机页面上改的那一版，要能
/// <b>跨重启</b>活到下次连上中心。只留在内存里的标志会在服务重启（或整机重启）后丢掉，
/// 于是那份改动<b>再也不会被推上去</b>，而两边都不会报错：本机照它跑、中心显示的是旧配置。
/// </para>
/// <para><c>null</c> = 这个字段出现之前写下的文档：按"来源是本机就算待同步"推断（见
/// <see cref="AgentConfigManager.RestoreFromStore"/>）。</para>
/// </param>
public sealed record AgentLocalConfigDocument(
    AgentConfigDto Config,
    string Origin,
    bool? HasUnsyncedLocalEdit = null);

/// <summary>
/// 客户机上那份<b>可写</b>的配置（M4.5 的只读缓存 → 2026-10-14 起可写）。
///
/// <para><b>它是什么</b>：这台机器"当前生效的那一份配置"的唯一落盘处。
/// 两个写入方：中心服务端下发（<see cref="AgentConfigManager.SyncAsync"/>）与
/// 本机配置页面（<see cref="AgentConfigManager.ApplyLocalEdit"/>）。</para>
///
/// <para>🔥 <b>为什么从"只读缓存"改成"可写"</b>：用户明确要求"客户端本身可独立使用"——
/// 离线也能改延时 / 循环 / 定时 / 启动程序 / UDP 端口。原来那份只读缓存的前提
/// （决策 D5：事实来源只有服务端）已经被<b>双向·后写覆盖</b>取代：两边都能改，
/// 谁后写谁生效（版本号优先、同版本比时间戳，见 <c>AgentConfigMapper.IsNewer</c>）。</para>
///
/// <para>⚠️ <b>它仍然只存"已经应用成功的那一版"</b>，不是"拿到手的最后一版"：
/// 拉回来但应用失败的配置不该留下痕迹 —— 否则降级运行会按一份从没生效过的配置跑。</para>
/// </summary>
public sealed class AgentLocalConfigStore
{
    private readonly string _path;
    private readonly LogService _log;

    public AgentLocalConfigStore(string stateDirectory, LogService log)
    {
        _path = AgentPaths.LocalConfigFile(stateDirectory);
        _log = log;
    }

    /// <summary>文件路径（排障用；日志里会打出来）。</summary>
    public string FilePath => _path;

    /// <summary>保存一版配置。失败只记一条日志 —— 写不出去不该影响本次运行。</summary>
    /// <param name="config">要落盘的那份配置。</param>
    /// <param name="origin">谁写的（见 <see cref="AgentConfigOrigins"/>）。</param>
    /// <param name="hasUnsyncedLocalEdit">
    /// 这份改动有没有"还没推给中心"的成分。<b>刻意不给默认值</b>：
    /// 它决定"重启后还要不要补推"，让每个调用点都必须显式表态，比默认成 false 安全。
    /// </param>
    public void Save(AgentConfigDto config, string origin, bool hasUnsyncedLocalEdit)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 与凭据落盘同一手法：临时文件 + 原子替换。半截 JSON 会让下次启动
            // 读不出配置 → 表现为"离线时任务全没了"，而那个症状看起来像服务端问题。
            var document = new AgentLocalConfigDocument(config, origin, hasUnsyncedLocalEdit);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, ControlJson.Serialize(document), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"写本机配置失败（不影响本次运行）：{ex.Message}");
        }
    }

    /// <summary>读配置。<b>任何异常都返回 null</b>（按"本机还没有配置"处理）。</summary>
    public AgentLocalConfigDocument? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var json = File.ReadAllText(_path, Encoding.UTF8);

            // 先按"文档"读（带 origin）。更早的版本写下的是一份裸配置（没有 origin 包裹），
            // 那样读出来会得到一份 Origin 为空的文档 —— 直接回退到裸配置解析，
            // 把 origin 记成"中心下发"（那个版本只可能是中心写进来的）。升级不需要手工清文件。
            var document = ControlJson.Deserialize<AgentLocalConfigDocument>(json);

            if (document?.Config is not null && !string.IsNullOrWhiteSpace(document.Origin))
            {
                return document;
            }

            var legacy = ControlJson.Deserialize<AgentConfigDto>(json);

            if (legacy is null) return null;

            _log.Info("Config", "读到的是旧版（无来源标记）的配置缓存，按「中心下发」处理。");

            return new AgentLocalConfigDocument(legacy, AgentConfigOrigins.Server, HasUnsyncedLocalEdit: false);
        }
        catch (Exception ex)
        {
            _log.Warn("Config", $"读本机配置失败（按无配置处理）：{ex.Message}");
            return null;
        }
    }

    /// <summary>清配置（被吊销时用）。失败静默。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 忽略。
        }
    }
}
