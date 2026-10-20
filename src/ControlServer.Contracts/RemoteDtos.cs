namespace ControlServer.Contracts;

/// <summary>
/// 远程桌面会话状态（M5）。
/// <para>
/// 🔥 <b>刻意把"已请求"与"推流中"分开</b>：建会话是"让被控端开始干活"的指令，
/// 它发出去之后，被控端可能正在弹托盘告知、正在创建 D3D 设备、或者干脆
/// 因为没人登录而<b>根本做不到</b>（见 <c>RemoteSessionStates.Failed</c> 与 endedReason）。
/// 把这两态合成一个"开启中"，现场就只能看到"点了没反应"。
/// </para>
/// </summary>
public static class RemoteSessionStates
{
    /// <summary>已向被控端发出请求，尚未开始推流。</summary>
    public const string Requested = "requested";

    /// <summary>正在推流。</summary>
    public const string Streaming = "streaming";

    /// <summary>已正常结束（观众主动断开 / 心跳超时）。</summary>
    public const string Stopped = "stopped";

    /// <summary>失败（被控端无硬编 / 无人登录 / 托盘不在）。</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 视频编码格式的<b>线上表示</b>（M5）。
/// <para>
/// 🔥 <b>为什么用字符串而不是枚举</b>：远端与会话状态都要经过 JSON、且要能表达
/// "还不知道"（<c>null</c>）。枚举的默认值 <c>0</c> 会悄悄变成某个具体格式
/// ——那正是"未知被写成已知"，与能力探测的第一原则冲突。
/// </para>
/// </summary>
public static class RemoteCodecs
{
    /// <summary>H.264 / AVC。硬编封顶 <b>4096×4096</b>。</summary>
    public const string H264 = "h264";

    /// <summary>HEVC / H.265。硬编封顶 <b>8192×8192</b>，是<b>超 4K 的唯一硬编路径</b>。</summary>
    public const string Hevc = "hevc";

    /// <summary>
    /// MJPEG（每帧一张独立 JPEG）—— <b>降级路径，不是临时脚手架</b>（M7.11 更正）。
    ///
    /// <para>
    /// 🔥 <b>它曾经被写成"M5.1 的联调脚手架、要等 M5.4 的硬编"</b>，那句话在当时成立，
    /// 但 M5.4 之后已经不成立：硬编 H.264/HEVC 是正常路径，而本格式有<b>两处不可替代</b> ——
    /// ① <b>多屏拼接</b>：DDA 一次只对一个 output 建 duplication，拼接只能 GDI + CPU 软编；
    /// ② <b>没有硬编的老机器</b>。把它说成脚手架，会让运维以为"这个功能还没做完"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 代价是真的：NVENC / QSV 都不编 JPEG，4K 级单帧软编 40–80 ms
    /// （M7.4 实测：硬编 57 fps，本格式 11–12 fps）。所以它是<b>兜底</b>，
    /// 不是"调一调就能到 60"的那条路。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它<b>刻意不出现在 <c>VideoCodec</c> 枚举</b>里 —— 那个枚举是"硬件编码器真能编"的格式，
    /// 是能力探测的结论类型。把这个塞进去，会让"这台机器支持 H.264"与
    /// "这台机器只能软编"变成同一类事实，而它们的分量完全不同。
    /// </para>
    /// </summary>
    public const string Mjpeg = "mjpeg";
}

/// <summary>
/// <b>参数集放在哪</b> —— 也就是码流用哪种封装（M5.4-b）。
///
/// <para>
/// 🔥 <b>它不是"多传几十个字节"的选择，而是决定码流怎么被解读</b>
/// （W3C 的 AVC / HEVC codec 注册里写死的）：
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="InBand"/></term>
///     <description>参数集<b>留在码流里</b>（Annex-B，起始码分隔）。帧头不带
///     <c>Description</c>，codec 串用 <c>avc3</c> / <c>hev1</c>。
///     <b>热路径零转换</b>（原样转发）。代价：依赖编码器把参数集重复进每个关键帧
///     —— 本项目已在真机实测成立（MF 每个关键帧都带 SPS+PPS）。</description>
///   </item>
///   <item>
///     <term><see cref="OutOfBand"/></term>
///     <description>参数集<b>随帧头</b>（<c>avcC</c> / <c>hvcC</c>）。码流<b>必须</b>
///     换成<b>长度前缀</b>形式，codec 串用 <c>avc1</c> / <c>hvc1</c>。
///     不依赖编码器的行为，但每帧要做一次 NAL 重写。</description>
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>两个名字都要在中心与客户机各出现一次</b>（中心下发、客户机认），
/// 而两个程序集之间没有引用关系 —— 所以它和 <c>RemoteCodecs</c> /
/// <c>StreamCodecs</c> 一样，<b>必须由用例钉住两端相等</b>。
/// 名字漂掉的表现是"中心说已下发、被控端不认得"，而没有任何编译错误。
/// </para>
/// </summary>
public static class RemoteStreamPackagings
{
    /// <summary>参数集在码流里（Annex-B）。<b>本批的默认</b>。</summary>
    public const string InBand = "inband";

    /// <summary>参数集随帧头（<c>avcC</c>/<c>hvcC</c>），码流长度前缀。</summary>
    public const string OutOfBand = "outofband";

    /// <summary>这个值是不是我认得的一个打包方式。</summary>
    /// <remarks>
    /// 🔥 <b>它是给"要不要说一句话"用的，不是给"要不要回落"用的</b>：
    /// 回落本身必须发生（不认得的值不能拿去当命令用），但<b>回落必须留下来过的痕迹</b> ——
    /// 否则"中心下发 <c>out-of-band</c>（写成连字符）"就会安静地按带内跑，
    /// 表现是前台解码器拿着 avc1 的串去解 Annex-B 码流，两边日志都正常。
    /// 差别在<b>谁</b>记录下来：归一化只管取值，留痕由调用方在有自己的日志出口时做。
    /// </remarks>
    public static bool IsKnown(string? value) =>
        string.Equals(value, InBand, StringComparison.Ordinal)
        || string.Equals(value, OutOfBand, StringComparison.Ordinal);

    /// <summary>
    /// 取一个<b>能当命令用</b>的值：认得就原样，认不得（含 <c>null</c>/空）就回落
    /// <see cref="InBand"/>。
    /// <para>
    /// ⚠️ 回落方向选带内，理由是<b>失败方式更干净</b>：带内那条路对码流一个字节都不改，
    /// 最坏是"浏览器解不了"——那在第 8 步会由 <c>isConfigSupported</c> 直接给一个布尔值；
    /// 而带外那条路要同时改帧头与码流，回落错方向会得到"半个 avcC"这种自相矛盾的东西。
    /// </para>
    /// </summary>
    public static string Normalize(string? value) => IsKnown(value) ? value! : InBand;
}

/// <summary>
/// <b>观看端（浏览器）的编码能力</b> —— 由页面在建会话时声明（M5.4-b 第 7 步）。
///
/// <para>
/// 🔥 <b>为什么必须由浏览器声明，而不是中心猜</b>：能解什么格式是
/// <c>VideoDecoder.isConfigSupported</c> 说了算，而那个 API 只存在于<b>观看端</b>的浏览器里。
/// 中心能看到的只有"被控端硬编支持什么"，那回答的是另一半问题。
/// </para>
///
/// <para>
/// ⚠️ <b>刻意只表达"中心真会用到的那几个位"</b>，不做成"浏览器能力清单"：
/// 后者会变成一份要跟着浏览器升级的表，而表一旦过期，
/// 症状是"中心按表挑了一个这台浏览器其实解不了的格式" —— 比不传更坏。
/// </para>
/// </summary>
/// <param name="Codecs">
/// 这台浏览器<b>能解码</b>的格式（取值见 <see cref="RemoteCodecs"/>）。
/// <para>
/// 🔥 <b><c>null</c> 与空表是两句不同的话，不许合并</b>：
/// </para>
/// <list type="bullet">
///   <item><c>null</c> / 空表 —— <b>没声明</b>（老页面、探测抛异常、或者压根没探测）。
///         中心按<b>带内 H.264</b> 回落并留痕（决策见 §3.10 (d) 问 2）。</item>
///   <item><b>显式空表</b>（<c>[]</c>）—— 浏览器<b>明确说"我一种都解不了"</b>。
///         ⚠️ 但这在 JSON 里与 <c>null</c> 长得几乎一样，所以本类型**不靠"空"来区分**：
///         要看 <see cref="Declared"/>。
///   </item>
/// </list>
/// </param>
/// <param name="Declared">
/// 🔥 <b>页面到底做没做过探测</b> —— 它是"<c>null</c> 与 <c>[]</c>"这件事的唯一判据。
///
/// <para>
/// 为什么必须显式带这样一个位，而不是靠 <see cref="Codecs"/> 的 <c>null</c>/空：
/// 两者在反序列化后是**同一个值**（<c>null</c> 的 <c>List</c> 与长度为 0 的 <c>List</c>
/// 在业务代码里几乎总被写成同一句判断），而它们的处置<b>相反</b>：
/// "没声明"要回落、明确说"一种都不支持"要落到 MJPEG。这与本仓
/// <c>Probed</c>（"这台机器报没报过能力"）是同一条纪律：
/// <b>"没测出来" ≠ "不支持"</b>。
/// </para>
/// </param>
/// <param name="Packaging">
/// 页面想要的<b>封装</b>（<see cref="RemoteStreamPackagings"/>）：<c>inband</c> / <c>outofband</c>。
///
/// <para>
/// 🔥 <b>由浏览器定，不由中心定</b>：<c>codec</c> 串用哪个前缀
/// （<c>avc1</c> 还是 <c>avc3</c>）、以及 <c>description</c> 到底要不要，
/// 是<b>这个浏览器的解码器</b>说了算，而它只存在于观看端。
/// 中心在这里只做透传 + 认不得时留痕（<see cref="RemoteStreamPackagings.Normalize"/>）。
/// </para>
///
/// <para>⚠️ <c>null</c> / 认不得的值 ⇒ 带内（与第 6 步的默认一致）。</para>
/// </param>
public sealed record RemoteViewerVideoCapabilities(
    IReadOnlyList<string>? Codecs = null,
    bool Declared = false,
    string? Packaging = null);

/// <summary>
/// <b>编码格式协商</b>（M5.4-b 第 7 步）—— 中心侧与客户机侧<b>共用同一份规则</b>。
///
/// <para>
/// 🔥 <b>为什么做成纯函数放在 Contracts 里</b>：中心与客户机是两个程序集、没有引用关系，
/// 而这条规则的两端都要用（中心算"偏好 + 可接受集合"，客户机在挑屏后按同一份集合裁决）。
/// 各写一份必然漂，而漂掉的表现是"中心算出来的集合里有一个被控端不认得的格式"，
/// 没有任何编译错误，只在现场变成一次"编码器建不起来"。
/// </para>
///
/// <para>
/// 🔴 <b>本规则里最容易被写错的一条事实：交集其实有<b>三项</b>输入，而第三项只有被控端知道。</b>
/// </para>
/// <list type="number">
///   <item>被控端硬编支持哪些格式（<c>GET api/remote/capabilities/{agentId}</c>）；</item>
///   <item>观看端能解哪些格式（<see cref="RemoteViewerVideoCapabilities"/>）；</item>
///   <item><b>本次会话的分辨率</b> —— H.264 硬编封顶 4096、HEVC 封顶 8192，
///         而分辨率<b>只有被控端在挑屏之后才知道</b>（同一台机器接不同的屏，答案就不同）。</item>
/// </list>
/// <para>
/// ⇒ 所以中心的产物**不是"一个格式"，而是"一个有序的可接受集合"**
/// （第 0 项 = 偏好），由被控端在挑屏之后按同一顺序裁决。
/// 反例最能说明为什么：浏览器只支持 h264、机器 h264+hevc、屏 5120×2880 ——
/// 中心算出的"h264"<b>恰好不可行</b>，而被排除的 hevc <b>恰好是唯一出路</b>。
/// </para>
/// </summary>
public static class RemoteVideoNegotiation
{
    /// <summary>
    /// 中心的<b>偏好顺序</b>：<c>H.264 &gt; HEVC &gt; MJPEG</c>。
    ///
    /// <para>
    /// H.264 优先的理由是<b>解码器支持面</b>而不是画质：H.264 在浏览器里几乎无版本差异，
    /// 而 HEVC 的 <c>VideoDecoder</c> 支持在 2026 年仍是"看浏览器看平台"。
    /// ⇒ 能用 H.264 就不赌 HEVC。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b><c>MJPEG</c> 排最后，但它是<b>合法的集合成员</b></b> —— 这一条是本步最容易被漏掉的：
    /// 第 8 步之前，页面<b>只会渲染 MJPEG</b>（<c>new Image()</c>，见 <c>remotePlayback.ts</c>），
    /// 而"浏览器的 <c>isConfigSupported</c> 为真"<b>不等于</b>"我们这个页面会去解它"。
    /// 两者是两件事：前者是浏览器的能力，后者是本页渲染器的能力。
    /// ⇒ 观看端声明的是<b>"这一路用哪些格式，观看端能显示出来"</b>，
    ///   所以第 8 步之前的页面如实声明 <c>["mjpeg"]</c>，画面照旧，一步都不会退。
    /// </para>
    /// </summary>
    private static readonly string[] Preference =
        { RemoteCodecs.H264, RemoteCodecs.Hevc, RemoteCodecs.Mjpeg };

    /// <summary>
    /// 观看端<b>没声明</b>能力时用的集合 —— <b>只有带内 H.264</b>（§3.10 (d) 问 2）。
    ///
    /// <para>
    /// 🔥 <b>刻意不带 HEVC</b>：带上它就意味着允许被控端把格式"升"到 HEVC，
    /// 而这个观看端<b>从来没说过它能解 HEVC</b> ⇒ 一次探测失败会换来一块黑屏。
    /// 反过来，"只给 H.264"在超 4096 的屏上会<b>如实失败</b>
    /// （尺寸闸门给出的理由很准："H.264 上限 4096，超过 4K 只能用 HEVC"），
    /// 而一句准确的失败比一块黑屏好得多。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它也刻意不带 MJPEG</b>：MJPEG 在 1440p 是软编 12–25 fps，
    /// 把它塞进回落集合等于"一次探测失败就永久降级"。
    /// （MJPEG 仍在被控端的最后一道回退里 —— 那是"硬编建不起来"时的事，与这里无关。）
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>一个已知的过渡期风险，必须写在这里</b>：如果那台机器上的页面是
    /// <b>第 8 步之前的旧版本</b>（它只渲染 MJPEG），本回落会让它<b>黑屏</b>。
    /// ⇒ 中心在走这条回落时会打一条警告，告诉现场"强制刷新一次页面"。
    /// 第 8 步交付之后这条风险自动消失（那时页面一定会声明）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FallbackCodecs { get; } =
        new[] { RemoteCodecs.H264 };

    /// <summary>这个格式名<b>要不要经过被控端的硬编能力过滤</b>。</summary>
    /// <remarks>
    /// 🔥 <c>mjpeg</c> <b>必须返回 <c>false</c></b>：被控端报的 <c>SupportedCodecs</c> 是
    /// <b>硬件编码器</b>的枚举结果，而 MJPEG 走的是 GDI+ 软编 —— 它**永远可用**。
    /// 拿硬编能力去过滤它，会把"这台机器没有硬编"错译成"连 MJPEG 都不能用"，
    /// 而那正好是 MJPEG 唯一该被用上的场合。
    /// </remarks>
    public static bool NeedsHardwareEncoder(string? name) =>
        name is not null
        && (string.Equals(name, RemoteCodecs.H264, StringComparison.Ordinal)
            || string.Equals(name, RemoteCodecs.Hevc, StringComparison.Ordinal));

