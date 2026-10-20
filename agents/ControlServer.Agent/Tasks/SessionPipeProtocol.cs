using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 服务 ↔ 托盘的<b>会话侧命名管道协议</b>（M4.5）。
///
/// <para><b>形状</b>：一条请求 = 一次连接 = 一行 JSON 进去、一行 JSON 出来。</para>
/// <list type="bullet">
///   <item>客户端（服务）连上、写一行、读一行、断开；</item>
///   <item>服务端（托盘）收一行、执行、回一行、断开。</item>
/// </list>
///
/// <para>
/// 🔥 <b>为什么不搞"一条长连接跑多条指令 + 请求 Id 配对"</b>：那需要维护
/// "谁在等哪个 Id"的映射、处理半截响应、处理并发写。而这里要送的东西
/// （按一下音量、拉起一个程序）是<b>一天也没几次</b>的低频动作，
/// 一次连接的开销在本地管道上远低于一百微秒。用最简单的形状换掉一整类
/// "偶发串包"的疑难杂症，是笔划算的买卖。
/// </para>
///
/// <para>
/// ⚠️ <b>行分隔的成立前提是"JSON 里不出现裸换行"</b>：紧凑序列化会把字符串里的
/// 换行转义成 <c>\n</c>（两个字符），因此序列化结果<b>必然只有一行</b>。
/// 反过来，只要谁把 <c>WriteIndented</c> 打开，一条请求就会被拆成好几行 ——
/// 接收方读到第一行就当成完整请求，剩下的行变成"下一条请求"，
/// 表现是"参数莫名其妙丢了半个"。所以这里 <b>绝不复用 <c>ControlJson.Options</c></b>
/// （它是给配置文件用的，<c>WriteIndented = true</c>）。
/// </para>
/// </summary>
public static class SessionPipeProtocol
{
    /// <summary>
    /// 管道名前缀。<b>后面必须接会话编号</b>（见 <see cref="PipeNameForSession"/>）。
    /// </summary>
    public const string PipeNamePrefix = "ControlServer.Agent.Session.";

    /// <summary>单条请求 / 响应的读取超时（毫秒）：连上了却一直不说话的一方会被放弃。</summary>
    public const int RequestTimeoutMs = 5000;

    /// <summary>探测 / 发指令时的连接超时（毫秒）。本机管道要么立刻连上，要么就是不在。</summary>
    public const int ConnectTimeoutMs = 400;

    /// <summary>
    /// UTF-8 <b>不带 BOM</b>。
    /// <para>⚠️ BOM 会粘在第一行 JSON 前面（<c>\uFEFF{…}</c>），解析直接失败，
    /// 而报错信息看起来像"JSON 格式错误"，与编码八竿子打不着。</para>
    /// </summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 管道上的 JSON 约定：<b>camelCase + 紧凑单行 + 中文不转义</b>（见类注释：
    /// 单行是行分隔协议的前提，所以这里的 <c>WriteIndented</c> 只能是 false）。
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// 某个会话的管道名。
    /// <para>
    /// 🔥 <b>按会话编号区分</b>而不是全局共用一个名字：托盘的"单实例"互斥量是
    /// <c>Local\</c> 作用域（每会话一个），多用户同时登录时会有多个托盘同时运行；
    /// 共用一个名字的话，只有先到的能建起管道，服务此后固定敲到<b>先登录那个人的桌面</b>，
    /// 音量改在别人账户里而日志毫无异常。
    /// </para>
    /// </summary>
    public static string PipeNameForSession(int sessionId) => PipeNamePrefix + sessionId;

    /// <summary>
    /// 同一个会话管道名，但<b>带上"哪一套客户端"这个维度</b>（M7.4 §11）。
    ///
    /// <para>
    /// 🔥 <b>为什么需要它</b>：一台机器上跑两套客户端（正式安装的那套 ＋ 验证 / 冒烟用的那套）
    /// 时，两边的托盘会抢**同一个** <c>Local\ControlServer.Agent.Session.1</c>。
    /// 先建起来的那套按"只有本服务账户能连"给管道配上 ACL，于是后建的那套在服务侧
    /// 拿到的是<b>「会话管道存在但无权连接：Access to the path is denied」</b> ——
    /// 这句话读起来像权限配错，实际是**两套客户端撞了名字**。
    /// 结果就是：装了正式客户端的机器上，冒烟脚本永远起不来会话（铁律 12 的形状：
    /// 红的不是代码，是机器上装了什么）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b><paramref name="scope"/> 为空 ⇒ 名字与老版<b>一模一样</b></b>：正式安装的
    /// 那套（服务名 <c>ControlServerAgent</c>）走的就是这一支，升级前后管道名不变，
    /// 不存在"服务还是新版、托盘还是旧版"那几分钟连不上的窗口。
    /// 只有**另配服务名**的那套才会带上自己的名字。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话编号（控制台会话）。</param>
    /// <param name="scope">
    /// 区分用的后缀（调用方传自己的服务名；<c>null</c>/空串 = 不加后缀）。
    /// </param>
    public static string PipeNameForSession(int sessionId, string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? PipeNameForSession(sessionId)
            : PipeNamePrefix + SanitizeScope(scope) + "." + sessionId;

    /// <summary>
    /// 把 scope 收窄成"能放进管道名的一段"：只留字母数字与 <c>-</c>/<c>_</c>/<c>.</c>。
    /// <para>
    /// ⚠️ 两侧（服务与托盘）必须用**同一套**规则，否则算出来的名字只差一个字符，
    /// 而症状是"通道不可用"—— 与"托盘没起来"完全同形。
    /// </para>
    /// </summary>
    private static string SanitizeScope(string scope)
    {
        var buffer = new char[scope.Length];

        for (var i = 0; i < scope.Length; i++)
        {
            var c = scope[i];

            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return new string(buffer);
    }

    /// <summary>序列化成<b>一行</b> JSON（不含换行；换行由 <c>WriteLineAsync</c> 补）。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
