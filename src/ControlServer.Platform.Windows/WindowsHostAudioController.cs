using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 音量控制（M4.5），走 <b>CoreAudio</b> 的 <c>IAudioEndpointVolume</c>。
///
/// <para><b>为什么是 CoreAudio 而不是那些看起来更省事的办法</b>：</para>
/// <list type="bullet">
///   <item><c>waveOutSetVolume</c>（winmm）改的是<b>旧版波形输出</b>的音量，
///         现代应用走 WASAPI 会话，多半<b>完全不受影响</b> —— 典型的"命令成功、声音没变"。</item>
///   <item>发送 <c>VK_VOLUME_UP</c> 键盘事件：需要目标窗口在前台，
///         而托盘进程根本没有前台窗口（甚至可能被别的窗口挡住）⇒ 时灵时不灵。</item>
///   <item>CoreAudio 的 <c>VolumeStepUp/Down</c> 是<b>系统自己那套</b>的步进，
///         与用户按键盘音量键完全等价 —— 这是唯一"所见即所得"的做法。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>没有音频输出设备的机器上（常见于虚拟机 / 无声音的工控机）</b>
/// <c>GetDefaultAudioEndpoint</c> 会返回 <c>E_NOTFOUND</c>。
/// 这时 <see cref="IsAvailable"/> 为假、<see cref="AvailabilityNote"/> 给出人话原因，
/// 而不是抛一个 <c>COMException</c> 上去把整条指令链打断。
/// </para>
///
/// <para>
/// <b>线程模型</b>：每次操作独立地 <c>CoCreateInstance</c> 一遍（不缓存端点 COM 对象）。
/// 音频设备会被插拔、会换默认端点，缓存一个"上次拿到的"对象很快就会指向不存在的设备；
/// 而音量指令是低频操作，重建的开销完全不值得优化。
/// 这段代码跑在用户会话里（托盘的线程池线程，MTA），因此拿到的就是用户桌面的默认端点。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostAudioController : IAudioController
{
    /// <summary>CLSCTX_INPROC_SERVER：CoreAudio 的实现是进程内 COM 组件。</summary>
    private const int ClsCtxInProcServer = 0x1;

    /// <summary>EDataFlow.eRender：输出设备。</summary>
    private const int Render = 0;

    /// <summary>ERole.eMultimedia：多媒体角色（用户按音量键改的就是它）。</summary>
    private const int Multimedia = 1;

    /// <summary>HRESULT E_NOTFOUND：没有该类设备。</summary>
    private const int ENotFound = unchecked((int)0x80070490);

    /// <inheritdoc />
    /// <remarks>
    /// 🔥 <b>不是简单的 <c>OperatingSystem.IsWindows()</c></b>：没有音频输出设备的机器
    /// （虚拟机 / 无声卡的工控机 / 声卡被禁用）上"支持音量控制"这句话是**假的**，
    /// 报 true 会让上层的提示词从"本机没有可用的音频输出设备"退化成一句空话。
    /// 代价是每次询问都真的去取一次端点 —— 调用点只有"托盘启动时显示一行"与"收到音量指令时"，
    /// 频率低到不值得为它做缓存（而缓存端点对象的坏处见类注释）。
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;

            var endpoint = TryGetEndpoint(out _);

            if (endpoint is null) return false;

            Release(endpoint);

            return true;
        }
    }

    /// <inheritdoc />
    public string AvailabilityNote
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "当前平台未实现音量控制（Windows 客户端先行）。";

            var endpoint = TryGetEndpoint(out var error);

            if (endpoint is null) return error ?? "拿不到默认音频输出设备。";

            Release(endpoint);

            return string.Empty;
        }
    }

    /// <inheritdoc />
    public HostAudioState Read()
    {
        if (!OperatingSystem.IsWindows()) return HostAudioState.Unknown;

        var endpoint = TryGetEndpoint(out _);

        if (endpoint is null) return HostAudioState.Unknown;

        try
        {
            return ReadState(endpoint);
        }
        finally
        {
            Release(endpoint);
        }
    }

    /// <inheritdoc />
    public HostAudioResult Apply(HostAudioAction action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return HostAudioResult.Failure("当前平台未实现音量控制（Windows 客户端先行）。");
        }

        var endpoint = TryGetEndpoint(out var error);

        if (endpoint is null) return HostAudioResult.Failure(error ?? "拿不到默认音频输出设备。");

        try
        {
            var empty = Guid.Empty;

            var hr = action switch
            {
                HostAudioAction.VolumeUp => endpoint.VolumeStepUp(ref empty),
                HostAudioAction.VolumeDown => endpoint.VolumeStepDown(ref empty),
                HostAudioAction.Mute => endpoint.SetMute(true, ref empty),
                HostAudioAction.Unmute => endpoint.SetMute(false, ref empty),

                // 枚举穷尽；走到这里说明以后加了新动作却忘了在这里实现。
                _ => unchecked((int)0x80070057 /* E_INVALIDARG */),
            };

            if (hr < 0)
            {
                return HostAudioResult.Failure($"系统拒绝了这次音量操作（0x{hr:X8}）。");
            }

            var state = ReadState(endpoint);

            return HostAudioResult.Success($"{Describe(action)}：{state.Describe()}", state);
        }
        catch (Exception ex)
        {
            // COM 设备被拔掉 / 音频服务重启时的常见形态：不抛上去，转成一句可读的失败。
            return HostAudioResult.Failure($"音量操作失败：{ex.Message}");
        }
        finally
        {
            Release(endpoint);
        }
    }

    /// <summary>
    /// 取默认音频输出端点的音量接口。<b>任何失败都返回 null 并给出人话原因</b>。
    /// </summary>
    private static IAudioEndpointVolume? TryGetEndpoint(out string? error)
    {
        error = null;

        object? enumerator = null;
        object? device = null;
        object? volume = null;

        try
        {
            enumerator = new MMDeviceEnumeratorComObject();

            var hr = ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(Render, Multimedia, out device);

            if (hr == ENotFound)
            {
                // 这是**真实存在**的一种机器（无声卡的工控机 / 虚拟机 / 声卡被禁用）：
                // 必须给一句能看懂的话，否则现场只会看到"音量没反应"。
                error = "本机没有可用的音频输出设备（无声卡、声卡被禁用，或没有已启用的播放设备）。";
                return null;
            }

            if (hr < 0 || device is null)
            {
                error = $"拿不到默认音频输出设备（0x{hr:X8}）。";
                return null;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;

            hr = ((IMMDevice)device).Activate(ref iid, ClsCtxInProcServer, IntPtr.Zero, out volume);

            if (hr < 0 || volume is null)
            {
                error = $"音频端点不支持音量控制（0x{hr:X8}）。";
                return null;
            }

            var result = (IAudioEndpointVolume)volume;

            // 所有权交接给调用方：把局部变量置空，finally 就不会把它释放掉。
            volume = null;
            device = null;
            enumerator = null;

            return result;
        }
        catch (Exception ex)
        {
            error = $"访问音频设备失败：{ex.Message}";
            return null;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    private static HostAudioState ReadState(IAudioEndpointVolume endpoint)
    {
        try
        {
            var hr = endpoint.GetMasterVolumeLevelScalar(out var scalar);
            var volume = hr >= 0
                ? (int?)Math.Clamp((int)Math.Round(scalar * 100f), 0, 100)
                : null;

            hr = endpoint.GetMute(out var muted);
            var mute = hr >= 0 ? (bool?)muted : null;

            return new HostAudioState(volume, mute);
        }
        catch
        {
            // 读状态失败不该让"音量已经改成功了"这件事变成一次报错。
            return HostAudioState.Unknown;
        }
    }

    private static string Describe(HostAudioAction action) => action switch
    {
        HostAudioAction.VolumeUp => "音量已调高",
        HostAudioAction.VolumeDown => "音量已调低",
        HostAudioAction.Mute => "已静音",
        HostAudioAction.Unmute => "已取消静音",
        _ => "音量操作已完成",
    };

    /// <summary>
    /// 释放 RCW。<b>用 <c>FinalReleaseComObject</c></b>：这几个 COM 对象是我们独占创建、
    /// 独占使用的，不需要留着给谁复用；不确定时就释放，免得音频服务那边的引用计数越积越多。
    /// </summary>
    private static void Release(object? comObject)
    {
        if (comObject is null) return;

        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // 释放失败（已被释放 / 进程正在退出）无计可施，也不值得打扰上层。
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// CoreAudio 的最小互操作声明
//
// 🔥 这里每一行都按 **vtable 槽位顺序**排列，顺序本身就是契约：
//    COM 接口方法的调用位置由"它是第几个方法"决定，方法名只对人类有意义。
//    少写一个方法 ⇒ 后面所有方法整体前移一格 ⇒ 调用 GetMute 实际调到 SetMute，
//    参数个数还对不上 —— 表现为**栈被写坏 / 进程直接崩**，而不是一个编译错误。
//
//    所以：**不要删除声明，也不要重排顺序**。用不到的方法也必须占着它的位置
//    （用 `IntPtr` 占位即可，永远不会被调用，声明什么签名都不影响）。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>MMDeviceEnumerator 的 CoClass（CLSID 来自 mmdeviceapi.h）。</summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject
{
}

/// <summary><c>IMMDeviceEnumerator</c>：只需要"取默认输出端点"。</summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // 槽位 1：EnumAudioEndpoints。我们不用它，但**必须占位**（见上面的警告）。
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    // 槽位 2：GetDefaultAudioEndpoint。
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.IUnknown)] out object? endpoint);
}

