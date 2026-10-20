using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Video;

/// <summary>
/// <b>Annex-B</b>（起始码分隔）码流的解析与改写 —— H.264 / H.265 通用。
///
/// <para>
/// 🔥 <b>为什么这段纯字节处理非要单独写出来</b>：被控端的 MF 硬编<b>默认按 Annex-B 输出</b>
/// （参数集混在码流里），而 WebCodecs 的 <c>VideoDecoderConfig.description</c> 要的是
/// <b>AVCDecoderConfigurationRecord</b>（H.265 是 <c>HEVCDecoderConfigurationRecord</c>），
/// 并且<b>一旦给了 description，码流就必须换成"长度前缀"</b>（ISO 14496-15 的 canonical 形式
/// = <c>avc</c> / <c>hevc</c> 格式）。两者都不是 Annex-B —— 直接塞进去不会报错，
/// 只会 <c>configure()</c> 失败或者解出一片花屏。
/// </para>
///
/// <para>
/// ⚠️ <b>本文件的方法只在"带外"打包时才会被用到</b>（M5.4-b 拍板：<b>默认带内</b>，
/// 带外是留的开关）。带内那条路对码流<b>一个字节都不动</b>，所以热路径上不会走到这里。
/// 那句"MF 会把参数集混在码流里"在 M5.4-a 第 4 步有直接证据：
/// <c>MF_MT_MPEG_SEQUENCE_HEADER</c> 读回 <b>36 字节</b>（含 3 字节起始码），即参数集
/// 既在码流里、也在这条属性上 —— 两条路都拿得到。
/// 而"<b>每个关键帧都会重复一遍</b>"也已实测成立（§3.4.2 两个关键帧；
/// 本轮 <c>gopSize = 4</c> 补到 <b>8/8</b>），这正是带内能当默认的前提。
/// </para>
///
/// <para>
/// ⚠️ <b>扫起始码是安全的</b>：RBSP 里的 <c>00 00 00/01/02/03</c> 会被编码器转义成
/// <c>00 00 03 xx</c>（emulation prevention，H.264 7.4.1.1 / H.265 7.4.2.1），
/// 所以 <c>00 00 01</c> <b>不可能</b>出现在一个 NAL 的内部。
/// </para>
/// </summary>
internal static class AnnexB
{
    /// <summary>起始码本身（<c>zero_byte?</c> + <c>start_code_prefix_one_3bytes</c>）里那个固定部分。</summary>
    private const int StartCodeLength = 3;

    /// <summary>
    /// 依次枚举 Annex-B 里的 NAL 单元。
    /// <para>
    /// <see cref="Current"/> <b>不含起始码</b>，且已剥掉 NAL 尾部的 <c>trailing_zero_8bits</c>。
    /// 剥尾部零字节是安全的：<c>rbsp_trailing_bits</c> 保证最后一个字节里一定有一位是 1，
    /// 所以"结尾是 0x00"只能是填充，不可能是 NAL 的内容。
    /// </para>
    /// </summary>
    internal ref struct Enumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _cursor;

        internal Enumerator(ReadOnlySpan<byte> annexB)
        {
            _data = annexB;
            _cursor = 0;
            Current = default;
        }

        /// <summary>当前 NAL（不含起始码、不含尾部填充零）。</summary>
        internal ReadOnlySpan<byte> Current { get; private set; }

        internal bool MoveNext()
        {
            while (true)
            {
                if (!TryFindStartCode(_data, _cursor, out var start))
                {
                    Current = default;
                    return false;
                }

                var nalStart = start + StartCodeLength;
                var nalEnd = TryFindStartCode(_data, nalStart, out var next) ? next : _data.Length;

                while (nalEnd > nalStart && _data[nalEnd - 1] == 0) nalEnd--;

                _cursor = nalEnd;

                if (nalEnd > nalStart)
                {
                    Current = _data[nalStart..nalEnd];
                    return true;
                }

                // 空 NAL（两个起始码贴在一起）没有任何语义，跳过它而不是当成一个元素交出去。
                // ⚠️ 这里必须继续循环：`_cursor` 已经被推到 nalEnd（> start），所以不会原地打转。
            }
        }
    }

    /// <summary>
    /// 统计"换成长度前缀形式"需要多少字节。
    /// <para>每个保留下来的 NAL 前加 <b>4 字节大端长度</b>（与 <c>lengthSizeMinusOne = 3</c> 对应）。</para>
    /// </summary>
    /// <param name="includeParameterSets">
    /// 是否保留参数集。走 <c>description</c> 那条路时必须是 <c>false</c>：
    /// 规格里写明"<c>description</c> 在时，参数集<b>不</b>出现在码流里"，
    /// 把它们留在每一帧里等于每帧白发几十字节，而且在"参数集也变了"的场景下会与
    /// <c>description</c> 里那份产生两种说法。
    /// </param>
    internal static int MeasureCanonicalAccessUnit(
        ReadOnlySpan<byte> annexB,
        VideoCodec codec,
        bool includeParameterSets)
    {
        var total = 0;
        var reader = new Enumerator(annexB);

        while (reader.MoveNext())
        {
            if (!includeParameterSets
                && NalUnitTypes.IsParameterSet(codec, NalUnitTypes.Read(codec, reader.Current)))
            {
                continue;
            }

            total += 4 + reader.Current.Length;
        }

        return total;
    }

    /// <summary>
    /// 写出一份<b>长度前缀</b>（canonical / <c>avc</c> / <c>hevc</c> 形式）的访问单元。
    /// </summary>
    /// <returns>写进去的字节数；<paramref name="destination"/> 不够大时返回 <c>-1</c>（<b>不抛异常</b>）。</returns>
    /// <remarks>
    /// 参数集<b>保留原样</b>（含 emulation prevention 字节）—— 长度前缀只是换封装，
    /// 不动载荷。别"顺手"去剥 <c>00 00 03</c>：那是 RBSP 层的事，剥了就把码流改坏了。
    /// </remarks>
    internal static int WriteCanonicalAccessUnit(
        ReadOnlySpan<byte> annexB,
        VideoCodec codec,
        bool includeParameterSets,
        Span<byte> destination)
    {
        var written = 0;
        var reader = new Enumerator(annexB);

        while (reader.MoveNext())
        {
            var nal = reader.Current;

            if (!includeParameterSets
                && NalUnitTypes.IsParameterSet(codec, NalUnitTypes.Read(codec, nal)))
            {
                continue;
            }

            if (written + 4 + nal.Length > destination.Length) return -1;

            var length = (uint)nal.Length;

            destination[written++] = (byte)(length >> 24);
            destination[written++] = (byte)(length >> 16);
            destination[written++] = (byte)(length >> 8);
            destination[written++] = (byte)length;

            nal.CopyTo(destination[written..]);
            written += nal.Length;
        }

        return written;
    }

    /// <summary>
    /// 找下一个 <c>00 00 01</c>，返回<b>第一个零字节</b>的下标。
    /// <para>
    /// 刻意<b>不区分 3 字节与 4 字节起始码</b>：多出来的那个 <c>zero_byte</c> 是填充，
    /// 分成两种情况只会多一个没人读的分支。需要它的地方（例如逐字节复现原码流）现在没有。
    /// </para>
    /// </summary>
    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int from, out int index)
    {
        for (var i = from < 0 ? 0 : from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }
}
