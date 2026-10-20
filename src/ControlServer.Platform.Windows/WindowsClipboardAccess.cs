using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 剪贴板的文本读写（M5.5）。
///
/// <para>
/// 🔥 <b>为什么每一次操作都带重试</b>：Windows 剪贴板是<b>全局独占</b>资源 ——
/// 别的程序（输入法、剪贴板管理器、浏览器）在某一瞬间持有它时，<c>OpenClipboard</c>
/// 会直接失败。这不是故障，是常态。第一次失败就报"读不到剪贴板"，
/// 现场会以为是权限问题（然后在 UAC / 会话隔离上白查半天）。
/// 实测 5 × 20 ms 足以跨过绝大多数争用。
/// </para>
///
/// <para>
/// ⚠️ <b>只处理 <c>CF_UNICODETEXT</c></b>：见 <see cref="IClipboardAccess"/> 的说明 ——
/// 图片/文件列表不属于这条通道。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardAccess : IClipboardAccess
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int Attempts = 5;
    private const int RetryDelayMs = 20;

    /// <inheritdoc />
    public string? ReadText(out string? note)
    {
        note = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                if (attempt == Attempts)
                {
                    note = $"剪贴板被别的程序占着，连续 {Attempts} 次都打不开"
                           + "（输入法 / 剪贴板管理器持有它时很常见，稍后再试即可）。";
                    return null;
                }

                Thread.Sleep(RetryDelayMs);
                continue;
            }

            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);

                // ⚠️ 句柄为 0 **不是错误**：说明剪贴板里此刻没有文本
                //    （有图片 / 空 / 只有私有格式）。所以这里返回 null 且 note 为 null。
                if (handle == IntPtr.Zero) return null;

                var pointer = GlobalLock(handle);

                if (pointer == IntPtr.Zero)
                {
                    note = "拿到了剪贴板句柄，但锁定内存失败（少见，通常是别的进程正在写）。";
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool WriteText(string text, out string? note)
    {
        note = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                if (attempt == Attempts)
                {
                    note = $"剪贴板被别的程序占着，连续 {Attempts} 次都打不开 —— 没有写入。";
                    return false;
                }

                Thread.Sleep(RetryDelayMs);
                continue;
            }

            IntPtr block = IntPtr.Zero;

            try
            {
                if (!EmptyClipboard())
                {
                    note = "清空剪贴板失败（通常是别的进程在同时写）。";
                    return false;
                }

                // ⚠️ 必须用 GMEM_MOVEABLE 的全局内存块交给系统：CF_UNICODETEXT
                //    的约定是"系统接管这块内存"，用栈/托管内存会当场崩。
                //    字符串长度 +1 是结尾的那个 '\0'。
                var bytes = (text.Length + 1) * sizeof(char);

                block = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);

                if (block == IntPtr.Zero)
                {
                    note = $"分配 {bytes} 字节的剪贴板内存失败（内存不足？）。";
                    return false;
                }

                var pointer = GlobalLock(block);

                if (pointer == IntPtr.Zero)
                {
                    note = "锁定剪贴板内存失败。";
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                    Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
                }
                finally
                {
                    GlobalUnlock(block);
                }

                if (SetClipboardData(CF_UNICODETEXT, block) == IntPtr.Zero)
                {
                    note = "把文本交给系统失败（剪贴板在这一瞬间被抢走了）。";
                    return false;
                }

                // 🔥 交接成功之后这块内存归系统所有，**不许**再 GlobalFree ——
                //    双释放会把宿主进程直接干掉（而且症状像"随机崩溃"）。
                block = IntPtr.Zero;

                return true;
            }
            finally
            {
                CloseClipboard();

                if (block != IntPtr.Zero) GlobalFree(block);
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