    /// <summary>
    /// 把一串格式名<b>归一化</b>：丢掉不认得的、去重、小写化、保持顺序。
    /// <para>
    /// ⚠️ 不认得的名字<b>不能被当成"不支持的格式"</b> —— 那是一个凭空的否定结论
    /// （可能是新版本报的新格式）。这里只把它剔出集合，由调用方决定说不说一句话。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? codecs)
    {
        if (codecs is null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();

        foreach (var raw in codecs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var name = raw.Trim().ToLowerInvariant();

            // 放行三种：两种需要硬编（h264/hevc），一种永远可用（mjpeg，GDI 软编）。
            if (name != RemoteCodecs.H264
                && name != RemoteCodecs.Hevc
                && name != RemoteCodecs.Mjpeg)
            {
                continue;
            }

            if (seen.Add(name)) list.Add(name);
        }

        return list;
    }

    /// <summary>按<b>偏好顺序</b>排列（H.264 在前），保持集合成员不变。</summary>
    public static IReadOnlyList<string> ByPreference(IEnumerable<string>? codecs)
    {
        var set = new HashSet<string>(Normalize(codecs), StringComparer.Ordinal);

        return Preference.Where(set.Contains).ToArray();
    }

    /// <summary>
    /// 中心侧的裁决：<b>「偏好 ＋ 可接受集合」</b>。
    /// </summary>
    /// <param name="agentSupported">
    /// 被控端上报的硬编格式（<c>null</c> = <b>它还没报过</b>，别当成"它一种都不支持"）。
    /// </param>
    /// <param name="viewer">观看端的声明（<c>null</c> = 老页面，没这个字段）。</param>
    /// <param name="packaging">归一化后的封装（认不得的值会回落带内）。</param>
    /// <param name="note">给人看的一句话，<b>必须进日志</b>（说清"凭什么这么定"）。</param>
    /// <param name="failure">非 <c>null</c> = <b>明确拒绝</b>，值就是给人看的原因。</param>
    /// <remarks>
    /// 🔥 <b>只有一种情况会拒绝</b>：两边都<b>清楚地</b>报过能力，而且交集为空。
    /// 其余一切"不知道"都<b>回落</b>（<see cref="FallbackCodecs"/>）——
    /// "没测出来" ≠ "不支持"，把它翻译成拒绝等于替现场做了一个它没做过的判断。
    /// </remarks>
    public static IReadOnlyList<string> Decide(
        IReadOnlyList<string>? agentSupported,
        RemoteViewerVideoCapabilities? viewer,
        out string packaging,
        out string note,
        out string? failure)
    {
        packaging = RemoteStreamPackagings.Normalize(viewer?.Packaging);
        failure = null;

        var agent = agentSupported is null ? null : ByPreference(agentSupported);

        // 🔥 集合非空**本身就等于**"声明过" —— `Declared` 只用来消解 `null` 与 `[]` 的歧义。
        //    所以这里不看 `Declared` 也能认出"页面给了两个格式名"这种情况
        //    （页面忘了置位也不会被误判成"没探测"）。
        var declared = ByPreference(viewer?.Codecs);

        // ── 观看端明确说"我一种都显示不了" ⇒ 这条路本来就走不了，如实拒绝 ──
        //
        // ⚠️ 放在最前面：这条与"没声明"的处置**相反**，而它们在代码里长得几乎一样。
        //    判据只能是有没有 Declared（见 RemoteViewerVideoCapabilities 的注释）。
        //
        // 🔥 "一种都显示不了"其实**不可能是真的**：MJPEG 任何浏览器都显示得了
        //    （它只需要 <img>，不需要 WebCodecs）。所以走到这里意味着
        //    **页面把声明写错了**，而这句话必须这么说出来 ——
        //    否则现场会去查浏览器版本，而真因在页面的那段声明代码里。
        if (declared.Count == 0 && viewer is { Declared: true })
        {
            failure = "观看端声明它一种格式都显示不了 —— 而 MJPEG 是不需要 WebCodecs 的"
                + "（任何浏览器用 <img> 就能显示），所以这份声明一定写错了："
                + "`viewerVideo.declared` 置了真、`codecs` 却是空表。"
                + "请检查页面里那一段能力声明；暂时不发 `viewerVideo` 也会走回落而不是拒绝。";

            note = "观看端声明零能力（不可能成立）⇒ 拒绝，并直指『声明写错了』。";

            return Array.Empty<string>();
        }

        // ── 观看端没声明 ⇒ 回落，并留痕 ──
        if (declared.Count == 0)
        {
            IReadOnlyList<string> fallback;

            if (agent is null)
            {
                // 🔥 **`null`（没上报）走这里**：这是"不知道"，按本步的决策回落 H.264。
                fallback = FallbackCodecs;
            }
            else if (agent.Count == 0)
            {
                // 🔴 **`[]`（明确无硬编）与上面那一种是两回事** —— 这是本方法最容易写错的一处，
                //    而混掉它的后果很具体：一台**一台硬编都没有**的机器上，
                //    "H.264 回落"必然在建编码器那一步就失败 ⇒ 永远建不起会话，
                //    而它本来用 MJPEG 能跑。
                //    ⇒ 这正是"**MJPEG 退回它本来的位置**：被控端连硬编都没有时的终点"
                //      （见 FallbackCodecs 的注释：那里说的是"不该因为*探测失败*而降到 MJPEG"，
                //       而这里我们已经**确知**没有硬编，不是探测失败）。
                fallback = new[] { RemoteCodecs.Mjpeg };
            }
            else if (agent.Contains(RemoteCodecs.H264))
            {
                // 🔥 被控端有 H.264 就**只给 H.264** —— 哪怕它还有 HEVC。
                //    观看端没声明 ⇒ 我们不知道它认不认 HEVC ⇒ 不许给被控端"升到 HEVC"的机会。
                fallback = new[] { RemoteCodecs.H264 };
            }
            else
            {
                // 机器只有 HEVC 硬编（罕见）：这时没有任何"更保守"的选择，
                // 只能把它的能力集原样交出去，并在 note 里说清这一点。
                fallback = agent;
            }

            note = (viewer is null
                ? "观看端没带能力字段（老页面 / 缓存里的旧 bundle？）"
                : "观看端声明了但探测没做成（没跑在安全上下文里？）")
                + (agent is null
                    ? "⇒ 按带内 H.264 回落"
                    : agent.Count == 0
                        ? "⇒ 这台机器**明确没有硬编**（能力表报了空），按 MJPEG 回落"
                        : "⇒ 按带内 H.264 回落")
                + $"，集合 = {string.Join(" > ", fallback)}。"
                + (agent is { Count: 0 }
                    ? ""
                    : "⚠️ 若那台机器上跑的是第 8 步之前的旧页面（它只会渲染 MJPEG），"
                      + "这一路会黑屏 —— 让用户强制刷新一次页面即可。");

            return fallback;
        }

        // ── 被控端**还没上报**能力 ⇒ 用观看端声明的集合，让被控端自己裁决 ──
        //
        // 不能在这里拒绝：`SupportedCodecs` 为 **null** 的含义是"还没报过"，
        // 而"还没报过"与"报了但一台硬编都没有"（空表）是两件事
        // （AgentEncoderCapabilityService 的注释）。⚠️ 所以这里只判 null，**不判空表** ——
        // 空表要走下面的交集，它是有信息量的（"这台机器明确没有硬编"）。
        if (agent is null)
        {
            note = "被控端尚未上报编码能力（它会在重连后立刻报一次）⇒ "
                + $"按观看端声明的集合下发，由被控端在挑屏后裁决：{string.Join(" > ", declared)}。";

            return declared;
        }

        // 🔥 交集要分两半算，这是本方法最容易写错的一处：
        //    · 需要硬编的那两种（h264 / hevc）**必须**过被控端能力这道筛；
        //    · mjpeg **不许**过筛 —— 它走的是 GDI+ 软编，永远可用。
        //    拿硬编能力去筛 mjpeg，会把"这台机器没有硬编"错译成"连 MJPEG 都不能用"，
        //    而那正好是 MJPEG 唯一该被用上的场合。
        var intersection = ByPreference(
            declared.Where(c => !NeedsHardwareEncoder(c) || agent.Contains(c)));

        if (intersection.Count == 0)
        {
            failure = $"观看端能显示的格式（{string.Join(" / ", declared)}）与这台机器真正能硬编的格式"
                + $"（{string.Join(" / ", agent)}）没有交集，而观看端也没有声明 MJPEG（它永远可用）—— "
                + "这一路建不起来。";

            note = "两边能力都清楚，交集为空 ⇒ 明确拒绝。";

            return Array.Empty<string>();
        }

        note = $"取交集：观看端 {string.Join(" / ", declared)} ∩ 被控端 {string.Join(" / ", agent)}"
            + $" ⇒ {string.Join(" > ", intersection)}"
            + "（偏好顺序；实际操作由被控端在挑屏后裁决 —— 分辨率只有它知道）。";

        return intersection;
    }
}

/// <summary>观看端的显示模式（M5 §10.1）。</summary>
public static class RemoteDisplayModes
{
    /// <summary>整帧等比缩放进视口（默认）。</summary>
    public const string Fit = "fit";