/// <summary><c>IMMDevice</c>：只需要 <c>Activate</c>（槽位 1，因此它前面没有需要占位的方法）。</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // 槽位 1：Activate。
    [PreserveSig]
    int Activate(
        ref Guid iid,
        int clsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    // 槽位 2：OpenPropertyStore（占位）。
    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr properties);

    // 槽位 3：GetId（占位）。
    [PreserveSig]
    int GetId(out IntPtr id);

    // 槽位 4：GetState（占位）。
    [PreserveSig]
    int GetState(out int state);
}

/// <summary>
/// <c>IAudioEndpointVolume</c>：只需要第 7 / 12 / 13 / 15 / 16 号方法，
/// 但它们前面（以及中间 14 号）的方法必须全部按序占位。
/// </summary>
[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    // 1
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    // 2
    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    // 3
    [PreserveSig]
    int GetChannelCount(out uint count);

    // 4
    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    // 5
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    // 6
    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    // 7 ← 实际用到：主音量（0.0~1.0）
    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    // 8
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    // 9
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    // 10
    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    // 11
    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    // 12 ← 实际用到：设置静音
    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    // 13 ← 实际用到：读静音（"读真实系统状态"就靠它）
    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

    // 14
    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    // 15 ← 实际用到：调高一格（与按键盘音量键等价）
    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    // 16 ← 实际用到：调低一格
    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    // 17
    [PreserveSig]
    int QueryHardwareSupport(out uint mask);

    // 18
    [PreserveSig]
    int GetVolumeRange(out float min, out float max, out float increment);
}
