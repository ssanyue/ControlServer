using ControlServer.Core.Services;

namespace ControlServer.Agent.Config;

/// <summary>
/// 被控机上那条<b>「这台机器不接受远程键鼠」</b>的本机开关（M5.2）。
///
/// <para><b>它在闸门体系里的位置</b>：M5.2 的输入回传有<b>两道</b>闸门，互相独立：</para>
/// <list type="number">
///   <item><b>中心侧</b>的会话级开关（<c>RemoteSessionDto.InputEnabled</c>）——
///        观看端每开一路画面就要有人显式打开一次，<b>默认关</b>。它防的是"误点一下就动了别人的机器"。</item>
///   <item><b>被控端</b>的这个本机开关 —— 一旦关上，<b>这台机器自己</b>不建输入管道、
///        中心再怎么下发也注不进去。它防的是"中心（或拿到中心管理员账号的人）单方面决定了
///        某台机器可以被远程操作"。</item>
/// </list>
///
/// <para>🔒 <b>为什么它必须是一个本机文件、而不是中心下发的配置项</b>：
/// 如果它走中心下发，那么能改中心配置的人就能把被控机的否决翻回来 ——
/// 那这道闸门就只是"建议"而不是"否决"。所以它<b>刻意不属于 <c>AgentConfigDto</c></b>，
/// 也不在任何同步路径上：整包替换、版本递增那些机制一概碰不到它。
/// （这与铁律「作用到别的机器的写操作必须在客户端做归属校验」是同一条思路：
/// 决定权留在被作用的那一台上。）</para>
///
/// <para>📄 <b>为什么是一个"标记文件"而不是一份 JSON</b>：
/// 翻转这件事多半发生在现场、手边只有记事本的时候，所以要满足两条：</para>
/// <list type="bullet">
///   <item><b>不需要记住字段名</b>：新建一个空文件、改名为 <c>remote-input.deny</c> 即生效；
///         删掉它即恢复。JSON 方案里把 <c>allowInput</c> 敲错一个字母，
///         结果是"文件在、但没人认"，而现场看到的是"文件都建了怎么还不停"。</item>
///   <item><b>没有"半句话"状态</b>：JSON 写到一半被断电，读出来是一个<b>语法错误</b> ——
///         此时"算禁止还是算允许"没有正确答案，而两种猜法在现场都无从解释。
///         标记文件只有"在"与"不在"两种状态，不存在第三种。</item>
/// </list>
/// <para>代价是它<b>只能表达"禁止"</b>（允许 = 文件不存在）。这不算缺陷：
/// 允许本来就是默认值，没人需要"用一个文件来证明自己允许"。</para>
///
/// <para>⚠️ <b>拿不准的时候按"允许"算（fail-open）</b>。这里刻意<b>没有</b>选 fail-closed，
/// 理由要对齐后果：</para>
/// <list type="bullet">
///   <item>fail-closed 意味着一次路径/权限的意外 → 远程控制<b>静默失效</b>，
///         现场看到的是"输入一直不可用"，而真正的原因在日志里只有一行，没人会往那儿想；</item>
///   <item>fail-open 的后果是<b>没有否决</b>，但中心那层闸门仍然关着（默认关、且要管理员开），
///         而且这个开关的定位是<b>运维偏好</b>而不是"对抗本机恶意进程"的安全边界
///         —— 能往这台机器的 ProgramData 里写文件的人，本来就已经能改客户端了。</item>
/// </list>
/// <para>一句话：<b>明确的"禁止"一定生效；拿不准的时候不假装禁止。</b></para>
///
/// <para>⏱ <b>为什么不是"启动时读一次"</b>：否决要能在<b>不重启客户端</b>的前提下生效 ——
/// 用户把文件建出来，下一个会话就不该再能控制。所以按 <see cref="CheckInterval"/>
/// 重新看一眼（两个进程各看一眼，都只是查文件存不存在，代价可以忽略）。</para>
/// </summary>
public sealed class AgentInputPolicy
{
    /// <summary>
    /// 否决标记文件名。<b>它存在 = 这台机器不接受远程键鼠注入。</b>
    /// </summary>
    /// <remarks>
    /// 扩展名刻意不是 <c>.json</c>：它<b>不是</b>一份数据文件，打开只会看到空内容 ——
    /// 用 <c>.json</c> 会让人以为"里面应该有两行配置"，然后开始往里面写字
    /// （写了也不影响行为，于是更迷惑）。
    /// </remarks>
    public const string DenyFileName = "remote-input.deny";