    /// <summary>按原始像素显示，超出部分出上下左右滚动条。</summary>
    public const string Actual = "actual";
}

/// <summary>
/// 一个显示适配器（能力探测结果里的一项）。
/// <para>
/// ⚠️ <see cref="EncodeEngines"/> 的 <c>null</c> 与 <c>0</c> 含义不同（见
/// <c>EncoderCapabilities</c> 的说明）：<b>null = 尚未测出，0 = 明确无硬编</b>。
/// </para>
/// </summary>
/// <param name="SupportedCodecs">
/// 这块适配器<b>确实能硬件编码</b>的格式（取自 Media Foundation 的硬件编码器枚举，
/// 取值见 <see cref="RemoteCodecs"/>）。空表 = 没发现硬编能力。
/// </param>
/// <param name="EngineBasis">这个引擎数是怎么来的。<b>报数字就必须带依据</b>。</param>
public sealed record RemoteAdapterDto(
    string Name,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemoryBytes,
    bool IsSoftwareRenderer,
    IReadOnlyList<string> SupportedCodecs,
    int? EncodeEngines,
    string? EngineBasis);

/// <summary>
/// 某台被控机的编码能力。
/// </summary>
/// <param name="Probed">
/// 🔥 <b>这台机器到底报没报过能力</b>。
/// <para>
/// 它与"这台机器没有编码能力"是两件事：<c>Probed = false</c> 表示<b>尚未上报</b>
/// （能力探测的上报链路在 M5.0 第二批接入）。若不区分这两者，
/// 页面会在"还没上报"时显示"不支持远控"—— 一个<b>凭空的否定结论</b>。
/// </para>
/// </param>
public sealed record RemoteCapabilityDto(
    bool Probed,
    IReadOnlyList<RemoteAdapterDto> Adapters,
    IReadOnlyList<string> SupportedCodecs,
    string? RecommendedCodec,
    string Summary,
    DateTimeOffset? ProbedAt);

/// <summary>
/// 客户机<b>上报给中心</b>的编码能力（M5.0 第二批的上行载荷）。
///
/// <para>
/// 🔥 <b>为什么它必须从客户机来，而不是中心自己探</b>：真正要编码的是<b>被控端</b>。
/// 中心那台机器上有没有 NVENC 与远控能不能跑毫无关系 ——
/// 一个装在服务器上的探测只能回答错误的问题。
/// </para>
///
/// <para>
/// ⚠️ <b>刻意不带 <c>Probed</c></b>：那是<b>中心的表述方式</b>（"这台机器报过没有"）,
/// 由中心在 <see cref="RemoteCapabilityDto"/> 上补出来。
/// 让客户机自己声称"我报过了"没有意义 —— 判断依据是"中心收没收到"。
/// </para>
/// </summary>
/// <param name="SupportedCodecs">全机范围内<b>确实有硬编</b>的格式（见 <see cref="RemoteCodecs"/>）。</param>
/// <param name="Summary">给人看的一句话结论（直接进页面与日志）。</param>
public sealed record AgentEncoderCapabilityDto(
    IReadOnlyList<RemoteAdapterDto> Adapters,
    IReadOnlyList<string> SupportedCodecs,
    string? RecommendedCodec,
    string Summary,
    DateTimeOffset ProbedAt);

/// <summary>一条远程桌面会话。</summary>
/// <param name="Codec">
/// 这路流实际用的编码格式（取值见 <see cref="RemoteCodecs"/>）。
/// <c>null</c> = 还没开始推流，所以<b>还谈不上用什么编</b>。
/// </param>
/// <param name="Stats">
/// <b>中心侧</b>的实时计数。<c>null</c> = 还没有任何一帧到过中心。
/// <para>
/// 🔥 它<b>不是"锦上添花的指标"</b>：M5.1 的验收手段就是它 ——
/// 页面显示"正在推流"而 <c>FramesReceived</c> 一直是 0，说明链路某一跳断了，
/// 而不是"画面在加载"。没有它，"没反应"只能靠猜。
/// </para>
/// </param>
/// <param name="AgentStats">
/// <b>被控端自己报</b>的发送计数。<c>null</c> = 它还没报过（会话刚建、或状态上报没到）。
/// <para>
/// 🔥 <b>它与 <see cref="Stats"/> 必须分开、也必须都有</b>：
/// 两个数摆在一起才是这条链路上最有用的一句诊断 ——
/// </para>
/// <list type="bullet">
///   <item><c>它发 500 / 我们收 12</c> ⇒ <b>链路上在掉</b>（Hub / 网络 / 中心侧丢帧）；</item>
///   <item><c>它发 3 / 我们收 3</c> ⇒ 链路是好的，<b>是被控端编不动</b>（CPU / 编码器 / 抓屏慢）。</item>
/// </list>
/// <para>
/// 只有其中一个数的时候，这两种故障长得<b>一模一样</b>（都表现为"画面很卡"），
/// 而它们该做的处置完全相反：前者去查网络，后者去查被控机的负载。
/// </para>
/// </param>
/// <param name="InputEnabled">
/// <b>这一路会话是否允许回传输入（M5.2）</b>。默认 <c>false</c>。
///
/// <para>
/// 🔥 <b>"能看"与"能控"必须分开，而且默认必须是"只能看"</b>（决策 11 只解决了
/// "谁能在页面上看到画面"，没有解决"谁能让被控机的鼠标动起来"）。
/// 两者被合成一个权限的后果是：管理员为了让同事"看一眼那台机器的状态"而开了一路观看，
/// 对方手里却同时拿到了那台机器的键盘 —— 而这个差别<b>在页面上看不出来</b>。
/// </para>
/// <para>
/// ⚠️ 它是<b>会话级</b>的（与 <see cref="Grayscale"/> 同一条理由）：注入作用在一台机器的
/// 桌面上，不是某个观看者的私有开关。所以一个人打开它，<b>所有</b>看这路流的人都能控制 ——
/// 页面必须显式提示"现在是谁、有多少人在控"，否则现场会出现两个人同时抢鼠标而互不知情。
/// </para>
/// </param>
/// <param name="InputState">
/// <b>被控端的输入通道状态</b>（M5.2），取值见 <see cref="AgentInputStates"/>。
/// <c>null</c> = 这台机器的客户端<b>压根没提过这个字段</b>（旧版）。
/// </param>
/// <param name="InputReason">
/// <see cref="InputState"/> 为 <c>unavailable</c> 时<b>为什么</b>（人话，可直接展示）。
///
/// <para>
/// 🔥 <b>它必须单独有一个字段，不能指望 <see cref="EndedReason"/></b>：
/// 输入通道接不上是<b>会话还活着</b>的时候发生的（画面照推、只是点不动），
/// 而 <see cref="EndedReason"/> 只在"停止 / 失败"那条分支上才被赋值 ——
/// 于是页面只能显示"输入不可用"这五个字，而<b>真正该看的那句话</b>
/// （"客户端版本过旧" / "这台机器本机禁止了远程输入"）就丢了。
/// 那句话恰恰是唯一能告诉人"下一步该做什么"的东西。
/// </para>
///
/// <para>⚠️ 只在 <c>unavailable</c> 时有值：就绪之后必须清掉，
/// 否则页面上会出现"通道就绪：本机已禁止远程输入"这种自相矛盾的一句话。</para>
/// </param>
public sealed record RemoteSessionDto(
    string SessionId,
    int AgentId,
    string State,
    string DisplayMode,
    bool Grayscale,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? EndedReason,
    string? ScreenId = null,
    string? Codec = null,
    RemoteSessionStatsDto? Stats = null,
    RemoteSessionAgentStatsDto? AgentStats = null,
    bool InputEnabled = false,
    string? InputState = null,
    string? InputReason = null,

    /// <summary>
    /// <b>被控端的音频通道处于什么状态</b>（M5.3）。取值见 <see cref="AgentAudioStates"/>。
    /// <para>
    /// 🔥 <c>null</c> = <b>这台机器压根没提过音频</b>（M5.3 之前的客户端）——
    /// 与 <c>"unavailable"</c>（新版明确说接不上）是两件事，页面不许把 <c>null</c> 画成"不可用"。
    /// </para>
    /// </summary>
    string? AudioState = null,

    /// <summary>音频不可用的原因（仅 <c>unavailable</c> 时有值）。例如"本机没有音频输出设备"。</summary>
    string? AudioReason = null,

    /// <summary>
    /// 被控端对<b>这一路用什么格式编</b>的裁决说明（M5.4-b 第 7 步）。
    /// <c>null</c> = 没有额外说明（照中心的首选做了，或者是旧客户端不认识这个字段）。
    ///
    /// <para>
    /// 🔥 <b>它与 <see cref="Codec"/> 是两件事，不许合并</b>：
    /// <see cref="Codec"/> 是<b>从首帧头里读到的事实</b>（"实际用的是 h264"），
    /// 本字段是<b>被控端给出的理由</b>（"中心首选 h264、本机改判为 hevc，因为那块屏 5120 宽"）。
    /// <b>一个是"是什么"，一个是"为什么"</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 为什么理由必须由被控端给：中心的协商产物是"偏好 ＋ 可接受集合"，
    /// 而最终裁决在被控端（交集还有第三项输入 —— 分辨率 —— 只有它挑屏之后才知道）。
    /// 没有这句话，现场看到的是"中心写着 h264、码流却是 hevc"，
    /// 而"哪一侧做的决定"完全无法判断。
    /// </para>
    /// </summary>
    string? CodecNote = null,

    /// <summary>
    /// <b>被控端的桌面已经静止了多久</b>（毫秒；M5.4-c② 步 7）。
    ///
    /// <para>
    /// 🔥 <b>它是"这画面为什么不动"的唯一答案</b>：一帧都不来时，现场只有三种可能 ——
    /// <b>桌面真的没动</b>（展厅里定格在一张图上，正常）、<b>被控端编不出来</b>、
    /// <b>链路把帧丢了</b>。三者在前端看起来一模一样（一张不动的旧图），
    /// 而处置完全不同。只有被控端的<b>抓屏器</b>知道是哪一种。
    /// </para>
    ///
    /// <para>
    /// 🔥🔥 <b>为什么它挂在会话上、而不是放进 <see cref="RemoteSessionAgentStatsDto"/></b>：
    /// 那份统计有一条"一帧都没发过就是 <c>null</c>"的判据 ——
    /// 而"从开流第一秒桌面就没动过"的机器<b>恰恰一帧都发不出去</b>。
    /// 挂进去的话，最该被解释的那一台在页面上反而什么都没有。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>三态</b>：<c>null</c> = 这台机器不提这件事（旧客户端 / 还没报过）；
    /// <c>0</c> = 刚动过；<c>12000</c> = 已静止 12 秒。
    /// <b>不许把 <c>null</c> 画成 <c>0</c></b>。
    /// </para>
    /// </summary>
    long? DesktopIdleMs = null,

    /// <summary>
    /// <b>这一路的质量档位</b>（M5.5 运行时自适应；取值见 <see cref="RemoteQualityProfiles"/>）。
    ///
    /// <para>
    /// 🔥 <b>它是"策略名"，不是实测值</b>：档位决定目标码率与帧率，
    /// 实际跑成什么样看 <see cref="Stats"/> 里的帧率与字节数。
    /// 把档位当成绩上报，会让"降到低档之后仍然卡"这件事看起来像是好了。
    /// </para>
    /// </summary>
    string Quality = RemoteQualityProfiles.Auto,

    /// <summary>
    /// <b>这一路当前的目标帧率</b>（档位与请求里的 <c>MaxFps</c> 取小之后的那个数）。
    /// <c>null</c> = 还没定（会话刚建，或旧客户端）。
    /// </summary>
    int? TargetFps = null,

    /// <summary>这一路当前的目标码率（kbps）。<c>null</c> = 还没定。</summary>
    int? TargetBitrateKbps = null,

    /// <summary>
    /// <b>自适应降过档没有</b>（M5.5）。
    ///
    /// <para>
    /// 🔥 <b>为什么它是一个独立的布尔值</b>：页面上"档位 = 自动"与
    /// "自动已经把你降到低了"是两个完全不同的状态 —— 前者说明"系统在管"，
    /// 后者说明"<b>系统已经在救场了</b>"，而用户该做的是去查那台机器为什么编不动。
    /// 只显示档位名时，这两种情况长得一模一样。
    /// </para>
    /// </summary>
    bool QualityDowngraded = false,

    /// <summary>降档原因（人话，仅在 <see cref="QualityDowngraded"/> 为真时有值）。</summary>
    string? QualityNote = null,

    /// <summary>
    /// <b>当前只编画面里的哪一块</b>（M5.5 第 5 项）—— 整幅画面时四个值都是 <c>0</c>。
    ///
    /// <para>
    /// 🔥 <b>它必须能被读回</b>：页面要知道"现在看的是整幅还是视口"，
    /// 否则用户滚了半天以为在看整幅、其实被控端只编了 1/16 ——
    /// 那种状态下<b>视口以外的区域是黑的</b>，而页面会把它当成"解码失败"。
    /// </para>
    /// </summary>
    int ViewportX = 0,

    /// <summary>视口左上角 Y（0 = 没有视口）。</summary>
    int ViewportY = 0,

    /// <summary>视口宽（0 = 没有视口，即整幅画面）。</summary>
    int ViewportWidth = 0,

    /// <summary>视口高（0 = 没有视口）。</summary>
    int ViewportHeight = 0,

    /// <summary>
    /// <b>这一路现在切成几块在编</b>（M5.5 第 4 项：分块编码）。
    /// <c>1</c> = 没切。
    /// <para>
    /// ⚠️ 页面靠它决定"要不要按块拼接"；中心靠它判断"8K 是不是真的用了双引擎"。
    /// 只写"分辨率 11520×2160"而看不出切了几块，就没法解释帧率。
    /// </para>
    /// </summary>
    int TileCount = 1);

/// <summary>
/// 一路流的实时计数（M5.1 的验收依据，也是页面上的帧率/码率）。
/// </summary>
/// <param name="FramesReceived">中心收到的帧数（<b>含被丢掉的旧帧</b>，即"到过多少"）。</param>
/// <param name="BytesReceived">中心收到的<b>码流</b>字节数（不含编码/传输开销）。</param>
/// <param name="Fps">最近一段时间的实测帧率（<b>实测</b>，不是目标值）。</param>
/// <param name="LastFrameBytes">最后一帧的字节数（判断"这个分辨率在这个质量下编出来多大"）。</param>
/// <param name="LastFrameAt">最后一帧的到达时刻。</param>
/// <param name="DroppedFrames">
/// 被<b>丢弃</b>的帧数（编码超时 / 超出单条消息上限 / 队列里过期）。
/// <para>
/// ⚠️ 它与 <see cref="FramesReceived"/> 必须分开看：丢帧多而收到少 ⇒ 该降质量或降帧率；
/// 两者都少 ⇒ 链路本身有问题。合成一个"帧率"会把这两种故障混成一种。
/// </para>
/// <para>
/// 🔴 <b>它是<b>被控端自己</b>丢的帧</b>（从帧头一路带过来的那个累计值）。
/// 中心自己在<b>分片重组</b>时丢的帧另有一个计数（见 <see cref="ReassemblyDroppedFrames"/>），
/// <b>两者绝不合并</b>："它编不动"（去查被控机）与"链路上把片丢了"（去查网络）
/// 合并之后就再也分不开了，而那正是这两个数并排存在的全部意义。
/// </para>
/// </param>
/// <param name="ReassemblyDroppedFrames">
/// <b>中心在分片重组时丢掉的帧数</b>（M5.4-c①；缺片 / 片非法 / 新序号打断）。
///
/// <para>
/// 🔥 <b>为什么它必须有自己的一格</b>：分片引入了一个<b>新的丢帧来源，而它在中心</b> ——
/// 被控端对此完全无感（它把片发出去了，计数一切正常）。
/// 没有这个数，"8K 那一路画面一直花"在现场只有"网络慢"一种解释，
/// 而真因可能是"某一帧的第 3 片没到"。
/// </para>
///
/// <para>⚠️ 默认 <c>0</c>：旧中心的 JSON 里没有这个成员，反序列化后就是 0 ——
/// 与"确实一次都没丢"看起来一样。判据是"这一版的中心是否上报过它"，不是这个 0 本身。</para>
/// </param>
public sealed record RemoteSessionStatsDto(
    long FramesReceived,
    long BytesReceived,
    double Fps,
    int LastFrameBytes,
    DateTimeOffset? LastFrameAt,
    long DroppedFrames = 0,

    /// <summary>
    /// <b>中心收到的音频帧数</b>（M5.3）。
    /// <para>
    /// 🔥 与 <see cref="FramesReceived"/> 分开计，理由与"两个帧计数必须并排"完全一致：
    /// <b>"画面卡了"与"声音断了"在只给一个总数的时候长得一模一样</b>，
    /// 而处置相反（一个查编码 / 一个查音频设备）。
    /// </para>
    /// <para>⚠️ 它与音频的"被控端发出数"（<see cref="RemoteSessionAgentStatsDto.AudioFramesSent"/>）
    /// 也要摆在一起看：只有一边有数才是异常。</para>
    /// </summary>
    long AudioFramesReceived = 0,

    /// <summary>中心收到的音频码流字节数（M5.3）。</summary>
    long AudioBytesReceived = 0,

    /// <summary>中心在<b>分片重组</b>时丢掉的帧数（M5.4-c①）。见类型参数说明。</summary>
    long ReassemblyDroppedFrames = 0,

    /// <summary>
    /// 中心<b>由多片拼成</b>的帧数（M5.4-c① 步 6）。
    ///
    /// <para>
    /// 🔥 <b>它与 <see cref="FramesReceived"/> 是不同的两件事，必须并排看</b>：
    /// <c>FramesReceived</c> 数的是"进来几帧"（老路 + 分片路都算），
    /// 而本参数只数"<b>其中有几帧是拼出来的</b>"。
    /// </para>
    /// <list type="bullet">
    ///   <item><c>FramesReceived &gt; 0 && ReassembledFrames == 0</c> ⇒
    ///         这一路<b>从没走过分片</b>（帧都装得下 —— 2160×1440 / 4K 的正常状态）；</item>
    ///   <item>两者都在涨 ⇒ <b>分片路真的在工作</b>；</item>
    ///   <item><c>ReassembledFrames == 0 && ReassemblyDroppedFrames &gt; 0</c> ⇒
    ///         <b>走是走了，但一帧都没拼成</b> —— 这时画面会一路花，而两个数各自都"不好看"，
    ///         合起来才指向真因。</item>
    /// </list>
    ///
    /// <para>
    /// 🔴 <b>它刻意不进观看页</b>（与 <see cref="ReassemblyDroppedFrames"/> 同一条纪律）：
    /// 分片是<b>传输层</b>的事，前端看到的是<b>已拼好的整帧</b> —— 把"重组帧数"画到页面上，
    /// 等于告诉前端"你收到的这一帧是几片拼的"，而那正是方案乙（终止在中心）
    /// 立身之处：<b>跳 4/5/6 与前端一律零改动</b>。
    /// 它只出现在中心接口（<c>/api/remote/sessions</c>）与冒烟判据里。
    /// </para>
    /// </summary>
    long ReassembledFrames = 0);

/// <summary>
/// <b>被控端报的</b>一路流的计数（与 <see cref="RemoteSessionStatsDto"/> 是两边的东西，别合并）。
///
/// <para>
/// 🔥 命名刻意避开 <c>Received</c> 这个词：<see cref="RemoteSessionStatsDto"/> 那组数的
/// 主语是<b>中心</b>（"我收到了多少"），这组数的主语是<b>被控机</b>（"我发出了多少"）。
/// 两边都叫 <c>Frames</c> 的时候，页面上出现两个"帧数"而没人分得清哪个是哪个 ——
/// 而那正是最需要分清的一处。
/// </para>
/// </summary>
/// <param name="FramesSent">被控端<b>发出</b>（送入管道）的帧数。</param>
/// <param name="BytesSent">被控端发出的码流字节数。</param>
/// <param name="Fps">被控端侧实测帧率（<b>它自己的</b>产出速度，与中心实测的那个不同）。</param>
public sealed record RemoteSessionAgentStatsDto(
    long FramesSent,
    long BytesSent,
    double Fps,

    /// <summary>被控端<b>发出</b>的音频帧数（M5.3）。</summary>
    long AudioFramesSent = 0,

    /// <summary>被控端发出的音频码流字节数（M5.3）。</summary>
    long AudioBytesSent = 0,

    /// <summary>被控端累计丢弃的音频帧数（M5.3；正常恒为 0，非 0 意味着有断点）。</summary>
    long AudioDropped = 0);

/// <summary>
/// 观看端<b>加入一路会话</b>的答复（<c>RemoteHub.Join</c> 的返回值）。
/// <para>
/// 🔥 <b>为什么不回一个空的 <c>void</c></b>："加入失败"有好几种原因，
/// 而它们的下一步完全不同（会话不存在 ⇒ 刷新列表；会话已结束 ⇒ 重新建；
/// 没权限 ⇒ 找管理员）。回 <c>void</c> 的话，前端只能靠自己再发一次
/// <c>GET /api/remote/sessions/{id}</c> 去猜，而那次查询与这次加入之间
/// 状态可能已经又变了。一次问清最省事。
/// </para>
/// </summary>
/// <param name="Ok">真 = 已加入分组，后续帧会推过来。</param>
/// <param name="ErrorCode">失败原因，取值见 <see cref="RemoteErrorCodes"/>。</param>
/// <param name="Session">加入时的会话快照（页面据此立刻把标题、分辨率、帧率画出来）。</param>
public sealed record RemoteWatchAckDto(
    bool Ok,
    string? ErrorCode,
    string? Message,
    RemoteSessionDto? Session);

/// <summary>
/// 建会话的请求。
/// </summary>
/// <param name="ScreenId">
/// 要抓哪个屏。<c>null</c> = 整个虚拟桌面（多屏拼接时就是它，也是"等效 8K"的形态）。
/// </param>
/// <param name="MaxFps">
/// 目标帧率上限。<c>null</c> = 用被控端默认值。
/// <para>
/// ⚠️ 它是<b>上限而非承诺</b>：实际帧率取决于被控端编得动多少。
/// 把"目标"当"承诺"写进页面，现场会以为"说了 30 就该有 30"。
/// </para>
/// </param>
/// <param name="ViewerVideo">
/// <b>观看端（浏览器）能解什么</b>（M5.4-b 第 7 步）。<c>null</c> = 老页面没带这个字段。
///
/// <para>
/// 🔥 <b>为什么协商的输入必须从这里来，而不是中心自己探</b>：中心能回答的是
/// "被控端硬编支持什么"，而"这个浏览器解不解得开"只有观看端知道
/// （<c>VideoDecoder.isConfigSupported</c> 只存在于那台机器的浏览器里）。
/// 两边各答一半，交集才成立。
/// </para>
///
/// <para>
/// ⚠️ <c>null</c> 与"带了但探测失败"<b>都是回落</b>（带内 H.264），
/// 只有页面<b>明确声明零能力</b>才是拒绝 —— 见 <see cref="RemoteVideoNegotiation.Decide"/>。
/// </para>
/// </param>
public sealed record RemoteSessionRequest(
    int AgentId,
    string? ScreenId = null,
    string? DisplayMode = null,
    int? MaxFps = null,
    RemoteViewerVideoCapabilities? ViewerVideo = null,

    /// <summary>
    /// 质量档位（<see cref="RemoteQualityProfiles"/>；<c>null</c> = <c>auto</c>）。
    /// <para>
    /// ⚠️ 它<b>不是</b> <paramref name="MaxFps"/> 的替代品：本字段是"码率 ＋ 推荐帧率"这一组
    /// 策略的名字，<paramref name="MaxFps"/> 是用户明确要的帧率。
    /// </para>
    /// <para>
    /// 🔥 <b>M7.16 起两者不再"取更小"</b>：请求给了帧率就以请求为准（见
    /// <see cref="RemoteQualityProfiles.FoldTargetFps"/>）。旧折法会让"帧率"这个旋钮
    /// 在档位之上整块失效 —— 选了 30 实际还是 12，而页面上没有任何一处说这件事。
    /// ⚠️ 码率<b>仍然只由档位决定</b>：60 fps 配「均衡」是可以选出来的组合（每帧更糊）。
    /// </para>
    /// </summary>
    string? Quality = null);

/// <summary>
/// 一帧码流的<b>头</b>（不含码流本身）。
///
/// <para>
/// 🔥 <b>为什么头和体要分开</b>：本地管道上码流走**原始二进制**（长度前缀 + 裸字节），
/// 头走一行 JSON。若整帧都序列化成 JSON，<c>byte[]</c> 会变成 base64
/// —— 白白多 33% 的字节和一份编解码 CPU，而管道本来就是本机的。
/// </para>
/// </summary>
/// <param name="Codec">取值见 <see cref="RemoteCodecs"/>。</param>
/// <param name="CapturedUnixMs">
/// 抓屏时刻（UTC 纪元毫秒）。
/// <para>
/// ⚠️ 它是<b>用来测延迟的时长基准，不是给人看的时间戳</b> ——
/// 所以刻意用 epoch 毫秒而不是 <c>BeijingTime</c>：观看端要算的是
/// "这一帧从抓到到画出来隔了多久"，与本地时区无关。
/// 人看的那些时间戳仍然一律走 <c>BeijingTime</c>。
/// </para>
/// </param>
/// <param name="Dropped">
/// 被控端<b>累计丢弃</b>的帧数。
/// <para>
/// 🔥 <b>它挤在每一帧的头上，是刻意的</b>：被控端"编不出来 / 这一帧太大"
/// 只发生在<b>它自己那边</b>，而中心能看到的一切（收了多少帧、多少字节）
/// 对这件事<b>完全无感</b>。不带上这个数，中心侧的"画面卡顿"就只剩一种解释
/// ——"网络慢"，而真实原因可能是被控机 CPU 被吃满了。
/// </para>
/// <para>它是<b>累计值</b>（不是增量），所以丢几帧不影响最终读到正确数字。</para>
/// </param>
/// <param name="CapturedMonoMs">
/// <b>会话单调时钟</b>上的抓屏时刻（毫秒，从这一路会话开始计）。
///
/// <para>
/// 🔥 <b>为什么除了 <paramref name="CapturedUnixMs"/> 还要有它</b>：
/// 那两个时间戳服务的是<b>两件不同的事</b>。
/// </para>
/// <list type="bullet">
///   <item><see cref="CapturedUnixMs"/> = <b>"这一帧离现在多久了"</b>（延迟），
///         需要绝对时间；</item>
///   <item>本字段 = <b>"音与画谁先谁后"</b>（AV 同步），需要的是<b>一个不被校正的、单调的</b>时间轴。</item>
/// </list>
/// <para>
/// ⚠️ <b>用墙钟做 AV 同步会出事</b>：本项目带 NTP 校时，会话中途一次时间校正
/// （哪怕只跳 5 秒）会让"音频单调时间轴"与"视频单调时间轴"错开同样的量，
/// 而症状是<b>声音忽然对不上口型，且之后一直对不上</b>，重启会话才恢复 ——
/// 排查的人根本不会想到是时钟校正在作祟。
/// </para>
/// <para>
/// <c>null</c> = <b>发这一帧的客户端还没有这个字段</b>（M5.1/M5.2 的旧客户端）。
/// 它是<b>刻意的可空</b>：用 <c>0</c> 冒充会让"旧客户端"与"会话正好在第 0 毫秒抓了一帧"
/// 变成同一件事，而这两个结论对应的下一步动作完全相反。
/// </para>
/// </param>
/// <param name="CodecString">
/// <b>浏览器怎么称呼这个编码</b>，例如 <c>avc1.640032</c> / <c>avc3.640032</c>
/// （H.265 对应 <c>hvc1.*</c> / <c>hev1.*</c>）。只在关键帧上带。
///
/// <para>
/// 🔥 <b>它<b>不是</b> <paramref name="Codec"/></b>：那个是"我们这条管道用什么编码"
/// （<c>h264</c> / <c>hevc</c>，中心靠它做能力交集），这个是"WebCodecs 的
/// <c>configure()</c> 要的串"。两者<b>都要存在，谁都不能顶替谁</b>
/// —— 拿 <c>"h264"</c> 去 <c>configure()</c> 只会得到 <c>NotSupportedError</c>，
/// 而那个症状看起来像"浏览器不支持 H.264"。
/// </para>
///
/// <para>
/// ⚠️ <b>必须由被控端给，中心与前端都推不出来</b>：串里的 profile / level
/// 藏在 SPS 里，而 8K 那一路（HEVC）与不同的硬件编码器给出的 profile 各不相同。
/// 手写一个"看起来对"的串，错的表现是<b>能力探测说支持、真解却失败</b>。
/// </para>
/// </param>
/// <param name="Description">
/// <b>解码器初始化描述</b>（<c>avcC</c> / <c>hvcC</c> 字节）。只在关键帧上带。
///
/// <para>
/// 🔥 <b>它<b>有</b>还是<b>没有</b>，决定的是码流的<b>封装</b>，不是"多一段字节"</b>：
/// 带上它 ⇒ 码流必须是<b>长度前缀</b>形式；不带它 ⇒ 码流是 <b>Annex-B</b>
/// （起始码分隔，参数集就混在码流里）。这两件事是<b>连体</b>的
/// ——「贴了 avcC、码流却还是 Annex-B」＝ <c>configure()</c> 失败或花屏。
/// </para>
///
/// <para>
/// ⇒ 本批<b>默认不带</b>（走"带内"，参数集留在码流里，已实测 MF 每个关键帧都重复 SPS）；
/// 走"带外"时它才有值。<b>两者都不能算错</b>，判据是 <c>CodecString</c> 的前缀
/// （<c>avc3</c>/<c>hev1</c> = 带内，<c>avc1</c>/<c>hvc1</c> = 带外）—— 三者必须自洽。
/// </para>
/// </param>
/// <param name="FrameWidth">
/// <b>整幅画面</b>的宽（M5.5 分块/ROI）。<c>0</c> = 这一路的被控端还没有这个概念
/// ⇒ <b>整幅画面就等于这一帧</b>（见 <paramref name="Width"/>）。
///
/// <para>
/// 🔥 <b>为什么不能用 <paramref name="Width"/> 代替</b>：分块之后
/// <paramref name="Width"/> 是<b>某一块</b>的宽（例如 11520 宽的画面切成两块，Width=5760），
/// 而前端画布要的是 11520。混用会让画布缩成一条，然后每块都画到错的位置上
/// —— 而<b>没有任何一处报错</b>。
/// </para>
/// </param>
/// <param name="FrameHeight">整幅画面的高。同 <paramref name="FrameWidth"/>。</param>
/// <param name="RegionX">
/// 这一帧画的是整幅画面里的<b>哪一块</b>（左上角 X，画面坐标系）。
/// 整帧模式下恒为 <c>0</c>。
/// </param>
/// <param name="RegionY">同上，左上角 Y。</param>
/// <param name="TileIndex">
/// 第几块（0 起）。<b>前端靠它决定"这是不是同一批的下一块"</b>：
/// 同一批的所有块共享同一个 <c>Sequence/id</c> 是一个更强的不变量，
/// 但弱客户端给的顺序不一定可靠，所以块序号要单独带。
/// </param>
/// <param name="TileCount">
/// 这一帧一共几块（1 = 没分块）。<b>默认 1 而不是 0</b>：
/// 旧客户端不发这个字段，反序列化会落在默认值上 —— 落成 0 会让"1 / 0 块到齐"
/// 变成永远等不齐（画面一片空白，日志正常）。
/// </param>
public sealed record RemoteStreamFrameHeader(
    string SessionId,
    long Sequence,
    string Codec,
    bool IsKeyframe,
    int Width,
    int Height,
    long CapturedUnixMs,
    long Dropped = 0,
    long? CapturedMonoMs = null,
    string? CodecString = null,
    byte[]? Description = null,
    int FrameWidth = 0,
    int FrameHeight = 0,
    int RegionX = 0,
    int RegionY = 0,
    int TileIndex = 0,
    int TileCount = 1);

/// <summary>
/// 一帧码流。<b>管道、中心中继、浏览器三处用的是同一个形状</b> ——
/// 换编码器时只换 <see cref="RemoteStreamFrameHeader.Codec"/>，
/// 链路上没有任何一层需要跟着改。
/// </summary>
public sealed record RemoteStreamFrame(RemoteStreamFrameHeader Header, byte[] Data);

/// <summary>
/// <b>一帧码流的一片</b>（M5.4-c① 的分片 / chunking）。
///
/// <para>
/// 🔥 <b>为什么需要它</b>：中心那条 SignalR 消息的上限是
/// <see cref="RemoteStreamLimits.SignalRMessageBytes"/>（8 MiB），而 8K 的 IDR
/// 会超过它 —— <b>而越限的后果是掐断整条连接</b>（心跳/遥测/配置/音频全在它上面）。
/// 分片把一帧摊成若干条小消息，到中心再拼回整帧。
/// </para>
///
/// <para>
/// 🔴 <b>「分片（chunking）」与「分块编码（tile）」不是一回事，中文只差一个字</b>：
/// </para>
/// <list type="bullet">
///   <item><b>分片</b>（本类型）：一帧<b>已编好的字节</b>装不进一条消息 ⇒ 拆成 N 条<b>传输消息</b>，
///   到对面拼回来。<b>码流本身一字未变</b>，解码器根本不知道这件事。→ M5.4-c①；</item>
///   <item><b>分块编码</b>：把<b>一幅图</b>切成几块、每块<b>独立编码</b>（各有自己的 SPS/PPS 与参考链）。
///   码流<b>变了</b>，前端要拼接。→ M5.5。</item>
/// </list>
///
/// <para>
/// 🔥 <b>每一片都带完整帧头</b>（约 200 字节 JSON，占单片上限的 0.003%）⇒ 每一片<b>自描述</b>，
/// 中心不必记住"第 0 片长什么样"，也<b>不必</b>假定片是按序到的。
/// </para>
///
/// <para>
/// ⚠️ <b>只有"装不下"的帧才分片</b>：<see cref="ChunkCount"/> 为 1 的帧
/// 仍然走 <c>PushRemoteFrame</c> 那条老路 ⇒ 2160×1440 与 4K 的行为<b>一个字都不变</b>
/// （这个改动对现有场景是<b>惰性</b>的，这是它敢进的底气）。
/// </para>
///
/// <para>
/// ⚠️ <b>它是独立的一条 Hub 消息（<c>PushRemoteFrameChunk</c>），不许给
/// <c>PushRemoteFrame</c> 加参数</b>：SignalR 的实参个数必须与签名严格一致，
/// 加参数等于让<b>旧中心上所有新客户端的每一帧</b>调用失败。
/// </para>
/// </summary>
/// <param name="Header">
/// 这一帧的帧头（<b>每一片都带</b>）。片与片之间它<b>必须一致</b> ——
/// 中心只校验"同一个 <see cref="RemoteStreamFrameHeader.Sequence"/> 下的
/// <see cref="ChunkCount"/> 一致"，不比整份帧头，
/// 因为帧头里的 <c>Description</c> 是 <c>byte[]</c>，而数组在 record 的相等性里<b>按引用比</b>
/// （两份内容相同的 <c>avcC</c> 会被判成"不一样"）。
/// </param>
/// <param name="ChunkIndex">这一片是第几片（<b>从 0 起</b>）。</param>
/// <param name="ChunkCount">
/// 这一帧一共几片。<b>取值 2..<see cref="RemoteStreamLimits.MaxChunkCount"/></b> ——
/// 1 片的帧不许走这条消息（走老路）；越界一律<b>丢帧 + 计数，绝不抛</b>
/// （这条路径上"抛"的后果是掐整条连接，而这里最坏的后果应该只是丢一帧）。
/// </param>
/// <param name="Data">这一片承载的裸码流字节。</param>
public sealed record RemoteStreamFrameChunk(
    RemoteStreamFrameHeader Header,
    int ChunkIndex,
    int ChunkCount,
    byte[] Data);

/// <summary>
/// 远控音频的编码格式（M5.3）。
/// <para>
/// ⚠️ 它<b>与 <see cref="RemoteCodecs"/> 是两套</b>，不要混：视频那套的名字
/// （h264/hevc/mjpeg）是<b>被控端硬编能力</b>决定的，而音频这里只有一个 Opus
/// —— 合并成一套会让"这台机器支持 H.264"与"音频用 Opus"变成同一类枚举值，
/// 而它们一个来自能力探测、一个是协议常量。
/// </para>
/// </summary>
public static class RemoteAudioCodecs
{
    /// <summary>
    /// Opus（20 ms 一帧，48 kHz）。
    /// <para>
    /// 🔥 选它的三条理由：① 浏览器 <c>AudioDecoder</c> 原生支持；
    /// ② 低码率下音质远好于其它选项（96–128 kbps 足够）；
    /// ③ <b>720×1</b> 的帧尺寸极小，对这条链路几乎没有重量。
    /// </para>
    /// </summary>
    public const string Opus = "opus";
}

/// <summary>
/// <b>被控端音频通道</b>处于什么状态（M5.3）。
///
/// <para>
/// 🔥 <b>为什么要三态而不是一个 <c>bool HasAudio</c></b>：
/// "正在接"与"接不上"压成同一个 <c>false</c>，会让用户去重装一台<b>本来就好好的</b>客户端
/// —— 而"没有声卡"这个最常见的失败原因，重装客户端根本解决不了。
/// </para>
/// <para>
/// 与 <see cref="AgentInputStates"/> 同一条纪律：<b>不许压成布尔</b>。
/// 但它比输入少一态 —— 音频<b>没有中心侧的会话级闸门</b>
/// （有声音是这路会话的默认行为，不像"能不能动别人鼠标"那样需要逐次授权）。
/// </para>
/// </summary>
public static class AgentAudioStates
{
    /// <summary>正在建立音频通道（托盘正在架管道 / 服务正在接）。</summary>
    public const string Connecting = "connecting";

