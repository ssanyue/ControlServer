using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 从嵌入的 <c>.ico</c> 里挑一帧并变成 <c>HICON</c>。
/// <para>
/// <b>为什么不直接用 <c>LoadImage(..., LR_LOADFROMFILE)</c></b>：托盘图标资源是<b>编译期嵌入</b>的
/// （绿色版单文件 exe 落地时磁盘上没有 assets 目录），落地到临时文件再加载既慢又脏。
/// 这里直接解析 ICO 容器、取出目标尺寸那一帧的原始 DIB，交给
/// <c>CreateIconFromResourceEx</c>，全程内存操作。
/// </para>
/// <para>
/// ICO 容器格式：6 字节 ICONDIR + N×(16 字节 ICONDIRENTRY) + N 段图像数据。
/// <c>bWidth/bHeight</c> 为 0 表示 256（这是唯一能表示 256 的方式）。
/// </para>
/// </summary>
internal static class IconLoader
{
    private const int DirSize = 6;
    private const int EntrySize = 16;

    /// <summary>图标在程序集里的资源名（由 csproj 的 <c>LogicalName</c> 固定）。</summary>
    internal const string ResourceName = "ControlServer.Platform.Windows.app.ico";

    /// <summary>
    /// 加载托盘图标。**永不抛异常** —— 拿不到就退回系统默认应用图标，
    /// 因为"托盘图标丑一点"远好过"服务起不来"。
    /// </summary>
    /// <param name="desiredSize">期望边长（通常取 <c>SM_CXSMICON</c>，即 16×DPI 缩放）。</param>
    internal static IntPtr Load(int desiredSize)
    {
        try
        {
            var bytes = ReadEmbedded();
            if (bytes is not null)
            {
                var hIcon = FromIcoBytes(bytes, desiredSize);
                if (hIcon != IntPtr.Zero) return hIcon;
            }
        }
        catch
        {
            // 落到系统默认图标。
        }

        return NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

    internal static byte[]? ReadEmbedded()
    {
        var assembly = typeof(IconLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>列出 ICO 各帧的边长（0 归一化为 256）。非法输入返回空列表。</summary>
    internal static IReadOnlyList<int> DescribeFrames(byte[] ico)
    {
        var result = new List<int>();

        if (ico.Length < DirSize) return result;
        if (BitConverter.ToUInt16(ico, 0) != 0) return result;   // idReserved 必须为 0
        if (BitConverter.ToUInt16(ico, 2) != 1) return result;   // idType：1 = 图标

        var count = BitConverter.ToUInt16(ico, 4);
        if (ico.Length < DirSize + count * EntrySize) return result;

        for (var i = 0; i < count; i++)
        {
            var width = ico[DirSize + i * EntrySize];
            result.Add(width == 0 ? 256 : width);
        }

        return result;
    }

    /// <summary>
    /// 选帧规则（**纯函数，可单测**）：
    /// 优先"<b>不超过期望尺寸里最大的</b>"那一帧；若所有帧都比期望大，则取<b>最小的</b>（缩放损耗最小）。
    /// 返回下标；没有可选帧时返回 -1。
    /// </summary>
    internal static int PickBestIndex(IReadOnlyList<int> frameSizes, int desiredSize)
    {
        if (frameSizes.Count == 0) return -1;

        var best = -1;
        var bestSize = -1;

        for (var i = 0; i < frameSizes.Count; i++)
        {
            var size = frameSizes[i];
            if (size > desiredSize) continue;
            if (best == -1 || size > bestSize)
            {
                best = i;
                bestSize = size;
            }
        }

        if (best >= 0) return best;

        for (var i = 0; i < frameSizes.Count; i++)
        {
            if (best == -1 || frameSizes[i] < bestSize)
            {
                best = i;
                bestSize = frameSizes[i];
            }
        }

        return best;
    }

    /// <summary>用 ICO 字节建 HICON；任何结构异常/建图失败都返回 <see cref="IntPtr.Zero"/>。</summary>
    internal static IntPtr FromIcoBytes(byte[] ico, int desiredSize)
    {
        var frames = DescribeFrames(ico);
        var index = PickBestIndex(frames, desiredSize);
        if (index < 0) return IntPtr.Zero;

        var entryOffset = DirSize + index * EntrySize;
        var imageSize = (int)BitConverter.ToUInt32(ico, entryOffset + 8);
        var imageOffset = (int)BitConverter.ToUInt32(ico, entryOffset + 12);

        if (imageSize <= 0) return IntPtr.Zero;
        if ((long)imageOffset + imageSize > ico.Length) return IntPtr.Zero;

        var payload = new byte[imageSize];
        Buffer.BlockCopy(ico, imageOffset, payload, 0, imageSize);

        var hIcon = NativeMethods.CreateIconFromResourceEx(
            payload, (uint)payload.Length,
            fIcon: true, dwVer: NativeMethods.ICON_VERSION_3,
            cxDesired: desiredSize, cyDesired: desiredSize, flags: 0);

        if (hIcon != IntPtr.Zero) return hIcon;

        // CreateIconFromResourceEx 对某些 ICO 变体挑食（如 PNG 压缩帧）：
        // 退一步按"原始尺寸"再试一次（cx/cy 传 0）。
        return NativeMethods.CreateIconFromResourceEx(
            payload, (uint)payload.Length,
            fIcon: true, dwVer: NativeMethods.ICON_VERSION_3,
            cxDesired: 0, cyDesired: 0, flags: 0);
    }
}