    /// <summary>多久重新看一眼文件系统（5 秒：翻开关后最多 5 秒生效，够快，也不至于每秒查盘）。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 标记文件的完整路径。
    /// <para>
    /// 🔥 它是<b>静态</b>的，因为托盘那个进程是零 DI、不持有本类实例的
    /// （它只需要"读一眼"，不需要 <see cref="Set"/>，也没有 <c>LogService</c>）——
    /// 让两边共用同一个"文件该放在哪"的答案，比各写一遍 <c>Path.Combine</c> 可靠。
    /// </para>
    /// </summary>
    public static string DenyPath(string stateDirectory) => Path.Combine(stateDirectory, DenyFileName);

    /// <summary>
    /// 一次性判断：<b>此刻这台机器允不允许远程输入</b>。不缓存、不记日志。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>整个项目里"文件在 = 禁止"这个取反只写在这一行</b>。
    /// 它写反了不会报任何错，只会让这个开关彻底失效（甚至反过来）——
    /// 所以它必须只有一个出处，让任何调用方都绕着它走。
    /// </remarks>
    public static bool AllowsInputIn(string stateDirectory) => !File.Exists(DenyPath(stateDirectory));

    private readonly string _stateDirectory;
    private readonly string _path;
    private readonly LogService _log;

    private readonly object _sync = new();

    private bool _allows = true;
    private DateTime _checkedAtUtc = DateTime.MinValue;

    public AgentInputPolicy(AgentOptions options, LogService log)
    {
        _log = log;

        _stateDirectory = AgentPaths.ResolveStateDirectory(options.StateDirectory);
        _path = DenyPath(_stateDirectory);
    }

    /// <summary>
    /// 标记文件在哪。排障时"我这个开关到底该放在哪"就靠它 ——
    /// 所以要能打进日志，不能只让人去猜目录。
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// 这台机器此刻允不允许远程键鼠注入。
    /// <para>调用点只有两处（每路会话开始时各看一次），所以这里<b>不是</b>每帧调用的热路径。</para>
    /// </summary>
    public bool AllowsInput
    {
        get
        {
            lock (_sync)
            {
                if (DateTime.UtcNow - _checkedAtUtc < CheckInterval) return _allows;

                _checkedAtUtc = DateTime.UtcNow;

                var previous = _allows;

                // 🔥 取反那一步在 AllowsInputIn 里（整个项目只有那一处），这里不重复写。
                _allows = AllowsInputIn(_stateDirectory);

                if (_allows != previous)
                {
                    // 状态翻转必须留痕：现场遇到"刚才还能点、现在点不动了"时，
                    // 这一行直接给出答案，否则要在一堆日志里找"谁动了什么"。
                    _log.Info(
                        "Remote",
                        _allows
                            ? $"这台机器已允许远程键鼠注入（标记文件 {_path} 已被移除）。"
                            : $"这台机器已禁止远程键鼠注入（检测到标记文件 {_path}）—— "
                              + "中心无法覆盖这个决定，此后新开的远控会话只能看、不能控。");
                }

                return _allows;
            }
        }
    }

    /// <summary>
    /// 翻这个开关（写 / 删标记文件）。
    /// <para>
    /// ⚠️ 它<b>只负责落盘</b>：翻转后多久生效由 <see cref="CheckInterval"/> 决定，
    /// 调用方不需要（也不该）去通知谁 —— 那样就变成"翻开关的人必须知道有几个进程在读它"。
    /// </para>
    /// </summary>
    /// <returns>写完之后的实际状态；写失败时返回<b>没变</b>的那一侧，让调用方如实回报。</returns>
    public bool Set(bool allowInput)
    {
        try
        {
            if (allowInput)
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            else
            {
                var directory = Path.GetDirectoryName(_path);

                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 空文件即可 —— 内容不参与判断（见类注释）。
                using var _ = File.Create(_path);
            }

            lock (_sync)
            {
                _allows = allowInput;
                _checkedAtUtc = DateTime.UtcNow;
            }

            _log.Info("Remote", allowInput
                ? $"已允许本机接受远程键鼠注入（{_path}）。"
                : $"已禁止本机接受远程键鼠注入（{_path}）—— 这是本机的否决，中心无法覆盖。");

            return allowInput;
        }
        catch (Exception ex)
        {
            // ⚠️ 写失败必须让用户知道：页面上刚说了"已保存"，而它重启后就会失效。
            _log.Error("Remote", $"写入远程输入开关失败（重启后会丢失）：{ex.Message}", ex);

            return AllowsInput;
        }
    }
}