    /// <summary>已经就绪，音频正在流。</summary>
    public const string Ready = "ready";

    /// <summary>
    /// <b>接不上</b>，且原因见 <c>AudioReason</c>。
    /// <para>最常见的三类：本机没有音频输出设备 / 托盘版本过旧不认识这条通道 / 被控端不是 Windows。</para>
    /// </summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// 音频链路的量纲上限（M5.3）。
/// <para>
/// 它<b>不是"性能调优参数"，是防呆</b>：WASAPI loopback 交上来的格式由系统决定，
/// 万一拿到一个 192 kHz / 8 声道的格式，不挡住就会一帧几 MB 地把链路打爆
/// （而表现是"声音断断续续"甚至"整个连接掉线"，没人会想到是采样率）。
/// 超限一律<b>降级或拒开并如实说明</b>，不静默截断。
/// </para>
/// </summary>
public static class RemoteAudioLimits
{
    /// <summary>接受的最高采样率。48 kHz 是 Opus 的原生采样率，留一倍余量给 96 kHz。</summary>
    public const int MaxSampleRate = 96_000;

    /// <summary>接受的最多声道数（立体声足够 —— 多声道系统在下混后送出）。</summary>
    public const int MaxChannels = 2;

    /// <summary>
    /// 单个音频帧（一个 Opus 包的载荷）的字节上限。
    /// <para>
    /// 20 ms 的 Opus 在 128 kbps 下约 <b>320 字节</b>；即使用 510 kbps 的极端设置也不到 1.3 KB。
    /// 给到 <b>8 KB</b> 是为了给"采集缓冲被凑成一整段"留余量，同时把畸形输入挡在门外
    /// —— 与视频那条上限是同一条纪律（见 <c>RemoteStreamProtocol.MaxPayloadBytes</c>，
    /// 它引用 <see cref="RemoteStreamLimits.MaxFrameBytes"/>）。
    /// </para>
    /// <para>
    /// ⚠️ <b>但两者的量级差得很远，别照着视频那个数改这一个</b>：视频一帧可以有 MB 级
    /// （8K/H.264 的大关键帧），而音频一包永远是几百字节 —— 这里是"防畸形输入的天花板"，
    /// 那里是"管道物理装不下"。共用的只是"要有上限"这条纪律。
    /// </para>
    /// </summary>
    public const int MaxFrameBytes = 8 * 1024;
}

/// <summary>
/// 一段音频的<b>头</b>（不含载荷）。
///
/// <para>
/// 🔥 <b>为什么音频也要有帧头</b>：它承担三件在别处无法表达的事 ——
/// ① <b>解码参数</b>（采样率 / 声道 / 每帧样本数）：Opus <b>不带内省信息</b>，
/// 解码端必须预先知道这些；② <b>AV 同步的时间戳</b>（同一会话的单调时钟）；
/// ③ <b>与视频对账的序号</b>。
/// </para>
/// </summary>
/// <param name="SessionId">属于哪一路会话。</param>
/// <param name="Sequence">单调递增序号（用于前端判断"有没有漏"）。</param>
/// <param name="Codec">取值见 <see cref="RemoteAudioCodecs"/>。</param>
/// <param name="SampleRate">采样率（Hz）。Opus 固定 48000。</param>
/// <param name="Channels">声道数（1 或 2）。</param>
/// <param name="SamplesPerFrame">
/// 这一帧包含多少个<b>每声道</b>样本。20 ms @ 48 kHz = <b>960</b>。
/// <para>解码端要用它算出这一帧的时长（<c>SamplesPerFrame / SampleRate</c> 秒）来做调度。</para>
/// </param>
/// <param name="CapturedUnixMs">
/// 采集时刻（UTC 纪元毫秒）。与视频帧同义：<b>只用于测延迟</b>，不用于播放调度。
/// </param>
/// <param name="CapturedMonoMs">
/// <b>会话单调时钟</b>上的采集时刻（毫秒）。🔊 <b>AV 同步靠的就是它</b> ——
/// 与 <see cref="RemoteStreamFrameHeader.CapturedMonoMs"/> 同一条轴，前端拿两者的差做对齐。
/// <para>
/// ⚠️ 它取自 <b>WASAPI 给出的 QPC 位置</b>（音频引擎真正采到这一段的那一刻），
/// 而不是"我们读到它的那一刻" —— 后者会带上一个可变的设备缓冲延迟（10–30 ms），
/// 而 40 ms 的同步预算经不起这么花。
/// </para>
/// </param>
/// <param name="Dropped">被控端<b>累计丢弃</b>的音频帧数（正常恒为 0；非 0 意味着音频有断点）。</param>
public sealed record RemoteAudioFrameHeader(
    string SessionId,
    long Sequence,
    string Codec,
    int SampleRate,
    int Channels,
    int SamplesPerFrame,
    long CapturedUnixMs,
    long CapturedMonoMs,
    long Dropped = 0);

/// <summary>
/// 一段音频。<b>管道、中心中继、浏览器三处用的是同一个形状</b>。
/// <para>与 <see cref="RemoteStreamFrame"/> 对称：换编码器时只换 <c>Codec</c>，链路上没有一层要改。</para>
/// </summary>
public sealed record RemoteAudioFrame(RemoteAudioFrameHeader Header, byte[] Data);

/// <summary>
/// 远控会话的动作名（<b>中心 → 客户机</b>，以及客户机服务 → 托盘）。
///
/// <para>
/// 🔥 <b>两跳共用一组常量，是刻意的。</b>这条链路上有两次"字符串合同"：
/// 中心经 SignalR 发给客户机服务、客户机服务再经命名管道发给托盘。
/// 如果两处各写一组名字，就会出现"中心认得的动作名，托盘不认得" ——
/// 表现为<b>点了开始远控、服务说已下发、画面永远不出来</b>，而两边日志都正常。
/// </para>
/// </summary>
public static class RemoteSessionActions
{
    /// <summary>开始推流。<c>sessionId</c> 必填（它同时决定客户机侧的管道名）。</summary>
    public const string Start = "start";

