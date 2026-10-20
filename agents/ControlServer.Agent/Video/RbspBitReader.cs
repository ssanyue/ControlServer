namespace ControlServer.Agent.Video;

/// <summary>
/// <b>RBSP 位读取器</b>：按 ITU-T 的 <c>u(n)</c> / <c>ue(v)</c> 语法从 NAL 载荷里读数，
/// 并在读的过程中自动跳过 emulation prevention 字节。
///
/// <para>
/// 🔴🔴 <b>"该不该跳过这个 <c>0x03</c>" 必须看<b>输入</b>里它前面的两个字节，不能看已经解出来的输出。</b>
/// 编码器是在源数据里出现 <c>00 00</c> 之后插入 <c>0x03</c>，而"已经解出来的输出"在跳过之后
/// 也同样以 <c>00 00</c> 结尾 —— 两种判据在源数据为 <c>00 00 03</c>（转义后 <c>00 00 03 03</c>）时结论相反：
/// 看输出会把第二个 <c>03</c> 也当转义字节删掉，解出 <c>00 00</c> 而不是 <c>00 00 03</c>。
/// 症状是 SPS 解到一半开始错，而且<b>不一定立刻失败</b> —— 可能只是 profile/level 读成另一个合法值。
/// </para>
/// <para>输入是<b>已剥掉 NAL 头</b>（H.264 1 字节 / H.265 2 字节）的载荷。</para>
/// </summary>
internal ref struct RbspBitReader
{
    private readonly ReadOnlySpan<byte> _rbsp;
    private int _byteIndex;
    private int _bitIndex;

    internal RbspBitReader(ReadOnlySpan<byte> nalPayloadWithoutHeader)
    {
        _rbsp = nalPayloadWithoutHeader;
        _byteIndex = 0;
        _bitIndex = 0;
        ConsumedBits = 0;
    }

    /// <summary>已经读掉的位数（只用于写错误原因，不参与任何判断）。</summary>
    internal int ConsumedBits { get; private set; }

    /// <summary>读 <paramref name="count"/> 位（1–32），<b>高位在前</b>。</summary>
    internal bool TryReadBits(int count, out uint value)
    {
        value = 0;

        if (count is < 0 or > 32) return false;

        for (var i = 0; i < count; i++)
        {
            if (_bitIndex == 0 && !TryAdvanceByte()) return false;

            value = (value << 1) | ((uint)(_rbsp[_byteIndex] >> (7 - _bitIndex)) & 1u);

            _bitIndex++;
            ConsumedBits++;

            if (_bitIndex == 8)
            {
                _bitIndex = 0;
                _byteIndex++;
            }
        }

        return true;
    }

    /// <summary>读 1 位当布尔。</summary>
    internal bool TryReadFlag(out bool value)
    {
        if (!TryReadBits(1, out var bit))
        {
            value = false;
            return false;
        }

        value = bit != 0;
        return true;
    }

    /// <summary>
    /// 读一个无符号 Exp-Golomb 码 <c>ue(v)</c>（H.264 9.1 / H.265 9.2）。
    ///
    /// <para>
    /// ⚠️ 前端长度超过 31 位时<b>返回 false 而不是回绕</b>：那个值一定超出 <c>uint</c>，
    /// 而我们读的每个字段（chroma_format_idc、bit_depth_*_minus8 等）都远小于 2^31。
    /// 回绕会让一个坏掉的 SPS 读出一串"合法但错"的值，比直接失败坏得多。
    /// </para>
    /// </summary>
    internal bool TryReadUe(out uint value)
    {
        value = 0;

        var leadingZeros = 0;

        while (true)
        {
            if (!TryReadBits(1, out var bit)) return false;

            if (bit == 1) break;

            leadingZeros++;

            if (leadingZeros > 31) return false;
        }

        if (leadingZeros == 0) return true;

        if (!TryReadBits(leadingZeros, out var rest)) return false;

        var total = ((1UL << leadingZeros) - 1UL) + rest;

        if (total > uint.MaxValue) return false;

        value = (uint)total;
        return true;
    }

    /// <summary>跳过若干位（用于 <c>profile_tier_level</c> 里那些我们不解释的字段）。</summary>
    internal bool TrySkipBits(int count)
    {
        while (count > 0)
        {
            var step = Math.Min(count, 32);

            if (!TryReadBits(step, out _)) return false;

            count -= step;
        }

        return true;
    }

    /// <summary>
    /// 前进到下一个<b>有效</b>字节，跳过 emulation prevention 字节。
    /// <para>判据是<b>输入</b>里它前面两个字节都是 <c>0x00</c>（见类型注释里那个反例）。</para>
    /// </summary>
    private bool TryAdvanceByte()
    {
        while (true)
        {
            if (_byteIndex >= _rbsp.Length) return false;

            if (_rbsp[_byteIndex] == 0x03
                && _byteIndex >= 2
                && _rbsp[_byteIndex - 1] == 0x00
                && _rbsp[_byteIndex - 2] == 0x00)
            {
                _byteIndex++;
                continue;
            }

            return true;
        }
    }
}
