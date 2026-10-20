using System.Runtime.InteropServices;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 用 <b>Media Foundation</b> 列出本机<b>真正存在</b>的硬件编码器（M5.0 第二批）。
///
/// <para>
/// 🔥 <b>它解决的是"这台机器到底有没有硬编"这个唯一可靠的问题</b>。
/// 在此之前方案里打算用 PDH 的 <c>engtype_VideoEncode</c> 实例数去数引擎 ——
/// 2026-10-15 在本机实测后否掉了那条路：
/// </para>
/// <list type="bullet">
///   <item><c>\GPU Engine(*)\Utilization Percentage</c> 的**全部实例都带 <c>pid_</c> 前缀**
///         （本机 231 条），即它按"<b>哪个进程用过哪个引擎</b>"创建实例 ——
///         <b>没有编码活动就没有 VideoEncode 实例</b>，一台空转的 4090 也会报出 0 条。
///         拿它当"引擎数"会<b>把"没用过"说成"没有"</b>，而那正是本方案最不能犯的错。</item>
///   <item>而 <c>MFTEnumEx</c> 查的是<b>注册表里注册了哪些硬件编码器</b>，与当前有没有活动无关。</item>
/// </list>
///
/// <para>
/// <b>本机实测（Intel UHD 620 + NVIDIA MX250/GP108）</b>：
/// </para>
/// <code>
/// H.264  硬编 → "Intel® Quick Sync Video H.264 Encoder MFT"（未出现任何 NVIDIA 条目）
/// HEVC   硬编 → "Intel® Hardware H265 Encoder MFT"
/// H.264  全部 → "H264 Encoder MFT"（微软**软编**，被 HARDWARE 过滤掉）
/// HEVC   全部 → 0 个   ← 连软编都没有（与"Chromium 无 HEVC 软解"同源：专利）
/// </code>
/// <para>
/// ⚠️ <b>返回的条目数不是引擎数</b>：本机实测两台编码器各返回 <b>2</b> 条<b>同名</b>记录
/// （驱动按多种设备型态各注册一份）。把 2 当"双引擎"会得出"这台机器能 8K60"的假结论。
/// 引擎数只能另找依据（见 <see cref="GpuEncoderEngineCounter"/> 与 <c>EngineBasis</c>）。
/// </para>
/// </summary>
internal static class MediaFoundationEncoders
{
    /// <summary>一条<b>已注册的硬件编码器</b>。</summary>
    /// <param name="Codec">它编哪种格式。</param>
    /// <param name="FriendlyName">驱动注册的显示名（含厂商名，用来归属到具体适配器）。</param>
    internal sealed record HardwareEncoder(VideoCodec Codec, string FriendlyName);

    /// <summary>
    /// <c>MFT_FRIENDLY_NAME_Attribute</c>。
    /// <para>⚠️ 它<b>不在</b> <see cref="MediaFoundationInterop"/> 里 —— 那边是 M5.4 管线要用的那批，
    /// 这个只有"枚举给人看"时才用到，所以留在自己的消费者旁边。</para>
    /// </summary>
    private static readonly Guid FriendlyNameKey = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");

    /// <summary>
    /// 🔥 <b>同一批 MF 工厂函数的声明在 <see cref="MediaFoundationInterop"/> 里</b>，这里只是回头去用。
    /// 原先本类自己声明了一份 <c>MFStartup</c> / <c>MFTEnumEx</c>，而 M5.4 的编码器要用同两个函数 ⇒
    /// 收敛到一处（同一条规矩不抄两遍）。
    /// </summary>
    private static Guid MajorVideo => MediaFoundationInterop.MFMediaType_Video;

    /// <summary>
    /// 列出本机的硬件编码器。
    ///
    /// <para>
    /// 🔥 <b>返回 <c>null</c> 与返回空表是两件完全不同的事</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>空表</b> = 查询成功了，这台机器<b>确实没有</b>硬件编码器
    ///         ⇒ 调用方可以据此下"引擎数 = 0"的结论；</item>
    ///   <item><b><c>null</c></b> = 查不了（<c>mfplat.dll</c> 缺失 / 调用失败）
    ///         ⇒ 调用方<b>只能填 <c>null</c></b>，绝不能像空表那样被解读成"这台机器不支持"。</item>
    /// </list>
    /// <para>这条区分是整个 M5.0 能力探测的地基，见 <c>EncoderCapabilities</c> 对 <c>null</c> 与 <c>0</c> 的说明。</para>
    /// </summary>
    internal static IReadOnlyList<HardwareEncoder>? TryEnumerateHardware()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            // 返回值不检查：MF 已经初始化过时会返回 S_FALSE，那不是失败。
            MediaFoundationInterop.MFStartup((int)MediaFoundationInterop.MfVersion, 0);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        var h264 = TryEnumerateOne(VideoCodec.H264, MediaFoundationInterop.MFVideoFormat_H264);
        var hevc = TryEnumerateOne(VideoCodec.Hevc, MediaFoundationInterop.MFVideoFormat_HEVC);