    /// <summary>停止推流。</summary>
    public const string Stop = "stop";

    /// <summary>切黑白模式（决策 14）。</summary>
    public const string Grayscale = "grayscale";

    /// <summary>
    /// <b>问一句"这台机器有没有音频采集能力"</b>（M5.3）。
    ///
    /// <para>
    /// 🔥 <b>为什么需要这一问，而不是"连不上音频管道就当没有音频"</b>：
    /// 服务侧能观察到的只有"管道连不上"，而"连不上"有几个完全不同的原因 ——
    /// <b>没有声卡</b> / <b>托盘版本过旧不认识这条通道</b> / <b>托盘刚重启</b> /
    /// <b>音频引擎拒绝了我们要求的格式</b>。它们的现场处置各不相同
    /// （查硬件 / 升级客户端 / 重试 / 改设备格式），而页面上如果只有一句"没有声音"，
    /// 用户只能去查网络。
    /// </para>
    ///
    /// <para>
    /// <b>约定</b>：成功 = 有能力（Message 里带一句格式描述，进日志用）；
    /// 失败 = 没有能力，<b>Message 就是给人看的原因</b>（会原样进页面的"音频不可用"那一格）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是<b>低频</b>动作（每路会话一次，且只在音频通道没接上时才有意义），
    /// 走 <c>launch</c> / <c>volume</c> 那条"一请求一连接"的会话管道即可，不需要新协议。
    /// </para>
    /// </summary>
    public const string AudioProbe = "audio-probe";

    /// <summary>
    /// <b>请被控端立刻补一个关键帧</b>（M5.4-c①）。
    ///
    /// <para>
    /// 🔥 <b>它为什么必须存在</b>：中心的重组（见 <c>RemoteStreamFrameChunk</c>）会丢帧，
    /// 而 H.264 丢一帧就在参考链上戳一个洞 —— 观看端从那一刻起画面全花，
    /// <b>而解码器不会报错</b>。这件事<b>发生在中心</b>，被控端无从知道
    /// ⇒ 必须有一条回传把"你该重开 GOP 了"告诉它。
    /// 没有它，恢复代价是<b>最多一个 GOP</b>（5 秒花屏）；有它，压到<b>下一帧</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>方向是"中心 → 服务 → 托盘"，与 <c>grayscale</c> 同一条管道</b>，
    /// 走 <c>launch</c> / <c>volume</c> 那套"一请求一连接"的会话命令即可：
    /// 它是低频的（服务侧已按会话节流到最多每秒一次），
    /// 而且它要的答案是"发出去了没有"，不是"画面变了没有"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>托盘侧没在推流时是失败</b>（与 <c>grayscale</c> 同形）：这条指令的作用对象是
    /// "正在推的那一路"，没有它就没有对象。如实说"此刻没有正在推流的会话"，
    /// 而不是安静地成功。
    /// </para>
    /// </summary>
    public const string RequestKeyframe = "keyframe";

    /// <summary>
    /// <b>问一句"你的桌面已经静止多久了"</b>（M5.4-c② 步 7）。
    ///
    /// <para>
    /// 🔥 <b>为什么这件事必须"问"，而不能由服务自己从"多久没收到帧"推</b>：
    /// "没有新帧"至少有三种成因 —— <b>桌面真的没变</b>（正常）、
    /// <b>被控端编不出来</b>（故障）、<b>中心把帧丢了</b>（链路）。三者在计数上
    /// 长得一模一样，而处置完全相反。只有<b>抓屏器</b>知道是哪一种
    /// （它回"没有新帧"就是第一种），所以这句话只能由它说。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>方向是"服务 → 托盘"，由服务每秒问一次</b>（它正好也每秒上报一次计数）。
    /// 走 <c>launch</c> / <c>volume</c> 那条"一请求一连接"的会话管道：
    /// 本机命名管道一次往返在**亚毫秒**量级，一秒一次连负载都算不上
    /// （<c>SessionPipeChannel</c> 的类注释里写着"宁可多连几次"）。
    /// <b>不走码流管道</b>：那是热路径，往里塞"状态消息"会让"帧数"这个判据与
    /// "帧"这个语义分叉，而那正是最不该出错的一处。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>没有正在推流的会话时是失败</b>（与 <c>grayscale</c> / <c>keyframe</c> 同形）：
    /// 安静地回一个 0 会让服务把"这台机器没有流"上报成"桌面刚动过"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧托盘不认识它是安全的</b>：它会回一句"不认识的远控动作"，
    /// 服务据此把 <c>DesktopIdleMs</c> 留成 <c>null</c>（"这台机器不提这件事"），
    /// 页面画 <c>—</c>。这正是三态里第一格存在的理由。
    /// </para>
    /// </summary>
    public const string DesktopIdle = "desktop-idle";

    /// <summary>
    /// <b>改这一路的质量档</b>（M5.5 运行时自适应）：码率 / 帧率 / 灰度三项一起下发。
    ///
    /// <para>
    /// 🔥 <b>为什么不能借 <c>grayscale</c> 或 <c>start</c> 顶包</b>：
    /// 灰度只改一项，而自适应要同时改<b>码率与帧率</b>；
    /// <c>start</c> 会把整路会话重建（换 sessionId ⇒ 观看端要重连）。
    /// 自适应每降一档就重连一次，现场看到的是"画面每隔一会儿黑一下"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>码率变化必然要重建编码器</b>：MF 的码率是<b>创建时定死</b>的
    /// （运行中改 `CODECAPI_AVEncCommonMeanBitRate` 回读不变，见 REF-M5 铁律 16），
    /// 所以托盘侧会重建一次编码器（实测 ≈0.3 s 空窗、首帧必是关键帧）。
    /// 页面上要能看到这个代价，而不是"切了一档、什么都不说"。
    /// </para>
    /// </summary>
    public const string Quality = "quality";

    /// <summary>
    /// <b>只编画面里的这一块</b>（M5.5 第 5 项：ROI 编码）。
    ///
    /// <para>
    /// 🔥 <b>它省的是"编码面积"，不是"带宽"</b>：8K 桌面在 1080p 视口下
    /// 只编 1080p 那一块 ⇒ 编码/转换的像素量降到 1/16，帧率能上一个量级；
    /// 而<b>传的仍然是那块画面</b>，所以带宽<b>按面积</b>下降（视口以外的东西根本不传）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>代价必须说清楚</b>：换视口 = 重建编码器 = ≈0.3 秒空窗 + 首帧必关键帧。
    /// 所以<b>托盘侧去抖</b>（滚动停下来才换），页面上也要显示"视口已切换"。
    /// 每滚一下就换一次的实现会让画面永远停在关键帧上 —— 看起来像"帧率更低了"。
    /// </para>
    ///
    /// <para>
    /// <b>参数</b>：<c>ViewportX/Y/Width/Height</c>（画面像素坐标，1:1）；
    /// <b>宽度或高度为 0 = 关掉 ROI</b>（回到整幅画面）。
    /// </para>
    /// </summary>
    public const string Viewport = "viewport";

    /// <summary>
    /// <b>读 / 写被控机的剪贴板</b>（M5.5 剪贴板同步）。
    ///
    /// <para>
    /// 🔥 <b>方向与其它动作都不一样</b>：读的时候要<b>把文本带回来</b>，
    /// 而这条指令链路的回执止步于服务进程（中心看不到托盘的回复）。
    /// ⇒ 读走"托盘 → 服务 → 中心"的上行（<c>PushRemoteClipboard</c>），
    /// 写才是真正的下行。两者共用本动作名，靠 <c>clipboardAction</c> 区分。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>写被控机剪贴板是"作用到别的机器的写操作"</b>（铁律 7）：
    /// 归属校验（必须正在看这一路）在中心做，被控端不重复判"谁在写"，
    /// 但它<b>不会</b>把内容当作可信输入 —— 超过上限直接拒绝（见 <c>RemoteClipboardLimits</c>）。
    /// </para>
    /// </summary>
    public const string Clipboard = "clipboard";

    /// <summary>
    /// <b>索取被控机的屏清单</b>（M5.5 多屏选择）。
    ///
    /// <para>
    /// 🔥 <b>为什么屏清单必须由托盘回答</b>：它要枚举的是<b>用户会话里的显示器</b>，
    /// 而服务进程在 Session 0 —— 那里既没有桌面也没有显示器。
    /// 编码能力探测（跑在服务里）答不了这个问题，所以它不能搭能力上报的车。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 回程同样不是回执：托盘把清单交给服务，服务再<b>上行</b>报给中心
    /// （<c>ReportRemoteScreens</c>）。中心侧是一个"问一次、等一秒、读缓存"的形态，
    /// 端点是 <c>GET /api/remote/screens/{agentId}</c>。
    /// </para>
    /// </summary>
    public const string Screens = "screens";
}

/// <summary>
/// <b>质量档位</b>（M5.5 运行时自适应）。
///
/// <para>
/// 🔥 <b>为什么档位表放在契约里、而不是各端各写一份</b>：
/// 中心要按档位判"该降还是该升"，被控端要按档位设编码器，页面要显示"现在是什么档"。
/// 三处各写一份数字，迟早出现"页面写着高画质、编码器按低码率跑"——
/// 而那种不一致<b>没有任何一层会报错</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>数字的来处</b>：`balanced` 对齐现有默认（12 fps，见
/// <c>RemoteStreamHost.DefaultMaxFps</c> 与 2160×1440 实测 ≈3–4 Mbps 的码流）；
/// `high` 面向 4K/8K（30 fps、20 Mbps —— 千兆下 4 路已接近 80%）；
/// `smooth` 面向 1080p–1440p 的硬编（<b>60 fps</b>、25 Mbps，2026-10-18 实测硬编余量 ≈105 fps@2160×1440）；
/// `low` 是"能看见就行"（12 fps、3 Mbps），对应方案 §1.5 降级链的倒数第二档。
/// <b>这些不是测量值，是配额的上界</b>；实测值由页面上的码率/帧率显示。
/// </para>
/// </summary>
public static class RemoteQualityProfiles
{
    /// <summary>自动：从 <see cref="Balanced"/> 起步，编不动就往下走（见中心的 governor）。</summary>
    public const string Auto = "auto";

    /// <summary>低：优先保"能看见"，弱网 / 老机器。</summary>
    public const string Low = "low";

    /// <summary>均衡：默认档。</summary>
    public const string Balanced = "balanced";

    /// <summary>高：4K/8K 与局域网充裕时用。</summary>
    public const string High = "high";

    /// <summary>
    /// <b>流畅：60 帧</b>（M7.4）。给"看动画 / 看鼠标跟手"的场景，不为省带宽。
    ///
    /// <para>
    /// 🔥 <b>为什么是单独一档，而不是把 <see cref="High"/> 从 30 提到 60</b>：
    /// <c>high</c> 的 30 fps 是<b>按 4K/8K 的上限</b>定的，而 4K60 的编码预算在弱 GPU 上会爆；
    /// 60 帧真正有意义的场合是<b>1080p–1440p 的硬编</b>，两者不是同一条曲线。
    /// 混成一档，会出现"用户为了流畅切高档，结果分辨率一高就掉帧"。
    /// </para>
    /// <para>
    /// ⚠️ <b>它的前提是硬编</b>：软件 JPEG（GDI 抓屏）这条路实测上限约 21 fps，
    /// 被控端会<b>如实夹到 20</b> 并在日志里说明（见 <c>RemoteStreamPacing.ApplyEncoderCap</c>）。
    /// 所以切到这一档却看到 20 fps，不是"档位没生效"，是那台机器没有硬编。
    /// </para>
    /// </summary>
    public const string Smooth = "smooth";

    /// <summary>认不认得这个档位名（<c>null</c> = 没指定 ⇒ 按 auto）。</summary>
    public static bool IsKnown(string? profile) =>
        profile is Auto or Low or Balanced or High or Smooth;

    /// <summary>
    /// 把一个档位名解析成<b>码率与帧率上界</b>。
    /// <para>
    /// ⚠️ <see cref="Auto"/> 解析成 <see cref="Balanced"/> 的数：<b>auto 不是第四套数字</b>，
    /// 它是"以 balanced 起步、由 governor 调整"。给它一套独立的数字，
    /// 页面上的"自动"就会变成一个谁也说不清的黑盒。
    /// </para>
    /// </summary>
    public static (int MaxFps, int BitrateKbps) Resolve(string? profile) => profile switch
    {
        Low => (12, 3_000),
        High => (30, 20_000),
        Smooth => (60, 25_000),
        _ => (12, 6_000),
    };

