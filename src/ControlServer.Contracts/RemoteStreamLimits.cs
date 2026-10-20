namespace ControlServer.Contracts;

/// <summary>
/// <b>远控码流这条链路上「一条消息 / 一片 / 一帧能装多大」的唯一口径。</b>
///
/// <para>
/// 🔥 <b>为什么它必须是一组共享常量，而不是各处各写一个数字</b>：
/// 这条链路上有<b>三道</b>尺寸闸门，而它们分居三个地方：
/// </para>
/// <list type="number">
///   <item><b>被控端托盘</b>在把一帧写进本地管道之前自己判一次（<c>RemoteStreamProtocol.WriteFrameAsync</c>），
///         拒绝的方式是<b>丢这一帧</b>；</item>
///   <item><b>被控端服务</b>在把一帧推给中心之前，决定「整帧发」还是「切成片发」
///         （<c>RemoteFrameUploader</c>），切片的粒度是 <see cref="MaxChunkPayloadBytes"/>；</item>
///   <item><b>中心</b>那台 SignalR 服务在反序列化时判一次
///         （<c>Program.cs</c> 的 <c>MaximumReceiveMessageSize</c>），
///         而它的拒绝方式是<b>直接掐断整条连接</b>。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>三者的失败代价差了两个数量级，所以这些数绝不能各写一份。</b>
/// 一旦漂了，方向只有一种：<b>客户机以为自己还能发，中心却已经把连接掐了</b> ——
/// 而且掐的不是"这一帧"，是<b>整条 Hub 连接</b>（心跳、遥测、配置、音频全在它上面）。
/// 现场表现是"看着看着整个客户机掉线了"，真因却只是某一帧大了几十 KB。
/// 这与 M5.4-a 那条「<c>MFSetAttributeSize</c> 打包反了、零错误码」属于同一类病：
/// <b>形状错了，但没有任何一层会报错。</b>
/// </para>
///
/// <para>
/// 🔸 <b>M5.4-c① 起这里从「两个数」变成「三个数」</b>，因为这条链路上多了一层：
/// 一帧不再必须塞进一条消息，它可以<b>切成若干片（chunk）分多条消息送</b>，
/// 到中心再拼回整帧。于是：
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="SignalRMessageBytes"/></term>
///     <description><b>中心单条消息</b>的上限。<b>没变</b>（8 MiB）—— 分片的全部意义
///     就是让每一片都远小于它，而不是把它抬高。</description>
///   </item>
///   <item>
///     <term><see cref="MaxChunkPayloadBytes"/></term>
///     <description><b>一片</b>的裸字节上限。<b>由它推出来</b>（消息上限扣掉 base64 的 +33%
///     与 JSON 外壳）。⚠️ 它的<b>取值与 M5.4-b 的 <c>MaxFramePayloadBytes</c> 完全相同</b>，
///     改的只是名字 —— 理由见它自己的注释。</description>
///   </item>
///   <item>
///     <term><see cref="MaxFrameBytes"/></term>
///     <description><b>一帧</b>的裸字节上限（防呆）。8K（7680×4320）按 M5.4-a 实测的
///     2160×1440 IDR 306.9 KB 外推，再留一个数量级余量 ⇒ <b>64 MiB</b>，最坏切成 12 片。</description>
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>三个数仍然只有一个根</b>（<see cref="SignalRMessageBytes"/> 那条），
/// 这正是原注释要守的东西 —— "别让帧大到撞穿第 3 跳那道墙"这句话，
/// 分片之后由 <see cref="MaxChunkPayloadBytes"/>（单片）继续守。
/// </para>
///
/// <para>
/// 🔥 <b>本类只有"多大"，没有"怎么切"</b>：把一帧真的折叠成几片是
/// <see cref="RemoteFrameChunker"/> 的事（被控端切片与用例共用那一处实现）。
/// 两者刻意分开放，为的是让这里一个字节的字面量都不出现
/// —— 三个数只有 <see cref="SignalRMessageBytes"/> 一个根。
/// </para>
///
/// <para>
/// 🔴 <b>8K 真机端到端仍然没被验证过</b>：本机显示器只有 2160×1440。
/// 分片做完了，但"8K 达不达标"仍是一条挂账 —— <b>不许把"分片机制跑通"说成"8K 跑通"</b>。
/// </para>
/// </summary>
public static class RemoteStreamLimits
{
    /// <summary>
    /// 中心 SignalR 的<b>单条消息</b>上限（<c>HubOptions.MaximumReceiveMessageSize</c>）。
    ///
    /// <para>
    /// 🔥 <b>它由 <c>Program.cs</c> 直接引用</b>（不是抄一份），所以"中心那边到底是多少"
    /// 只有一个答案。改动它会同时改变中心的行为与下面两个数的推导结果
    /// —— 这正是我们要的耦合。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是<b>整条连接</b>的上限，音频也走同一条连接（音频一包几百字节，远够不着）。
    /// <b>分片之后它依然不许抬高</b>：抬它等于让"一帧畸形"就能吃掉几十 MB 内存，
    /// 而分片要的正是"把压力摊平成多条小消息"。
    /// </para>
    /// </summary>
    public const int SignalRMessageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// <b>一片（chunk）</b>裸码流的上限 —— 保证它经 base64 之后仍塞得进一条消息。
    ///
    /// <para>
    /// 🔥 <b>它是"推"出来的，不是拍的</b>：中心那条消息里装的不是裸字节，
    /// 而是 JSON 里 base64 过的字符串（<b>4 字符/3 字节，+33%</b>）。
    /// 所以一片的上限 = 消息上限 ÷ 4 × 3，再<b>扣掉 1/32</b> 留给帧头、
    /// JSON 字段名与包了一层的外壳。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>它<b>曾经</b>叫 <c>MaxFramePayloadBytes</c>，M5.4-c① 改成了现在这个名字。</b>
    /// 值一个字节都没动，改的是<b>它现在管"一片"而不是"一帧"</b>。
    /// 为什么改名比"再加一个"好：<b>名字错了比数字错了更难查</b> ——
    /// 下一个人会拿一个叫 <c>MaxFrame…</c> 的常量去当帧上限用，
    /// 而那样写出来的代码在 2160×1440 上跑得完全正常，只在 8K 上静默丢帧。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不许把它写成字面量</b>：一旦中心的那个数变了而这里没跟着变，
    /// 结果就是上面说的"客户机还在发、中心已经掐了"。
    /// </para>
    /// </summary>
    public const int MaxChunkPayloadBytes =
        (SignalRMessageBytes / 4 * 3)      // 去掉 base64 的膨胀
        - (SignalRMessageBytes / 32);      // 给帧头、JSON 外壳留一点