        // 两半都失败 ⇒ "查不了"；只要有一半成功，就能给出一个**部分成立**的答案。
        // ⚠️ 但部分成功时不能把失败的那半当成"没有" —— 所以这里只在两半都失败时返回 null，
        //    而单个格式失败会退化成"这个格式没查到"（对 H.264 而言这已经是主要结论了）。
        if (h264 is null && hevc is null) return null;

        var result = new List<HardwareEncoder>();

        if (h264 is not null) result.AddRange(h264);
        if (hevc is not null) result.AddRange(hevc);

        return result;
    }

    private static List<HardwareEncoder>? TryEnumerateOne(VideoCodec codec, Guid subtype)
    {
        var info = new MediaFoundationInterop.MftRegisterTypeInfo { Major = MajorVideo, Sub = subtype };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MediaFoundationInterop.MftRegisterTypeInfo>());

        try
        {
            Marshal.StructureToPtr(info, buffer, false);

            var hr = MediaFoundationInterop.MFTEnumEx(
                MediaFoundationInterop.CategoryVideoEncoder,
                MediaFoundationInterop.EnumFlagHardware,
                IntPtr.Zero,
                buffer,
                out var activates,
                out var count);

            // 非 0 = 这次枚举没成功（不是"没有编码器"）⇒ 交回 null。
            if (hr != 0) return null;

            var found = new List<HardwareEncoder>();

            if (count == 0 || activates == IntPtr.Zero) return found;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);

                    try
                    {
                        var name = TryReadFriendlyName(activate);

                        if (!string.IsNullOrWhiteSpace(name)) found.Add(new HardwareEncoder(codec, name!));
                    }
                    finally
                    {
                        // 每一条都要释放：MFTEnumEx 给的是**已加引用**的 IMFActivate。
                        Marshal.Release(activate);
                    }
                }
            }
            finally
            {
                // ⚠️ 数组本身由 COM 任务分配器给出 ⇒ 只能 CoTaskMemFree，
                //    与上面那个 AllocHGlobal 用的 FreeHGlobal 不是一个堆，别互相替用。
                MediaFoundationInterop.CoTaskMemFree(activates);
            }

            return found;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception)
        {
            // 任何意料之外的失败都算"没查出来"，绝不当成"没有"。
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 读一个 MF 激活对象的显示名。读不到返回 null（调用方会跳过它）。
    /// <para>
    /// 🔥 <c>internal</c> 而不是 <c>private</c>：M5.4 的编码器也要用它把
    /// <b>"这一路码流到底是哪个 MFT 编的"</b>写进报告 —— 现场排障时
    /// "用的是 Intel 硬编还是微软软编"是第一个要问的问题，
    /// 而这两者在日志里除了名字<b>没有任何区别</b>。
    /// </para>
    /// </summary>
    internal static string? TryReadFriendlyName(IntPtr activate)
    {
        try
        {
            var key = FriendlyNameKey;

            // ⚠️ 槽位 13 = IMFAttributes::GetAllocatedString，见 §3.1 那张表。
            //    首次落地时用一次性探针实测过：它返回了可读的
            //    "Intel® Quick Sync Video H.264 Encoder MFT"。
            var fn = ComVtable.Bind<MediaFoundationInterop.Delegates.AttributeGetAllocatedStringFn>(
                activate, MediaFoundationInterop.AttributeSlots.GetAllocatedString);

            if (fn(activate, ref key, out var value, out _) != 0 || value == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUni(value)?.Trim();
            }
            finally
            {
                // GetAllocatedString 的契约是"调用方用 CoTaskMemFree 释放"。
                MediaFoundationInterop.CoTaskMemFree(value);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