    /// <summary>归一化：不认得的值一律回到 <see cref="Auto"/>（并让调用方自己留痕）。</summary>
    public static string Normalize(string? profile) => IsKnown(profile) ? profile! : Auto;

    /// <summary>
    /// 帧率的<b>合法区间</b>。与 <c>RemoteStreamHost</c> 里的
    /// <c>MinFps</c> / <c>MaxFps</c> 是同一组数（那边是执行端，它还会再夹一次）。
    /// </summary>
    public const int MinFps = 1;

    /// <summary>上限 60：再高没有意义（面板就是 60 Hz），且没有一层能验证 120。</summary>
    public const int MaxFps = 60;

    /// <summary>
    /// 把「用户要的帧率」与「档位推荐的帧率」折成<b>下发给被控端的目标值</b>。
    ///
    /// <para>
    /// 🔥🔥 <b>M7.16 起：请求值优先，档位只是"没指定时的默认"</b>。原来的折法是
    /// <c>Min(请求, 档位)</c>，理由是"别让页面写着低画质却按 30 fps 推"。
    /// 那条理由只在<b>档位是用户唯一能碰的旋钮</b>时成立 —— 而 M7.13 把参数搬进
    /// 悬浮面板之后，页面上<b>两个</b>旋钮都在（档位、帧率），于是
    /// <c>Min</c> 让帧率那个旋钮在档位之上<b>整个失效</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item>默认档位是 <c>auto</c> ⇒ 解析成 12 fps；</item>
    ///   <item>用户在帧率里选 30 ⇒ 下发 <c>Min(30, 12) = 12</c>；</item>
    ///   <item>页面上没有任何一处说这件事 —— 它只是"选了没用"。</item>
    /// </list>
    /// <para>
    /// ⚠️ 代价说清楚：<b>码率仍由档位决定</b>，所以「均衡（6 Mbps）＋ 60 fps」是真的能选出来的
    /// 组合，而那意味着每帧只有 100 kbps —— 画面会糊。这是<b>用户明确选的</b>，
    /// 页面上靠"档位"与"生效帧率"两个读数让他看见；不在这里替他做决定。
    /// </para>
    /// </summary>
    /// <param name="requestedMaxFps">页面传来的帧率（<c>null</c> = 没指定）。</param>
    /// <param name="profileFps">档位推荐的帧率（<see cref="Resolve"/> 的第一项）。</param>
    public static int FoldTargetFps(int? requestedMaxFps, int profileFps)
        => Math.Clamp(requestedMaxFps ?? profileFps, MinFps, MaxFps);
}

/// <summary>
/// 中心下发到客户机的远控指令（<c>AgentHubMethods.Command</c> 的载荷）。
///
/// <para>
/// ⚠️ ⚠️ <b>它跟着 <c>AgentHubMethods.Command</c> 走，而不是新开一个 Hub 方法</b>，
/// 这是有意的：那条通道的实参个数不能变（SignalR 的硬约束，见
/// <c>AgentHubClientMethods.Heartbeat</c> 的实测备注），
/// 而它的载荷 <c>AgentCommandDto</c> 本来就是"名字 + 参数"的通用信封 ——
/// 加一个新的指令名不会影响任何旧客户端（旧客户端会回一句"本机不处理的指令"）。
/// </para>
///
/// <para>
/// 🔥 <b><c>SessionId</c> 由中心生成</b>（而不是客户机自己起）：中心要在
/// 会话建立的那一刻就把它写进注册表，客户机只是照用它去取管道名。
/// 让客户机自己生成的话，中心得等它回话才知道"这路流叫什么" ——
/// 而那期间到来的观看看不到任何东西。
/// </para>
/// </summary>
/// <param name="SessionId">会话 Id（中心生成，客户机用它命名码流管道）。</param>
/// <param name="ScreenId">要抓哪块屏；<c>null</c> = 客户机自动选（多屏时取虚拟桌面）。</param>
/// <param name="MaxFps">目标帧率上限；<c>null</c> = 客户机默认值。</param>
/// <param name="Grayscale"><see cref="RemoteSessionActions.Grayscale"/> 用：开还是关。</param>
/// <param name="Codecs">
/// <b>可接受的编码格式，按偏好排序</b>（第 0 项 = 首选；M5.4-b 第 7 步）。
/// 取值见 <see cref="RemoteCodecs"/>。
///
/// <para>
/// 🔥 <b>为什么是"一个有序集合"而不是"一个格式"</b>：交集其实有<b>三项</b>输入，
/// 而第三项 —— <b>本次会话的分辨率</b> —— 只有被控端在挑屏之后才知道
/// （H.264 硬编封顶 4096、HEVC 封顶 8192，同一台机器接不同的屏答案不同）。
/// 中心能给的最多是"偏好 ＋ 可接受的边界"，最后一步裁决必须在被控端做。
/// </para>
///
/// <para>
/// ⚠️ 被控端<b>只许在这个集合里挑</b>，不许挑集合外的格式 ——
/// 集合本身就是"浏览器声明过 ∩ 机器做得到"，越出去就会出现浏览器解不开的黑屏。
/// </para>
///
/// <para><c>null</c> = 调用方没指定（按带内 H.264）。</para>
/// </param>
/// <param name="Packaging">
/// 参数集放哪（<see cref="RemoteStreamPackagings"/>）。<b>由观看端定、中心透传</b> ——
/// <c>codec</c> 串用哪个前缀、<c>description</c> 要不要，是那个浏览器的解码器说了算。
/// </param>
public sealed record RemoteSessionCommandDto(
    string Action,
    string SessionId,
    string? ScreenId = null,
    int? MaxFps = null,
    bool? Grayscale = null,
    IReadOnlyList<string>? Codecs = null,
    string? Packaging = null,

    /// <summary>
    /// <see cref="RemoteSessionActions.Quality"/> 用：质量档位名（<see cref="RemoteQualityProfiles"/>）。
    /// <para>
    /// ⚠️ 中心下发的是<b>归一化之后</b>的名字 —— 不认得的值在中心就被改写并留痕，
    /// 否则托盘的日志里会出现一个"看起来像新功能"的档位名，而实际按默认跑。
    /// </para>
    /// </summary>
    string? Quality = null,

    /// <summary>
    /// <see cref="RemoteSessionActions.Quality"/> 用：目标码率（kbps）。
    /// <para>
    /// ⚠️ <c>null</c> = 这一条不涉及码率（例如只改帧率），<b>不是 0</b>。
    /// 0 会被托盘当成"显式要求零码率"而拒绝。
    /// </para>
    /// </summary>
    int? BitrateKbps = null,

    /// <summary><see cref="RemoteSessionActions.Clipboard"/> 用：<c>get</c> / <c>set</c>。</summary>
    string? ClipboardAction = null,

    /// <summary><see cref="RemoteSessionActions.Clipboard"/> 且 <c>set</c> 时要写入的文本。</summary>
    string? ClipboardText = null,

    /// <summary>
    /// <see cref="RemoteSessionActions.Viewport"/> 用：要编的那一块（画面像素坐标，1:1）。
    /// <para>
    /// ⚠️ <b>宽或高为 0 = 关掉 ROI</b>（回到整幅画面）。四个值必须<b>一起</b>给：
    /// 只给 X 而 Width=0 会被托盘理解成"关掉"，那不是"挪到 X 但宽度不变"。
    /// 想挪视口就四个值全给（页面本来就知道视口多大）。
    /// </para>
    /// </summary>
    int ViewportX = 0,

    /// <summary><see cref="RemoteSessionActions.Viewport"/> 用：视口左上角 Y。</summary>
    int ViewportY = 0,

    /// <summary><see cref="RemoteSessionActions.Viewport"/> 用：视口宽（0 = 关掉 ROI）。</summary>
    int ViewportWidth = 0,

    /// <summary><see cref="RemoteSessionActions.Viewport"/> 用：视口高（0 = 关掉 ROI）。</summary>
    int ViewportHeight = 0);

/// <summary>
/// 客户机<b>上报给中心</b>的远控会话状态（M5.1 上行载荷）。
///
/// <para>
/// 🔥 <b>为什么必须由客户机上报，而不是中心自己推</b>：会话状态里
/// 最有价值的那部分 ——"到底有没有在推、推了多少帧、为什么停了" ——
/// 只发生在<b>被控机那一侧</b>。中心能做的最多是"我收到过多少帧"，
/// 而那句话在"推流根本没起来"的时候正好什么都说明不了。
/// </para>
///
/// <para>
/// ⚠️ <b>它与 <see cref="RemoteSessionDto"/> 是两种东西，别合并</b>：
/// 后者是中心<b>对外的</b>表述（含建立时刻、显示模式这些中心自己才知道的字段），
/// 本类型是客户机<b>对内的</b>上报（只有它能观测到的东西）。合并之后，
/// "谁填哪个字段"会变成一个每次都要重新想一遍的问题。
/// </para>
/// </summary>
/// <param name="State">见 <see cref="RemoteSessionStates"/>。</param>
/// <param name="Reason">为什么停 / 为什么失败（人话，直接进页面）。</param>
/// <param name="Frames">客户机<b>发出</b>的帧数。</param>
/// <param name="Bytes">客户机发出的码流字节数。</param>
/// <param name="Fps">客户机侧实测帧率。</param>
/// <param name="Dropped">客户机侧累计丢帧数（编码失败 / 单帧超限）。</param>
public sealed record AgentRemoteSessionStateDto(
    string SessionId,
    string State,
    string? Reason,
    long Frames,
    long Bytes,
    double Fps,
    long Dropped,

    /// <summary>
    /// <b>被控端的输入回传通道处于什么状态</b>（M5.2）。取值见 <see cref="AgentInputStates"/>。
    ///
    /// <para>
    /// 🔥 <b>为什么它必须由被控端来说、而且必须是一个三态字符串</b>：
    /// 输入这条链路上，"中心没打开控制开关"与"被控端根本没有输入管道
    /// （客户端版本过旧 / 托盘不在用户会话里）"在现场的表现<b>一模一样</b> ——
    /// 都是"点了没反应"。而两者的下一步动作完全相反：
    /// 前者去点那个开关，后者去升级客户端。中心无法自己分辨（它只知道指令发出去了）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b><c>null</c> 与 <c>"unavailable"</c> 是两件事，别合并</b>：
    /// <c>null</c> = 这台机器<b>压根没提过</b>输入这回事（旧版客户端，
    /// 它连 <c>InputState</c> 这个字段都不认识）；<c>"unavailable"</c> = 新版客户端
    /// <b>明确说</b>"我这条管道接不上"。前者要升级，后者要看它给的原因。
    /// </para>
    /// </summary>
    string? InputState = null,

    /// <summary>
    /// <b>被控端的音频通道处于什么状态</b>（M5.3）。取值见 <see cref="AgentAudioStates"/>。
    ///
    /// <para>
    /// 🔥 与 <see cref="InputState"/> 同一条纪律：<b>三态字符串，不许压成 bool。</b>
    /// 音频最常见的失败原因是<b>被控机根本没有音频输出设备</b>（虚拟机 / 无声卡工控机），
    /// 而那与"通道还在建"在页面上如果都表现为"没有声音"，现场只会去查网络。
    /// </para>
    ///
    /// <para>⚠️ <c>null</c> = 旧版客户端（不认识这个字段），与 <c>"unavailable"</c> 是两件事。</para>
    /// </summary>
    string? AudioState = null,

    /// <summary>音频不可用的原因（仅 <c>unavailable</c> 时有值；其余情况为 <c>null</c>）。</summary>
    string? AudioReason = null,

    /// <summary>
    /// 被控端<b>已经送到中心</b>的音频段数（M5.3）。
    ///
    /// <para>
    /// 🔥 <b>这三个字段是"被控端发出数"唯一的传输通道</b>，别以为它们只是页面上好看：
    /// 中心侧的 <see cref="RemoteSessionStatsDto.AudioFramesReceived"/> 与
    /// <see cref="RemoteSessionAgentStatsDto.AudioFramesSent"/> 必须摆在一起看 ——
    /// "它发 500 / 我们收 12"是<b>链路上在掉</b>，"它发 3 / 我们收 3"是
    /// <b>被控端根本采不出音</b>，而这两种故障在只看一个数的时候长得一模一样。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 「已经送到中心」的口径是<b>服务进程成功推给中心</b>的那一段，
    /// 而不是托盘写进管道的段数 —— 后者在托盘自己的日志里。本机管道丢包基本不可能，
    /// 所以两个数正常情况下应当相同；不同就说明问题在本机管道上。
    /// </para>
    ///
    /// <para>⚠️ <b><c>0</c> 与 <c>null</c> 在这里不是一回事</b>：老客户端不认识这三个字段，
    /// 反序列化后也是 <c>0</c>。所以判断"这台机器到底有没有在发音频"要看
    /// <see cref="AudioState"/>，<b>不是</b>看这里的 <c>0</c>。</para>
    /// </summary>
    long AudioFrames = 0,

    /// <summary>被控端已经送到中心的音频码流字节数（M5.3）。</summary>
    long AudioBytes = 0,

    /// <summary>
    /// 被控端<b>累计丢弃</b>的音频段数（M5.3；正常恒为 0）。
    /// <para>
    /// 🔥 音频的丢与视频的丢<b>不是同一个量级的事</b>：视频丢一帧只是抖一下，
    /// 音频丢一段就是一个可听见的断点。所以它一旦非 0，页面上必须显眼地说出来
    /// —— 而不是混进"帧率"那个数字里。
    /// </para>
    /// </summary>
    long AudioDropped = 0,

    /// <summary>
    /// 输入通道不可用的原因（M5.2 补齐：<b>仅 <see cref="InputState"/> 为 <c>unavailable</c> 时有值</b>）。
    ///
    /// <para>
    /// 🔥 <b>为什么它必须与上面的 <see cref="Reason"/> 分开</b>：
    /// 在它出现之前，输入的原因是搭 <see cref="Reason"/> 这个位置来回传的 ——
    /// 而 <see cref="Reason"/> 的本义是<b>"这路会话为什么结束"</b>。
    /// 两者恰好在"会话正常结束、而输入本来就不通"的时候撞在一起：
    /// 页面上于是会写着「输入通道不可用：已在本机取消推流」，
    /// 而真正的原因（"本机已禁止远程输入"）被顶掉了 —— 一句看起来很像回事、
    /// 却指向完全不同处置方法的话。
    /// </para>
    ///
    /// <para>
    /// 三个原因字段（这个、<see cref="AudioReason"/>、以及会话的 <see cref="Reason"/>）
    /// 各说各的事，<b>不许再互相借用</b>：借用一次不会有任何报错，
    /// 只会在某一种组合下悄悄显示错的那一句。
    /// </para>
    /// </summary>
    string? InputReason = null,

    /// <summary>
    /// <b>本机对"这一路用什么格式编"的裁决说明</b>（M5.4-b 第 7 步）。
    /// 只在<b>有话要说</b>的时候才有值（<c>null</c> = 一切照中心的首选，没什么可说的）。
    ///
    /// <para>
    /// 🔥 <b>为什么必须让被控端把这句话报上来，而不是中心自己推</b>：
    /// 中心算出来的是"**偏好** ＋ 可接受集合"，而<b>最终裁决在被控端</b> ——
    /// 因为交集还有第三项输入（本次会话的<b>分辨率</b>），它只有被控端在挑屏之后才知道
    /// （H.264 硬编封顶 4096、HEVC 封顶 8192）。
    /// 于是会出现"中心首选 H.264、实际送出来的是 HEVC"这种**正确的**偏离 ——
    /// 没有这句话，现场看到的就是"页面写着 H.264、码流却是 HEVC"，
    /// 而那是"哪一侧坏了"完全无法判断的局面。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>刻意不复用 <see cref="Reason"/></b>：那个字段的本义是"这路会话为什么结束"，
    /// 而这里说的是"它为什么**没**按中心的首选做，但仍然正常在推"。
    /// 借过去的话，一个<b>成功</b>的会话会在页面上带着一句像失败原因的话 ——
    /// 这正是 <see cref="InputReason"/> 那段注释里踩过的坑，不再踩第二次。
    /// </para>
    ///
    /// <para>⚠️ <c>null</c> 也可能是"老客户端不认识这个字段"⇒ 页面不许把 <c>null</c> 画成"没问题"，
    /// 只能画成"没有额外说明"。</para>
    /// </summary>
    string? CodecNote = null,

    /// <summary>
    /// <b>被控端的桌面已经静止了多久</b>（毫秒；M5.4-c② 步 7）。
    /// <para>
    /// 🔥 <b>它关的是一条很贵的挂账</b>（<c>REF-M5 §14.14</c>）：修完"活性判据"之后，
    /// <b>「桌面静止」与「托盘卡住」在协议层上长得一模一样</b> ——
    /// 都是"没有新帧、状态照报"，中心只能靠猜。而被控端<b>早就知道</b>
    /// （DXGI 抓屏返回"没有新帧"就是"桌面内容与指针都没变"），它只是<b>不说</b>。
    /// </para>
    ///
    /// <para>
    /// 🔥🔥 <b>为什么是"多久"而不是 <c>bool Static</c></b>：<c>null</c>、<c>0</c>、<c>12000</c>
    /// 是三件不同的事 —— <b>「旧客户端不认识」/「刚动过」/「已静止 12 秒」</b>，
    /// 而一个 bool 把它们压成两件。页面上这三句的下一步动作完全不同：
    /// 第一句要升级客户端，第二句什么都不用做，第三句是"这画面没动是正常的"。
    /// （同一条纪律见 <see cref="InputState"/> / <see cref="AudioState"/>：不许压成布尔。）
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它是扩展字段、不是改签名</b>：Hub 方法的实参个数没有变（加一个成员而已，
    /// 旧客户端反序列化时忽略它）⇒ 新旧版本可以混跑。
    /// </para>
    ///
    /// <para>
    /// 🔸 <b>本批中心只用它显示与提示，<b>不并入</b>活性的生死判据</b>
    /// （<c>RemoteSessionRegistry.Sweep</c>）：同一批里既改协议又改生死，
    /// 出问题就分不清是谁干的。真要让它参与判据，是下一批的独立决定 ——
    /// 那时它已经有真机数据了。
    /// </para>
    /// </summary>
    long? DesktopIdleMs = null);

/// <summary>
/// <b>输入事件的种类</b>（M5.2）。
///
/// <para>
/// 🔥 <b>为什么是"一种事件类型 + 一个 Kind 字符串"，而不是四个子类型</b>：
/// 这条链路上它要走两跳（中心 → 服务 → 托盘管道），而每一跳都要<b>反序列化到具体类型</b>。
/// 用继承层次就必然出现"多态反序列化"这个新的失败点
/// （多一个字段写错、就变成静默的 <c>null</c>，症状是"点了没反应"）。
/// 扁平类型 + 字符串判别式是这里唯一不需要额外机制的做法
/// —— 与 <c>RemoteStreamRequest</c> 是同一个取舍。
/// </para>
///
/// <para>
/// ⚠️ <b>取值与 <c>RemoteSessionActions</c> 同理：两跳共用这一组常量</b>，
/// 各写一组必然漂，而漂掉的表现是"中心说已下发、托盘不认得"。
/// </para>
/// </summary>
public static class RemoteInputKinds
{
    /// <summary>
    /// 指针移动。<see cref="RemoteInputEventDto.X"/>/<see cref="RemoteInputEventDto.Y"/> 是
    /// <b>归一化坐标</b>（0–65535，相对<b>这一路画面</b>，见该类型的说明）。
    /// <para>
    /// 🔥 <b>只有它可以被丢弃</b>：鼠标划过时会以几百赫兹产生事件，
    /// 而人眼只关心"最后停在哪"。所以中心侧对它做<b>合并（只留最新一条）</b>，
    /// 其余三类一律不合并 —— 丢一个"抬起"的后果是那个键在被控端<b>永远卡住</b>。
    /// </para>
    /// </summary>
    public const string PointerMove = "pointer-move";

