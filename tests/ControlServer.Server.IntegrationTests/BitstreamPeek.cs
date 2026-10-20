namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 从一段<b>码流</b>里"瞄一眼"用的极简扫描 —— 给日志与用例断言，<b>不是生产代码</b>。
///
/// <para>
/// ⚠️ <b>它刻意与真正那份分开</b>：项目里真正的 Annex-B 解析在
/// <c>ControlServer.Agent.Video.AnnexB</c>（带尾部填充零剥离、带长度前缀转换、带 RBSP 位读取），
/// 但那是 <c>internal</c> 且本测试工程<b>看不到</b> —— 为了打印一行字去开
/// <c>InternalsVisibleTo</c>，会把"Agent 的内部实现"变成两个测试工程都能碰的东西。
/// ⇒ 这里只保留"够用且不会说谎"的那一点：<b>认出起始码、读一个 NAL 类型</b>。
/// </para>
///
/// <para>
/// 🔥 <b>两个编码的 NAL 类型取法不同</b>，所以本类的名字里必须带上格式：
/// H.264 是低 5 位，H.265 是<b>中间</b>的 6 位（<c>(b >> 1) &amp; 0x3F</c>）。
/// 写成同一个"通用取法"，症状是 H.265 的类型全部读错 —— 而它只会让日志更难看，
/// 不会让任何用例失败，于是没人会发现。
/// </para>
///
/// <para>
/// ⚠️ 本类<b>只服务测试</b>，因此用最直白的写法（逐字节比较），不追求零分配。
/// </para>
/// </summary>
internal static class BitstreamPeek
{
    /// <summary>首帧是不是 Annex-B（<c>00 00 00 01</c> 或 <c>00 00 01</c> 起始码）。</summary>
    internal static bool LooksLikeAnnexB(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;

        return (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            || (data[0] == 0 && data[1] == 0 && data[2] == 1);
    }

    /// <summary>
    /// 扫出一段 Annex-B 访问单元里所有 NAL 的类型（<b>H.264</b> 的取法：低 5 位）。
    ///
    /// <para>
    /// ⚠️ 只认 <c>00 00 01</c>（三字节起始码），不处理"<c>00 00 00 00 01</c>"这类
    /// 带额外填充零的写法。对 MF 编出来的码流足够 —— 它服务的是"给人看的一行字"，
    /// <b>不是判据本身</b>。
    /// </para>
    /// </summary>
    internal static List<int> H264NalTypes(ReadOnlySpan<byte> data)
    {
        var types = new List<int>();
        var cursor = 0;

        while (cursor + 3 < data.Length)
        {
            if (data[cursor] != 0 || data[cursor + 1] != 0 || data[cursor + 2] != 1)
            {
                cursor++;
                continue;
            }

            cursor += 3;

            types.Add(data[cursor] & 0x1F);
        }

        return types;
    }

    /// <summary>H.264 的 NAL 类型名（只列本方案会碰到的那些，其余按数字报出来 —— <b>不猜</b>）。</summary>
    internal static string H264NalName(int type) => type switch
    {
        1 => "非IDR片",
        5 => "IDR片",
        6 => "SEI",
        7 => "SPS",
        8 => "PPS",
        9 => "AUD",
        _ => $"类型{type}",
    };
}