    /// <summary>
    /// <b>一帧</b>裸码流的上限（被控端写管道之前按它判，超了丢这一帧）。
    ///
    /// <para>
    /// 🔥 <b>它的身份在 M5.4-c① 变了</b>：原来这条链路上只有"单片"这一个概念，
    /// 所以那个数同时兼任"帧上限"。现在一帧可以切成多条消息送
    /// ⇒ 帧上限与片上限是<b>两件事</b>，而帧上限变成了纯粹的<b>防呆</b>：
    /// 挡住"畸形 / 异常大的帧"把中心的重组缓冲撑爆，而不是"挡住它撞穿某一道墙"。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>64 MiB 是怎么来的</b>：8K（7680×4320）的 IDR 量级，
    /// 按 M5.4-a 实测的 2160×1440 IDR <b>306.9 KB</b> 与像素数比例外推
    /// （8K 的像素是它的 10.7 倍 ⇒ 约 3.3 MB，关键帧还会随内容浮动），
    /// 再留一个数量级的余量 ⇒ 64 MiB 是"够用且绝不会误伤真帧"的值。
    /// 按 <see cref="MaxChunkPayloadBytes"/> 一片折算：**最坏 12 片**。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它与 <see cref="MaxChunkPayloadBytes"/> 的关系不是"大一点小一点"，而是两道不同的闸门</b>：
    /// 片上限守的是"这一条 SignalR 消息装得下吗"（破了 ⇒ 掐整条连接）；
    /// 帧上限守的是"这个数合理吗"（破了 ⇒ 丢这一帧并置"请补关键帧"）。
    /// </para>
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 一帧最多允许切成多少片（<b>防御性上限，不是容量规划</b>）。
    ///
    /// <para>
    /// 🔥 真实的最大片数是算出来的：<c>ceil(MaxFrameBytes / MaxChunkPayloadBytes) = 12</c>。
    /// 这个 1024 只是让"对方报了一个荒唐的 <c>ChunkCount</c>"有机会被挡住
    /// —— 没有它，中心会按对方说的数去预分配/等待，而那个数可能是 <c>int.MaxValue</c>。
    /// </para>
    ///
    /// <para>⚠️ 两端共用它：被控端切片时不许超过，中心收片时超过就<b>丢帧 + 计数</b>（绝不抛）。</para>
    /// </summary>
    public const int MaxChunkCount = 1024;

    /// <summary>把字节数写成"X.X MB / KB"的人话（日志与报错共用，免得各写一套单位换算）。</summary>
    public static string Describe(int bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:F1} MB"
            : $"{bytes / 1024} KB";
}