    /// <summary>指针键按下 / 抬起（<see cref="RemoteInputEventDto.Down"/>）。</summary>
    public const string PointerButton = "pointer-button";

    /// <summary>滚轮。<see cref="RemoteInputEventDto.Delta"/> 正值 = 向上滚。</summary>
    public const string Wheel = "wheel";

    /// <summary>
    /// 键盘按下 / 抬起（<see cref="RemoteInputEventDto.VirtualKey"/>，Windows 虚拟键码）。
    /// <para>
    /// ⚠️ <b>按下与抬起是一对</b>：合并或丢弃其中一条，那个键就会在用户那边"卡住"
    /// （表现为之后所有输入都带着一个莫名其妙的 Ctrl）。所以它<b>绝不合并</b>。
    /// </para>
    /// </summary>
    public const string Key = "key";
}

/// <summary>鼠标键的线上取值（<see cref="RemoteInputKinds.PointerButton"/> 用）。</summary>
public static class RemoteMouseButtons
{
    public const string Left = "left";
    public const string Middle = "middle";
    public const string Right = "right";
}

/// <summary>
/// 输入回传的<b>硬上限</b>（M5.2）。两端共用一组数 —— 各写各的必然漂，
/// 而漂掉的表现是"中心放行了一批、被控端认为超限"（或反过来）。
/// </summary>
public static class RemoteInputLimits
{
    /// <summary>
    /// 单批最多多少条事件。
    ///
    /// <para>
    /// 🔥 <b>它挡的不是"正常的鼠标拖动"</b>（一次拖动通常是几十条），
    /// 而是"一个坏掉的脚本一次塞十万条"——那会同时打爆中心的内存与被控端的注入循环，
    /// 而表现是"远控一开，被控机就卡死"，看起来像远控本身有问题。
    /// </para>
    /// <para>超限的处理是<b>拒绝整批</b>（不是截断）：截断会让"按下"留下、"抬起"被砍掉。</para>
    /// </summary>
    public const int MaxEventsPerBatch = 256;
}

/// <summary>
/// <b>一条输入事件</b>（M5.2，浏览器 → 中心 → 客户机 → 托盘）。
///
/// <para>
/// 🔥 <b>坐标为什么是"归一化 0–65535"而不是像素</b>：
/// </para>
/// <list type="bullet">
///   <item>观看端可能用 <c>fit</c>（缩放过）或 <c>1:1</c>（带滚动条）两种视口显示，
///         两者算出来的像素完全不同，而在前端做这个换算是<b>最容易错且最难查</b>的一处
///         —— 错了的表现是"鼠标整体偏一点"或"缩放比例不对时越点越偏"；</item>
///   <item>被控端的分辨率可能<b>大于观看端的显示器</b>（方案 §0.4：被控端最高 8K，
///         而看的人可能只有 1080p），像素坐标在这里根本没法表达"画面的 60% 处"；</item>
///   <item>0–65535 是 Windows <c>SendInput</c> 自己的坐标约定
///         （<c>MOUSEEVENTF_ABSOLUTE</c> 就是这个量程），
///         于是<b>最后一跳不需要做任何换算</b> —— 少一次换算就少一类偏差。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>归一化的基准是"这一路画面的矩形"</b>，由<b>托盘</b>换算成虚拟桌面像素。
/// 这样无论是抓单块屏还是抓虚拟桌面，<c>0</c> 永远等于"画面最左/最上"、
/// <c>65535</c> 等于"最右/最下" —— 而这件事<b>只有托盘知道</b>
/// （它才知道这一路抓的是哪块屏、那块屏在虚拟桌面里的偏移）。
/// 把这个换算放在中心或前端，多屏机器上必然整体偏移。
/// </para>
///
/// <para>
/// ⚠️ <b>它刻意是一个"宽类型"</b>（每种 Kind 只用其中几个字段）：
/// 拆成四个子类型会引入多态反序列化，而收益为零 —— 这里没有第三方消费者。
/// </para>
/// </summary>
/// <param name="X">
/// 归一化横坐标（0–65535，相对这一路画面）。仅 <see cref="RemoteInputKinds.PointerMove"/> 用。
/// </param>
/// <param name="Y">归一化纵坐标（0–65535）。仅 <see cref="RemoteInputKinds.PointerMove"/> 用。</param>
/// <param name="Button">见 <see cref="RemoteMouseButtons"/>。仅 <see cref="RemoteInputKinds.PointerButton"/> 用。</param>
/// <param name="Down">按下（真）还是抬起（假）。指针键与键盘都用它。</param>
/// <param name="Delta">滚轮刻度，正值 = 向上。仅 <see cref="RemoteInputKinds.Wheel"/> 用。</param>
/// <param name="VirtualKey">
/// Windows 虚拟键码（如 <c>0x41</c> = A、<c>0x11</c> = Ctrl）。仅 <see cref="RemoteInputKinds.Key"/> 用。
/// </param>
public sealed record RemoteInputEventDto(
    string Kind,
    int X = 0,
    int Y = 0,
    string? Button = null,
    bool Down = false,
    int Delta = 0,
    int VirtualKey = 0);

/// <summary>
/// <b>一批输入事件</b>（M5.2 的传输单位）。
///
/// <para>
/// 🔥 <b>为什么批量而不是一条一个消息</b>：鼠标拖动一次就是几十到几百个移动事件。
/// 一条一个 Hub 调用意味着<b>每个像素移动都走一次完整的 SignalR 往返</b>，
/// 而那会把这条链路的延迟从"毫秒"推到"抖得没法用"。
/// 批量把"一次往返"摊到多条事件上，同时给了中心侧一个天然的顺序单元
/// （同批事件按数组顺序执行 —— 拖拽必须保序，否则会画出乱七八糟的轨迹）。
/// </para>
///
/// <para>⚠️ <b>按键的按下/抬起可能落在不同批次里</b>（人手速有限，一批塞不满）——
/// 所以合批只影响传输效率，不影响语义：链路上<b>不重排</b>，只按到达顺序执行。</para>
/// </summary>
/// <param name="SessionId">
/// 目标会话。⚠️ 复用现有的会话账本做归属校验（会话里就带着机器 Id）——
/// 这比"让调用方再报一次机器 Id"稳：两个字段迟早会说出不一样的话。
/// </param>
/// <param name="Events">事件数组，按顺序执行。空数组 = 什么都不做（但不报错）。</param>
public sealed record RemoteInputBatchDto(
    string SessionId,
    IReadOnlyList<RemoteInputEventDto> Events);

/// <summary>
/// 输入回传的结果（M5.2）。
///
/// <para>
/// 🔥 <b>为什么必须有它</b>：输入是<b>唯一一类"发了没反应就等于没发"</b>的动作。
/// 画面卡住时人能看见"卡住"，而按下一个键之后<b>什么都没发生</b> ——
/// 到底是"键没送到"、"被控端拒了"、还是"送到了但那个键在那台机器上没用"，
/// 没有这个回执就完全分不出来。所以即使它是"尽力而为"的通道，
/// 也必须让调用方知道这一次的结论。
/// </para>
/// </summary>
/// <param name="Ok">真 = 这批事件已经交给下一跳（<b>不保证被控端已经作用于桌面</b>）。</param>
/// <param name="Accepted">实际下发了多少条。</param>
/// <param name="Coalesced">
/// 被<b>合并</b>掉的条数（只有 <see cref="RemoteInputKinds.PointerMove"/> 会被合并）。
/// <para>它与"被拒绝"是两回事：合并是设计如此，不是故障。</para>
/// </param>
public sealed record RemoteInputAckDto(
    bool Ok,
    string? Code,
    string? Message,
    int Accepted,
    int Coalesced = 0);

/// <summary>
/// HTTP 入口（<c>POST /api/remote/sessions/{id}/input</c>）的请求体。
///
/// <para>
/// 🔥 <b>它刻意不带 <c>SessionId</c></b>：会话 Id 在 URL 里。
/// 让同一个事实有两个来源（路径 + 请求体），迟早会出现两者不一致的请求 ——
/// 而那时"按哪个走"就成了每次都要重新想一遍的问题。
/// 校验的归属也因此清清楚楚：<b>路径说了算</b>。
/// </para>
///
/// <para>⚠️ 观看页面<b>不走这个端点</b>（它走 Hub）：这里是给自动化脚本用的低频入口。</para>
/// </summary>
public sealed record RemoteInputRequest(IReadOnlyList<RemoteInputEventDto> Events);

/// <summary>
/// <b>允许 / 禁止输入回传</b>（M5.2）。
/// <para>
/// ⚠️ 它与 <see cref="RemoteGrayscaleRequest"/> 的差别很关键：灰度是"改了之后<b>我们让它改</b>"，
/// 而它是<b>一道闸门</b> —— 关掉它之后，中心<b>根本不会</b>把输入转发出去。
/// 所以"关"是一个可靠的结论（中心自己就能保证），不需要被控端配合。
/// </para>
/// </summary>
public sealed record RemoteInputToggleDto(bool Enabled);

/// <summary>
/// <b>被控端的输入通道状态</b>（M5.2）。
///
/// <para>
/// 🔥 <b>三态是刻意的，不许压成 bool</b>：把 <c>connecting</c> 与 <c>unavailable</c>
/// 压成同一个 <c>false</c>，页面就只能说"不可用" —— 而"正在连"的时候
/// 用户最该做的是<b>等一秒</b>，不是去查客户端版本。看错这一眼的代价是
/// 一台好好的机器被重新装了客户端。
/// </para>
///
/// <para>
/// ⚠️ 这三个值是<b>被控端说的</b>，不是中心推的：只有它知道自己的托盘有没有
/// 把那条管道架起来。中心侧那个"允许控制"的开关是另一件事（见
/// <c>RemoteSessionDto.InputEnabled</c>）—— 两者独立，缺任何一个都点不动。
/// </para>
/// </summary>
public static class AgentInputStates
{
    /// <summary>正在接入（会话刚起，管道还没接上）。</summary>
    public const string Connecting = "connecting";

    /// <summary>输入管道已接上，随时可以注入。</summary>
    public const string Ready = "ready";

    /// <summary>
    /// <b>确定接不上</b>：托盘没建这条管道（客户端版本过旧）、
    /// 或本平台没有输入通道（非 Windows）、或接入超时。
    /// <para>它一定带原因 —— 走的是<b>专门那个</b> <c>InputReason</c> 字段，
    /// <b>不是</b>会话的 <c>Reason</c>（那是"这路会话为什么结束"）。见
    /// <see cref="RemoteChannelStates"/>。</para>
    /// </summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// <b>通道状态（输入 / 音频）与它"为什么不可用"之间唯一的一条规矩。</b>
///
/// <para>
/// 规矩只有一句：<b>原因只在 <c>unavailable</c> 时有值，其余状态一律清掉</b>。
/// 就绪之后还留着上一次的失败原因，页面上会写成
/// "通道就绪：本机已禁止远程输入" —— 一句自相矛盾、却看起来很像回事的话。
/// </para>
///
/// <para>
/// ⚠️ <b>为什么值得为这一句话单开一个类</b>：这条规矩在本方案里被抄了 <b>5 遍</b>
/// （被控端 <c>SetInputState</c> / <c>SetAudioState</c>、托盘的 <c>SetAudioState</c>、
/// 中心 <c>Entry.ApplyState</c> 里的输入与音频各一处）。抄 5 遍的规则，
/// 第 6 处一定会写错，而写错的表现是"页面上有个看起来很正常的原因" ——
/// 没人会觉得那是个 bug。抽成纯函数之后，它才能被用例钉住。
/// </para>
///
/// <para>
/// 🔥 还有一层：<b>它与"会话为什么结束"是两件事</b>。在它出现之前，输入的原因
/// 是搭会话的 <c>Reason</c> 来回传的，两者恰好在"会话正常结束、而输入本来就不通"
/// 时撞在一起 —— 页面上于是写着「输入通道不可用：已在本机取消推流」，
/// 而真正的原因（"本机已禁止远程输入"）被顶掉了：
/// 一句看起来很像回事、却<b>指向完全不同处置方法</b>的话。
/// </para>
/// </summary>
public static class RemoteChannelStates
{
    /// <summary>
    /// 该保留的原因：<paramref name="state"/> 恰好是"不可用"时返回
    /// <paramref name="reason"/>，其余情况一律返回 <c>null</c>。
    /// </summary>
    /// <param name="state">通道状态（输入看 <see cref="AgentInputStates"/>，音频看 <see cref="AgentAudioStates"/>）。</param>
    /// <param name="unavailable">
    /// 该通道"不可用"那个取值。两个域现在都写作 <c>"unavailable"</c>，
    /// 但<b>不许在这里写死那个字面量</b>：哪天音频多出一态（如 <c>error</c>），
    /// 要改的就只有传参这一处，而不是每一处等着被想起来的地方。
    /// </param>
    /// <param name="reason">被控端给出的人话原因（要直接上页面，所以必须是用户能读懂的句子）。</param>
    public static string? ReasonToKeep(string? state, string unavailable, string? reason)
        => string.Equals(state, unavailable, StringComparison.Ordinal) ? reason : null;
}

/// <summary>
/// 功能尚未接入时的统一回应体（M5.0 骨架用）。
/// <para>
/// 它存在的意义是<b>让"没实现"看起来像"没实现"</b>，而不是
/// 返回一个 200 空对象让人以为"成功但没数据"。前者一眼能看出进度，
/// 后者会让现场白等。
/// </para>
/// </summary>
public sealed record RemoteNotAvailableDto(string Code, string Message);

/// <summary>黑白模式开关（决策 14）。</summary>
/// <param name="Enabled">真 = 开。</param>
/// <remarks>
/// ⚠️ <b>它是会话级参数，不是观看端私有的</b>：编码只做一路，
/// 所以一个人切黑白，<b>所有</b>看这路流的人都会变灰。
/// 页面上必须提示这一点，否则会被当成 bug（"我没点它怎么变灰了"）。
/// </remarks>
public sealed record RemoteGrayscaleRequest(bool Enabled);

/// <summary>远程桌面相关的机器可读错误码。</summary>
public static class RemoteErrorCodes
{
    /// <summary>
    /// 该功能尚未接入。<b>M5.0 骨架期用它回答"还没做"</b>，M5.2 起不再有调用点。
    ///
    /// <para>
    /// ⚠️ <b>刻意保留而不是删掉</b>：它是这个协议里唯一一个"我们承认自己没做"的码，
    /// 而 M5.3–M5.5（音频 / 硬编 / 多屏）还会再出现骨架期。
    /// 删掉它，那段时期的代码就会开始拿别的东西顶包（历史上就发生过：
    /// "请求体为空"曾经借用它，而那是<b>两句完全不同的话</b>）。
    /// </para>
    /// </summary>
    public const string NotImplemented = "remote-not-implemented";

