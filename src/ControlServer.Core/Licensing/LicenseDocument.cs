namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权文件的载荷（字段名与参照实现 HudongJingling 的 <c>license.dat</c> 保持一致）。
/// <para>
/// <b>为什么沿用对方的字段名而不是起一套更漂亮的</b>：这些名字会出现在
/// 授权文件正文、排查话术、以及将来可能的"把旧客户授权迁过来"的脚本里。
/// 名字一致，迁移就是字段级的一一对应；名字自创，就得先做一张映射表，且每次对照都要翻两边。
/// </para>
/// <para>
/// ⚠️ <b>本类型是签名载荷的载体</b>：<see cref="LicenseCodec.BuildPayload"/> 会按<b>固定字段序</b>
/// 把它序列化成确定性文本。因此**加字段、改字段序、改数值格式都会让所有已签发授权失效**。
/// 要动这里，等于要重新签发全部历史授权。
/// </para>
/// </summary>
public sealed class LicenseDocument
{
    /// <summary>当前支持的授权文件版本。读取时不等于该值的文件一律拒绝。</summary>
    public const int CurrentVersion = 1;

    /// <summary>版本号（<c>v</c>）。</summary>
    public int V { get; set; } = CurrentVersion;

    /// <summary>
    /// 绑定的机器码（<c>mc</c>）。
    /// <para>
    /// <b>通用授权在签发时留空</b>，由客户首次导入时绑定 —— 但注意：绑定结果<b>不写回本字段</b>，
    /// 因为改本字段会破坏签名（签发方持有私钥，服务端只有公钥、无法重签）。
    /// 绑定结果落在 <see cref="LicenseBinding"/> 侧车文件里，详见该类型的注释。
    /// </para>
    /// </summary>
    public string Mc { get; set; } = string.Empty;

    /// <summary>授权类型（<c>lt</c>），取值见 <see cref="LicenseKind"/>。</summary>
    public string Lt { get; set; } = string.Empty;

    /// <summary>签发日（<c>id</c>），<c>yyyyMMdd</c>（北京日期）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>到期日（<c>ed</c>），<c>yyyyMMdd</c>（北京日期）；空 = 不按日期限制。</summary>
    public string Ed { get; set; } = string.Empty;

    /// <summary>
    /// 精确到期时刻（<c>et</c>），UTC 毫秒戳；<c>0</c> = 未指定。
    /// <para>存在时优先于 <see cref="Ed"/>；否则回退为 <see cref="Ed"/> 当日北京时间 23:59:59。</para>
    /// </summary>
    public long Et { get; set; }

    /// <summary>
    /// 最大有效天数（<c>md</c>）；<c>0</c> = 不按天数限制。
    /// <para>仅在 <see cref="Et"/> 与 <see cref="Ed"/> 都为空的时序授权里作为兜底（起算点见评估器）。</para>
    /// </summary>
    public int Md { get; set; }

    /// <summary>序列号（<c>sn</c>），仅展示。</summary>
    public string Sn { get; set; } = string.Empty;

    /// <summary>厂商标识（<c>vi</c>），仅展示。</summary>
    public string Vi { get; set; } = string.Empty;

    /// <summary>客户备注（<c>hn</c>），仅展示。</summary>
    public string Hn { get; set; } = string.Empty;

    /// <summary>是否为通用授权（<c>ub</c>）：<c>1</c> = 通用（签发时未绑机器码，首次导入时绑定）。</summary>
    public int Ub { get; set; }

    /// <summary>
    /// 最迟可导入绑定时刻（<c>ud</c>），UTC 毫秒戳；<c>0</c> = 不限。
    /// <para>仅对通用授权有意义：超过这个时刻就不允许再导入绑定，防止一份通用授权被无限期复用。</para>
    /// </summary>
    public long Ud { get; set; }

    /// <summary>
    /// 签发时登记的绑定时刻（<c>bt</c>），UTC 毫秒戳。
    /// <para>由<b>签发方</b>填写（对"指定机器码"的授权即为签发时刻）；<c>0</c> = 未登记。</para>
    /// </summary>
    public long Bt { get; set; }

    /// <summary>
    /// 联网校对时间（<c>ivt</c>），UTC 毫秒戳 —— <b>整个防线最关键的一个字段</b>。
    /// <para>
    /// 正式/试用授权只能由"联网签发 / 联网导入"两条路径产生，两条都会写入本字段。
    /// 于是：手工拼出来的授权文件<b>即使签名合法</b>，也会因缺 <c>ivt</c> 被拒 ——
    /// 堵死"改文件绕过在线校对"这条路。
    /// </para>
    /// <para>校验规则：<c>ivt &gt; 0</c> 且 <c>now + 容差 &gt;= ivt</c>。</para>
    /// </summary>
    public long Ivt { get; set; }

    /// <summary>签名（<c>sig</c>），ECDSA P-256 + SHA-256 的 IEEE-P1363 定长签名，Base64URL 编码。</summary>
    public string Sig { get; set; } = string.Empty;

    /// <summary>深拷贝（不含 <see cref="Sig"/> 之外的特殊处理，仅逐字段复制）。</summary>
    public LicenseDocument Clone() => new()
    {
        V = V,
        Mc = Mc,
        Lt = Lt,
        Id = Id,
        Ed = Ed,
        Et = Et,
        Md = Md,
        Sn = Sn,
        Vi = Vi,
        Hn = Hn,
        Ub = Ub,
        Ud = Ud,
        Bt = Bt,
        Ivt = Ivt,
        Sig = Sig,
    };
}
