using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 视频码流用例的<b>素材与工具</b>。
///
/// <para>
/// 🔥 <b>真实素材（不是手搓的）</b>：下面那两组 H.264 参数集是从本机 Intel QSV 硬编
/// 落盘的真码流里原样抠出来的 ——
/// 来源 <c>.tools/m5.4-capture/capture-H264.h264</c>（2160×1440，M5.4-a 的硬编冒烟用例产出）。
/// 抠法就是"找 <c>00 00 01</c> 起始码，取 <c>nal[0] &amp; 0x1F</c> 为 7 / 8 的两个 NAL"。
/// </para>
/// <para>
/// ⚠️ <b>刻意不用"测试运行时去读那个文件"</b>：那个文件是硬编用例<b>每次跑都会覆盖</b>的产物，
/// 拿它当输入会让这条用例的结果取决于"这次跑的时候屏幕上有什么" ——
/// 于是"改了代码"与"换了壁纸"表现一样。
/// </para>
/// </summary>
internal static class VideoTestVectors
{
    /// <summary>真机 H.264 SPS（25 字节）。<c>profile_idc = 0x64</c>（High）、<c>level_idc = 0x32</c>（5.0）。</summary>
    internal static readonly byte[] RealH264Sps =
    [
        0x27, 0x64, 0x00, 0x32, 0xAC, 0x2B, 0x40, 0x10, 0xE0, 0x5A, 0xD8, 0x08, 0x80,
        0x00, 0x00, 0x03, 0x00, 0x80, 0x00, 0x00, 0x1E, 0x46, 0xD0, 0xE1, 0x95,
    ];

    /// <summary>真机 H.264 PPS（4 字节）。</summary>
    internal static readonly byte[] RealH264Pps = [0x28, 0xEE, 0x3C, 0xB0];

    /// <summary>真机 H.264 访问单元分隔符（2 字节）。</summary>
    internal static readonly byte[] RealH264Aud = [0x09, 0x10];

    /// <summary>真机 IDR 片的前 16 字节（只取片头，用例不需要那 229 KB 的载荷）。</summary>
    internal static readonly byte[] RealH264IdrHead =
    [
        0x25, 0x88, 0x80, 0x47, 0xFF, 0xEC, 0x85, 0x12,
        0xD6, 0x0D, 0xFE, 0x0B, 0x39, 0x60, 0x53, 0x54,
    ];

    /// <summary>
    /// 按 Annex-B 拼一段码流：每个 NAL 前面加 <c>00 00 00 01</c>。
    /// 真机文件就是这样开头的（第一个起始码是 4 字节的 <c>00 00 00 01</c>）。
    /// </summary>
    internal static byte[] AnnexB(params byte[][] nalUnits)
    {
        var total = nalUnits.Sum(static n => 4 + n.Length);
        var buffer = new byte[total];
        var cursor = 0;

        foreach (var nal in nalUnits)
        {
            buffer[cursor++] = 0x00;
            buffer[cursor++] = 0x00;
            buffer[cursor++] = 0x00;
            buffer[cursor++] = 0x01;
            nal.CopyTo(buffer, cursor);
            cursor += nal.Length;
        }

        return buffer;
    }