    /// <summary>
    /// 请求体不符合约定（空体 / 缺字段 / 畸形）。
    ///
    /// <para>
    /// 🔥 <b>为什么单独要有它</b>：这类失败在页面上最容易被说错 ——
    /// 曾经"请求体为空"借用了 <see cref="NotImplemented"/>（看起来像"功能没做"），
    /// 后来又差点借用 <see cref="InputNotEnabled"/>（看起来像"闸门没开"）。
    /// 而这两句都会把现场引向<b>完全错误的方向</b>：一个去等版本，一个去点开关，
    /// 而真正要做的只是"把请求发对"。
    /// </para>
    /// </summary>
    public const string BadRequest = "remote-bad-request";

    /// <summary>找不到该会话。</summary>
    public const string SessionNotFound = "remote-session-not-found";

    /// <summary>在线表里没有这台机器。</summary>
    public const string AgentNotFound = "remote-agent-not-found";

    /// <summary>
    /// 机器在在线表里，但<b>此刻没有长连接</b> —— 指令发不出去。
    /// <para>
    /// 🔥 与 <see cref="AgentNotFound"/> 分开：前者是"没这台机器"（去装 Agent），
    /// 后者是"有这台机器但它掉线了"（等它重连）。合并成一句"客户端不存在"，
    /// 现场会去重装一个本来好好的客户端。
    /// </para>
    /// </summary>
    public const string AgentOffline = "remote-agent-offline";

    /// <summary>
    /// 被控端<b>会话侧不在</b>（无人登录 / 托盘没跑）。
    /// <para>
    /// 🔥 这是远控最常见、也最容易被误判成"软件坏了"的一种失败：
    /// 抓屏与注入都只能在用户会话里做，而没人登录就没有那个会话。
    /// 所以它必须有自己的错误码，并且文案要直说"需要有人登录"。
    /// </para>
    /// </summary>
    public const string SessionSideUnavailable = "remote-session-side-unavailable";

    /// <summary>被控端有会话侧，但抓屏/编码起不来（无桌面、编码器不可用等）。</summary>
    public const string StartFailed = "remote-start-failed";

    /// <summary>这台机器上已经有一个未结束的会话（M5.1 只允许一路）。</summary>
    public const string SessionBusy = "remote-session-busy";

    /// <summary>
    /// <b>中心的总量配额用完了</b>（M5.5 并发准入）。
    ///
    /// <para>
    /// 🔥 <b>它与 <see cref="SessionBusy"/> 是两句完全不同的话</b>：
    /// 那个是"<b>这一台</b>机器已经在推了"（去结束那一路），
    /// 这个是"<b>全中心</b>已经到量了"（去结束别的机器上的一路，或者等别人结束）。
    /// 合并成一句"已有会话在推流"，现场会一直盯着这台机器找那路会话 —— 而它不存在。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>为什么不干脆不限</b>：每路 4K 硬编都要吃一台机器的编码引擎，
    /// 而所有路共用<b>千兆</b>。不设总量时第一次事故的形态是
    /// "第 5 路开了之后，<b>前 4 路一起变卡</b>" —— 现场只会看到"远控变卡了"，
    /// 没人会想到"是因为多开了一路"。配额存在的意义是把这个因果**在入口处**说清楚。
    /// </para>
    /// </summary>
    public const string QuotaExceeded = "remote-quota-exceeded";

    /// <summary>
    /// <b>这一路会话没有打开输入回传</b>（M5.2）。
    ///
    /// <para>
    /// 🔥 <b>它与"没权限"必须分开报</b>：没权限是"去找管理员要角色"，
    /// 而这个开关就在页面上、自己就能打开。合并成一句"无权操作"，
    /// 现场会去找一个本来就有权限的管理员 —— 而问题其实是"那个开关没点"。
    /// </para>
    /// </summary>
    public const string InputNotEnabled = "remote-input-not-enabled";

    /// <summary>
    /// 这一路会话不允许输入（被控端侧不接受，或平台不支持注入）。
    /// <para>与 <see cref="InputNotEnabled"/> 分开：那个是"你没打开"，这个是"打开了也做不了"。</para>
    /// </summary>
    public const string InputUnavailable = "remote-input-unavailable";

    /// <summary>一批输入事件过大（防"一次塞十万条把队列打爆"）。</summary>
    public const string InputTooLarge = "remote-input-too-large";

    /// <summary>
    /// <b>这条连接当前没有在观看这一路</b>（M5.2 的归属校验）。
    ///
    /// <para>
    /// 🔥 <b>它守的是一件很具体的事</b>：输入是"照着画面点"的动作 ——
    /// 没有在收这一路画面的连接，送进来的坐标没有任何意义，
    /// 而它多半意味着"某个早已被遗忘的页面还在发东西"
    /// （刷新过、切走过、或者 <c>Leave</c> 那次调用在路上丢了）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>与 <see cref="SessionNotFound"/> 必须分开</b>：
    /// 那一个是"会话已经没了"（去刷新列表），
    /// 这一个是"会话还在、只是你这条连接不在看它"（去重新打开这一路）。
    /// 两者曾经共用同一个码，而它们的处置完全不搭界。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>它也<b>不是</b>"控制权被抢了"</b>。本方案<b>没有</b>独占控制权这个概念：
    /// 多人同时看一路时，<b>任何一个正在看这一路的人</b>都有资格操作 ——
    /// 这是刻意的（见 <c>RemoteSessionDto.InputEnabled</c> 的注释：
    /// 开关是<b>会话级</b>的，一个人打开，所有正在看的人都获得能力）。
    /// 所以看到这个码，要做的动作只有一个：<b>把这一路重新点开</b>。
    /// </para>
    /// </summary>
    public const string NotWatching = "remote-not-watching";

    /// <summary>
    /// <b>编码格式协商失败</b>：观看端与被控端<b>各报了一份</b>能力，而它们没有交集（M5.4-b 第 7 步）。
    ///
    /// <para>
    /// 🔥 <b>它与 <see cref="StartFailed"/> 必须分开</b>：那一个是"指令发出去了、被控端起不来"
    /// （去查那台机器：没人登录 / 抓屏被占 / 编码器坏了），
    /// 这一个是<b>在建会话之前就已经定下来的、确定做不到</b>（去换浏览器或换机器）。
    /// 合并成"启动失败"会让现场去折腾一台本来好好的机器。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>只有"两边都清楚地报过能力、且交集为空"才会用它</b>。
    /// 任何一侧"不知道"（没上报 / 没探测）一律<b>回落</b>，不是这个码 ——
    /// "没测出来" ≠ "不支持"（见 <see cref="RemoteVideoNegotiation.Decide"/>）。
    /// </para>
    /// </summary>
    public const string CodecNotNegotiable = "remote-codec-not-negotiable";
}

// ═══════════════════════════════════════════════════════════════
// M5.5：多屏 / 剪贴板 / 质量档位
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// <b>一块可抓的屏</b>（M5.5 多屏选择）。
///
/// <para>
/// 🔥 <b>为什么必须有 <c>IsVirtualDesktop</c></b>：拼屏那一项<b>不是一块屏</b>，
/// 它是"把所有 output 拼成一张大图"。它的可行性取决于抓屏后端
/// （GDI 能整块 BitBlt，DXGI **不能** —— 一个 duplication 只对一个 output），
/// 所以它必须能和真实显示器区分开，页面上才说得清"为什么选了它却回落到了 MJPEG"。
/// </para>
///
/// <para>
/// ⚠️ <c>Id</c> 是<b>抓屏器认的那个 Id</b>（不是显示器序号）：它直接进
/// <c>RemoteSessionRequest.ScreenId</c>，两边只认这一个约定。
/// </para>
/// </summary>
/// <param name="Name">给人看的名字（"显示器 2" / "全部显示器（拼接）"）。</param>
public sealed record RemoteScreenDto(
    string Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    bool IsVirtualDesktop);

/// <summary>
/// 屏清单指令的占位会话 Id。
///
/// <para>
/// ⚠️ <b>为什么需要它</b>：<see cref="RemoteSessionCommandDto.SessionId"/> 是<b>非空</b>字段
/// （每条远控指令都作用在某一路会话上），而"给我看看你有几块屏"这件事
/// **不针对任何一路会话**。用空串会被客户机的通用校验当成"缺字段"挡下来，
/// 所以只能给一个明确的占位值 —— 它同时也是一个可搜索的日志标记：
/// 日志里见到它，就知道这条指令是屏清单。
/// </para>
/// </summary>
public static class RemoteScreenRequests
{
    /// <summary>屏清单指令的会话 Id 占位值（客户机不拿它去开任何东西）。</summary>
    public const string NoSessionId = "(screens)";
}

/// <summary>
/// <b>被控机的屏清单</b>（托盘 → 服务 → 中心的上行载荷）。
/// </summary>
/// <param name="Screens">屏列表（至少一项；空列表说明会话里没有桌面）。</param>
/// <param name="Note">
/// 给人看的一句话。<c>null</c> = 没什么可说的。
/// <para>⚠️ 典型内容是"这台机器只有一块屏，因此没有拼接项" ——
/// 空白列表会让人以为是"功能坏了"。</para>
/// </param>
/// <param name="ReportedAt">采集时刻（北京时间）。</param>
public sealed record AgentScreensDto(
    IReadOnlyList<RemoteScreenDto> Screens,
    string? Note,
    DateTimeOffset ReportedAt);

/// <summary>
/// <b>剪贴板读写的动作名</b>（<see cref="RemoteSessionActions.Clipboard"/> 的载荷里用）。
/// </summary>
public static class RemoteClipboardActions
{
    /// <summary>把被控机的剪贴板读到观看端。</summary>
    public const string Get = "get";

    /// <summary>把观看端的一段文字写进被控机的剪贴板。</summary>
    public const string Set = "set";
}

/// <summary>剪贴板同步的硬限制。<b>三端共用这一份</b>。</summary>
public static class RemoteClipboardLimits
{
    /// <summary>
    /// 单次同步的最大字符数（<b>2 万字</b>）。
    ///
    /// <para>
    /// 🔥 <b>为什么是"字符"而不是"字节"</b>：要传的是<b>文本</b>（UTF-16 字符串），
    /// 按字节截断会把一个汉字/emoji 切一半 —— 而半个代理对在 JSON 里是
    /// 非法字符，序列化那一步就会抛。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 2 万字 ≈ 60 KB（SignalR 一条消息 8 MiB 的千分之七），
    /// 远超"复制一段代码 / 一个地址"的日常用法；而<b>刻意不传文件</b>
    /// （图片、文件列表一律拒绝并说明原因），因为那条路要的是文件传输，不是剪贴板。
    /// </para>
    /// </summary>
    public const int MaxChars = 20_000;
}

/// <summary>
/// <b>一段剪贴板内容</b>（M5.5）。
/// </summary>
/// <param name="Text"><c>null</c> = 被控机剪贴板里没有文本（有图片 / 空 / 被别的程序锁着）。</param>
/// <param name="Note">
/// 给人看的一句话。<c>null</c> = 正常。
/// <para>⚠️ "剪贴板里没有文本"与"读失败"是两件事：前者 <c>Text = null</c> 且
/// <c>Note</c> 为空，后者 <c>Note</c> 有原因。</para>
/// </param>
/// <param name="At">读取时刻（北京时间）。</param>
/// <param name="Truncated">是否因为超过 <see cref="RemoteClipboardLimits.MaxChars"/> 被截断。</param>
public sealed record RemoteClipboardDto(
    string? Text,
    string? Note = null,
    DateTimeOffset? At = null,
    bool Truncated = false);

/// <summary>写剪贴板的请求体。</summary>
public sealed record RemoteClipboardRequest(string? Text);

/// <summary>
/// <b>中心侧看到的屏清单</b>（M5.5）。它是 <see cref="AgentScreensDto"/> 的"对外表述"，
/// 多了一个"正在问、还没回来"的状态。
/// </summary>
/// <param name="Screens">已知的屏列表（<see cref="Pending"/> 为真时可能是空的）。</param>
/// <param name="Pending">
/// <b>刚向客户机发出了索取指令，清单还没回来</b>。
/// <para>⚠️ 它与"问了、答了、一块屏都没有"是两件事：后者 <c>Pending = false</c> 且
/// <c>Screens</c> 为空 —— 那说明那台机器<b>真的没有可抓的显示器</b>（没人登录）。</para>
/// </param>
/// <param name="Note">给人看的一句话。</param>
/// <param name="ReportedAt">这份清单是什么时候采的（<c>null</c> = 从没采到过）。</param>
public sealed record RemoteScreensDto(
    IReadOnlyList<RemoteScreenDto> Screens,
    bool Pending,
    string? Note,
    DateTimeOffset? ReportedAt);

/// <summary>改质量档位的请求体（M5.5）。</summary>
/// <param name="Profile">档位名（见 <see cref="RemoteQualityProfiles"/>）；<c>null</c> = 回到 <c>auto</c>。</param>
/// <param name="MaxFps">
/// 帧率硬上限（可选）。与档位推荐值取小 —— 与建会话时同一条折法：
/// 否则"低画质"可能按 30 fps 在推。
/// </param>
public sealed record RemoteQualityRequest(string? Profile, int? MaxFps = null);

/// <summary>
/// <b>把视口挪到这一块</b>（M5.5 第 5 项：ROI 编码）。
///
/// <para>
/// 坐标是<b>画面像素</b>（不是 CSS 像素、不是缩放后的）：页面在 <c>1:1</c> 模式下
/// 把 <c>scrollLeft/scrollTop</c> 直接填进来即可，在 <c>等比缩放</c> 模式下
/// <b>不该发</b>这个请求 —— 那种模式下用户没有"视口"这个概念（整幅都在屏幕上）。
/// </para>
///
/// <para>
/// ⚠️ <b>宽或高为 0 = 关掉 ROI</b>。用一个"独立的关"标志会更清楚，
/// 但那意味着线上多一个"可能是 null"的字段，而"宽高为 0 就是没有视口"
/// 本来就没有第二种解释。
/// </para>
/// </summary>
public sealed record RemoteViewportRequest(int X, int Y, int Width, int Height);

