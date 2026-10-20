using System.Buffers.Binary;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>一帧画面的内容指纹</b>（M5.5 挂账：软编路径没有静止检测）。
///
/// <para>
/// <b>它解决什么问题</b>：GPU（DXGI）那条路天然知道"桌面有没有变"—— 桌面没变时
/// DDA 根本不给新帧。而 GDI / X11 抓屏<b>每调一次就返回一整幅</b>，
/// 于是 MJPEG 路径在桌面定格时会一直抓、一直编、一直发：一张 4K 桌面
/// 每秒 12–60 次 JPEG 编码，CPU 白白烧掉，带宽也白占（发的是同一张图）。
/// </para>
///
/// <para>
/// 🔥 <b>为什么用指纹而不是"存一份上一帧逐字节比"</b>：存副本要每帧拷 33 MB（4K BGRA），
/// 那份拷贝本身就和编码差不多贵；而且它是 O(画面大小) 的常驻内存。
/// 指纹只需一次顺序扫描、O(1) 内存。
/// </para>
///
/// <para>
/// 🔥🔥 <b>为什么必须是 64 位、且必须把尺寸混进去</b>：
/// </para>
/// <list type="number">
///   <item>
///     指纹<b>误判"没变"</b>的后果是画面永远停住（用户以为程序卡死），
///     而误判"变了"的后果只是退化成现在的行为（每帧都发）。两者代价差得远，
///     所以这里宁可取一个碰撞概率可忽略的宽度（64 位 FNV-1a）。
///   </item>
///   <item>
///     分辨率变化时缓冲区长度可能<b>恰好相同</b>（例如 100×100 与 200×50 的 BGRA 都是 40000 字节）。
///     不把宽高混进初值，"切了分辨率但画面内容碰巧同构"就会被判成静止 ——
///     而那一刻正是最该重发一帧的时候。
///   </item>
/// </list>
///
/// <para>
/// ⚠️ <b>它不承诺密码学性质</b>：这里只防"两张不同的画面撞成同一个指纹"，
/// 不防有人刻意构造碰撞（没有这个攻击面 —— 输入是本机屏幕）。
/// </para>
/// </summary>
public static class FrameFingerprint
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>算一帧的指纹。</summary>
    public static ulong Of(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return Of(frame.Width, frame.Height, frame.Buffer);
    }

    /// <summary>按"宽高 + 像素"算指纹（供无 <see cref="CapturedFrame"/> 的调用方/用例使用）。</summary>
    public static ulong Of(int width, int height, ReadOnlySpan<byte> pixels)
    {
        var hash = OffsetBasis;

        // 尺寸先混进去（理由见类注释 ②）。
        hash = (hash ^ (uint)width) * Prime;
        hash = (hash ^ (uint)height) * Prime;

        // 8 字节一批：4K 一帧 33 MB，逐字节的循环在这里是真实成本（~30 ms）。
        // 分批之后只剩一次顺序扫描（~5 ms），比一整帧 JPEG 编码便宜一个数量级。
        var i = 0;

        for (; i + sizeof(ulong) <= pixels.Length; i += sizeof(ulong))
        {
            hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(pixels.Slice(i, sizeof(ulong)))) * Prime;
        }

        for (; i < pixels.Length; i++)
        {
            hash = (hash ^ pixels[i]) * Prime;
        }

        return hash;
    }
}