    /// <summary>把一段字节按 <c>xx xx xx</c> 打出来（断言失败时看得懂）。</summary>
    internal static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// 按 H.265 语法逐位写一个 SPS。
    ///
    /// <para>
    /// ⚠️ <b>为什么 HEVC 这边只能合成、不能像 H.264 那样抠真机字节</b>：
    /// 本机这轮落盘的是 H.264 码流，没有 HEVC 的真实 SPS 可用；
    /// 而 HEVC 的 <c>profile_tier_level()</c> 是<b>位对齐不是字节对齐</b>的，
    /// 手工凑十六进制比按语法写更容易错。⇒ 这里的素材是<b>按规格合成</b>的，
    /// 只能证明"我们的解析器对得上规格"，<b>不能</b>证明"对得上真机编码器" ——
    /// 后者要等真机 HEVC 码流复验（已记进方案的挂账）。
    /// </para>
    /// </summary>
    internal static byte[] HevcSps(
        byte profileSpace,
        bool tierFlag,
        byte profileIdc,
        uint compatibilityFlags,
        ulong constraintFlags,
        byte levelIdc,
        byte chromaFormatIdc = 1,
        uint bitDepthLumaMinus8 = 0,
        uint bitDepthChromaMinus8 = 0,
        byte maxSubLayersMinus1 = 0,
        bool temporalIdNesting = true)
    {
        var writer = new HevcBitWriter();

        writer.WriteBits(0, 4);                       // sps_video_parameter_set_id
        writer.WriteBits(maxSubLayersMinus1, 3);      // sps_max_sub_layers_minus1
        writer.WriteFlag(temporalIdNesting);          // sps_temporal_id_nesting_flag

        writer.WriteBits(profileSpace, 2);
        writer.WriteFlag(tierFlag);
        writer.WriteBits(profileIdc, 5);
        writer.WriteBits(compatibilityFlags, 32);
        writer.WriteBits((uint)(constraintFlags >> 16), 32);
        writer.WriteBits((uint)(constraintFlags & 0xFFFF), 16);
        writer.WriteBits(levelIdc, 8);

        // 🔥 这一段是"profile_tier_level 会变长"的来源。**必须照语法写全**：
        //    少写这一截，解析器就会把后面的位当成子层信息读走，
        //    于是色度/位深读出来的是一串"合法但错"的值 —— 用例会以一种很难懂的方式红。
        if (maxSubLayersMinus1 > 0)
        {
            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                writer.WriteFlag(true);      // sub_layer_profile_present_flag
                writer.WriteFlag(true);      // sub_layer_level_present_flag
            }

            for (var i = maxSubLayersMinus1; i < 8; i++) writer.WriteBits(0, 2);   // reserved_zero_2bits

            for (var i = 0; i < maxSubLayersMinus1; i++)
            {
                writer.WriteZeros(88);       // sub_layer_profile_tier_level
                writer.WriteBits(0, 8);      // sub_layer_level_idc
            }
        }

        writer.WriteUe(0);                            // sps_seq_parameter_set_id
        writer.WriteUe(chromaFormatIdc);
        if (chromaFormatIdc == 3) writer.WriteFlag(false);   // separate_colour_plane_flag
        writer.WriteUe(1920);                         // pic_width_in_luma_samples
        writer.WriteUe(1080);                         // pic_height_in_luma_samples
        writer.WriteFlag(false);                      // conformance_window_flag
        writer.WriteUe(bitDepthLumaMinus8);
        writer.WriteUe(bitDepthChromaMinus8);
        writer.WriteUe(0);                            // log2_max_pic_order_cnt_lsb_minus4

        return [0x42, 0x01, .. writer.Finish()];      // NAL 头：type 33 = SPS
    }

    /// <summary>H.265 的 VPS / PPS 只用类型，内容我们用不到（hvcC 只原样搬运）。</summary>
    internal static byte[] HevcFiller(int nalUnitType, params byte[] payload)
        => [(byte)(nalUnitType << 1), 0x01, .. payload];

    /// <summary>MSB 在前的位写入器（<b>仅测试用</b>）。</summary>
    private sealed class HevcBitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitsInCurrentByte;

        internal void WriteFlag(bool value) => WriteBits(value ? 1u : 0u, 1);

        internal void WriteBits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--) WriteBit(((value >> i) & 1u) != 0);
        }

        /// <summary>写一串 0 位（<c>WriteBits(0, 88)</c> 会踩到位移只取低 5 位的坑）。</summary>
        internal void WriteZeros(int count)
        {
            for (var i = 0; i < count; i++) WriteBit(false);
        }

        /// <summary><c>ue(v)</c>：<c>value + 1</c> 的二进制长度减一那么多个 0，然后是它本身。</summary>
        internal void WriteUe(uint value)
        {
            var code = value + 1;
            var length = 0;

            while ((code >> length) != 0) length++;

            for (var i = 0; i < length - 1; i++) WriteBit(false);

            for (var i = length - 1; i >= 0; i--) WriteBit(((code >> i) & 1u) != 0);
        }

        internal byte[] Finish()
        {
            WriteBit(true);                                  // rbsp_stop_one_bit（按规格总是要写）

            while (_bitsInCurrentByte != 0) WriteBit(false);

            return [.. _bytes];
        }

        private void WriteBit(bool value)
        {
            if (_bitsInCurrentByte == 0) _bytes.Add(0);

            if (value) _bytes[^1] |= (byte)(1 << (7 - _bitsInCurrentByte));

            _bitsInCurrentByte = (_bitsInCurrentByte + 1) % 8;
        }
    }
}
