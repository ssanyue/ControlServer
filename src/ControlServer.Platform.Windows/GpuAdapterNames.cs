using System.Globalization;
using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 把「GPU 适配器 LUID」翻译成「显卡型号」。<b>两条路，互为兜底。</b>
/// <para>
/// <b>为什么必须翻译</b>：Windows 的 <c>\GPU Engine(*)</c> 性能计数器只按 LUID 区分适配器
/// （实例名形如 <c>pid_1234_luid_0x00000000_0x0000D7B1_phys_0_eng_0_engtype_3D</c>），
/// 名字里<b>没有任何型号信息</b>。LUID 与 <c>DXGI_ADAPTER_DESC1.AdapterLuid</c> /
/// <c>D3DKMTOpenAdapterFromLuid</c> 接受的是<b>同一个值</b> —— 这才是"对号入座"的钥匙。
/// </para>
/// <para>
/// 🔥 <b>为什么不能靠"硬件清单里的显卡名字依次配上去"</b>：清单（WMI <c>Win32_VideoController</c>）
/// 与计数器（PDH）的枚举顺序、数量、甚至<b>成员集合</b>都不保证一致 ——
/// 本机实测清单里是 <c>Intel UHD 620 + NVIDIA MX250</c>，而 GPU Engine 报出了<b>三</b>个适配器
/// （多出一个 Windows 合成器用的软件适配器）。按顺序硬配会把 MX250 的占用率画到 UHD 620 上 ——
/// 比"适配器 1 / 2"错得更远，因为那个错误<b>看起来是对的</b>。
/// </para>
/// <para>
/// <b>两条路的分工</b>（2026-10-14）：
/// </para>
/// <list type="number">
///   <item><b>DXGI</b>（首选）：文档化、能一次拿到全部适配器，且<b>带
///         <c>DXGI_ADAPTER_FLAG_SOFTWARE</c></b> —— 靠它可以干净地剔掉软件光栅器。
///         代价是 COM（裸 vtable）。</item>
///   <item><b>D3DKMT</b>（兜底）：纯 <c>gdi32</c> 调用，不需要 COM，也不需要版本相关的枚举结构体
///         （拿 PDH 已经给出来的 LUID 逐个问）。走 GDI 的路径在 <b>Session 0</b>（服务、无桌面）
///         里可用性更好 —— 而 Agent 服务恰恰就跑在 Session 0。</item>
/// </list>
/// <para>
/// ⚠️ <b>为什么两条都要</b>：本机只能验证"有桌面的会话"里两条都成立，
/// 无法在这里实测 Session 0。若只留 DXGI，一旦它在服务里枚举不出来，
/// 表现是<b>退回占位名</b> —— 也就是"这个改进悄悄没生效"，而这正是需求要修的病症。
/// </para>
/// <para>
/// 两条都失败时返回空表，调用方退回占位名（<c>GPU 适配器 N</c>）——
/// 刻意的"宁可不说，也不说错"。
/// </para>
/// </summary>
internal static class GpuAdapterNames
{
    /// <summary>一个适配器的型号，以及它是不是软件光栅器（不是物理显卡）。</summary>
    internal sealed record Named(string Name, bool IsSoftware);

    /// <summary>
    /// LUID 的数值形态。<b>用它当字典键</b>，而不是用 PDH 给的那个字符串 ——
    /// 字符串的十六进制补零位数（<c>0x0000D7B1</c> vs <c>0xD7B1</c>）是实现细节，
    /// 拿它当键会在某台机器上突然"一条都匹配不上"，且没有任何报错。
    /// </summary>
    internal readonly record struct Luid(uint Low, int High);

    /// <summary>
    /// <c>DXGI_ADAPTER_FLAG_SOFTWARE</c>：Windows 自带的软件光栅器（WARP，显示名
    /// <c>Microsoft Basic Render Driver</c>），<b>不是一块显卡</b>。本机实测它会被
    /// GPU Engine 计数器报出来（占用率恒为 0）—— 当成"一块显卡"列进硬件清单会让人以为装了三个显卡。
    /// </summary>
    internal const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;

    /// <summary>
    /// 从 PDH 实例名里抠出来的 LUID 段：<c>luid_0xAAAAAAAA_0xBBBBBBBB</c>（<b>先高后低</b>）。
    /// 解析失败返回 false（那种实例不属于任何适配器）。
    /// </summary>
    internal static bool TryParseLuid(string? luidKey, out Luid luid)
    {
        luid = default;

        if (string.IsNullOrEmpty(luidKey)) return false;

        var parts = luidKey.Split('_');

        // luid_0xAAAAAAAA_0xBBBBBBBB ⇒ 至少 3 段，且后两段是 0x 开头的十六进制。
        if (parts.Length < 3) return false;

        return TryParseLuidRaw(parts[1], parts[2], out luid);
    }

    /// <summary>
    /// 从<b>已经切好的</b>两段十六进制（先高后低）拼一个 LUID。
    /// <para>
    /// 拆出来是因为调用方拿到的实例名格式并不统一：PDH 给的是
    /// <c>pid_…_luid_0xA_0xB_phys_…</c>（<b>含</b><c>luid_</c> 前缀），
    /// 而按段解析的调用方自己已经定位到那两段了；再让它拼回字符串调
    /// <see cref="TryParseLuid"/> 只是多余的往返，且容易把"先高后低"写反。
    /// </para>
    /// <para>⚠️ <b>顺序是先高后低</b>（与 <c>LUID</c> 的 <c>{ LowPart; HighPart; }</c> 相反）。
    /// 写反不会报错，只会"一条都匹配不上"。</para>
    /// </summary>
    internal static bool TryParseLuidRaw(string highHex, string lowHex, out Luid luid)
    {
        luid = default;

        if (!TryParseHex(highHex, out var high)) return false;
        if (!TryParseHex(lowHex, out var low)) return false;

        luid = new Luid(low, unchecked((int)high));

        return true;
    }

    private static bool TryParseHex(string text, out uint value)
    {
        value = 0;

        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;

        return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 解析出「LUID → 型号」。<paramref name="luidKeys"/> 只在<b>兜底路径</b>用到
    /// （D3DKMT 是"按 LUID 逐个问"，不是枚举）。
    /// </summary>
    internal static IReadOnlyDictionary<Luid, Named> Resolve(IReadOnlyList<string> luidKeys)
    {
        if (!OperatingSystem.IsWindows()) return new Dictionary<Luid, Named>();

        // ── 首选：DXGI ──
        var byLuid = ResolveByDxgi();

        if (byLuid.Count > 0) return byLuid;

        // ── 兜底：D3DKMT ──
        return ResolveByD3dkmt(luidKeys);
    }

    /// <summary>
    /// 只走 DXGI。测试与排障可用它单独验证"首选那条路通不通"。
    /// </summary>
    internal static IReadOnlyDictionary<Luid, Named> ResolveByDxgi()
    {
        var byLuid = new Dictionary<Luid, Named>();

        if (!OperatingSystem.IsWindows()) return byLuid;

        foreach (var adapter in Dxgi.Enumerate())
        {
            byLuid[adapter.Luid] = new Named(adapter.Description, adapter.IsSoftware);
        }

        return byLuid;
    }

    /// <summary>
    /// 只走 D3DKMT（gdi32，逐 LUID）。
    /// <para>
    /// 🔥 单独暴露出来是为了<b>能被真的测到</b>：这条路是"DXGI 在 Session 0 里枚举不出来"时的保险，
    /// 而 <see cref="Resolve"/> 正常情况下永远走不到它 —— 不被测到的保险等于没有保险。
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<Luid, Named> ResolveByD3dkmt(IReadOnlyList<string> luidKeys)
    {
        var byLuid = new Dictionary<Luid, Named>();

        if (!OperatingSystem.IsWindows()) return byLuid;

        foreach (var key in luidKeys)
        {
            if (!TryParseLuid(key, out var luid)) continue;

            var name = D3dkmt.QueryModelName(luid);

            if (string.IsNullOrEmpty(name)) continue;

            // ⚠️ 这条路拿不到 DXGI 的 Flags，只能用名字认软件光栅器。
            //    这是**已知的唯一软件适配器**的官方显示名；真显卡不会叫这个名字。
            byLuid[luid] = new Named(name, IsSoftwareRenderer(name));
        }

        return byLuid;
    }

    /// <summary>软件光栅器的显示名判定（仅在 D3DKMT 兜底路径上用，因为那里没有 Flags）。</summary>
    internal static bool IsSoftwareRenderer(string name)
        => name.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase);

    /// <summary>按 PDH 给的字符串找型号。找不到返回 false（调用方退回占位名）。</summary>
    internal static bool TryFind(
        IReadOnlyDictionary<Luid, Named> map,
        string luidKey,
        out Named named)
    {
        named = null!;

        if (!TryParseLuid(luidKey, out var luid)) return false;

        if (!map.TryGetValue(luid, out var hit)) return false;

        named = hit;

        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 首选：DXGI（只走裸 vtable，不引 COM 接口定义）
    // ═══════════════════════════════════════════════════════════════

    internal static class Dxgi
    {
        /// <summary>IID_IDXGIFactory1。</summary>
        private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        /// <summary><c>DXGI_ERROR_NOT_FOUND</c>：枚举到头了（不是错误）。</summary>
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        // IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) ⇒ EnumAdapters1 落在第 12 槽。
        private const int VtblEnumAdapters1 = 12;

        // IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) ⇒ IDXGIAdapter1::GetDesc1 落在第 10 槽。
        private const int VtblGetDesc1 = 10;

        /// <summary>
        /// 一个适配器。
        /// <para>
        /// 🔥 <b>M5.0 补齐了 <c>VendorId</c> / <c>DeviceId</c> / 显存</b> ——
        /// 能力探测（<see cref="WindowsEncoderCapabilityProbe"/>）要报的是<b>事实</b>，
        /// 而"名称 + 厂商标识 + 设备标识"三样齐了才算事实完备：
        /// 名称是给人看的，后两者才是能拿去比对、查表的东西。
        /// </para>
        /// </summary>
        internal sealed record Adapter(
            Luid Luid,
            string Description,
            bool IsSoftware,
            uint VendorId,
            uint DeviceId,
            ulong DedicatedVideoMemoryBytes);

        /// <summary>
        /// <c>DXGI_ADAPTER_DESC1</c>，字段顺序照抄 <c>dxgi.h</c>：
        /// <c>Description[128]</c> → 4 个 <c>UINT</c> → 3 个 <c>SIZE_T</c> → <c>LUID</c> → <c>Flags</c>。
        /// <para>
        /// ⚠️ <c>LUID</c> 在 C 里是 <c>{ DWORD LowPart; LONG HighPart; }</c> ——
        /// <b>低在前、高在后</b>。顺序写反不会报错，只会"一块都匹配不上"。
        /// </para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;

            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;

            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;

            public uint LuidLow;
            public int LuidHigh;

            public uint Flags;
        }

        private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

        private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        internal static List<Adapter> Enumerate()
        {
            var result = new List<Adapter>();

            var iid = IidFactory1;
            IntPtr factory;

            try
            {
                if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return result;
            }
            catch (DllNotFoundException)
            {
                // 极精简的容器镜像里没有 dxgi.dll：交给 D3DKMT 兜底，不影响其它遥测。
                return result;
            }
            catch (EntryPointNotFoundException)
            {
                return result;
            }

            try
            {
                var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), VtblEnumAdapters1 * IntPtr.Size));

                for (uint index = 0; ; index++)
                {
                    IntPtr adapter;

                    int hr;

                    try
                    {
                        hr = enumAdapters(factory, index, out adapter);
                    }
                    catch (AccessViolationException)
                    {
                        // vtable 槽位对不上（驱动异常）时别把整个采集循环带走。
                        break;
                    }

                    if (hr == DXGI_ERROR_NOT_FOUND) break;

                    if (hr < 0 || adapter == IntPtr.Zero) break;

                    try
                    {
                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(
                            Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), VtblGetDesc1 * IntPtr.Size));

                        var desc = new AdapterDesc1();

                        if (getDesc(adapter, out desc) < 0) continue;

                        var name = (desc.Description ?? string.Empty).Trim();

                        result.Add(new Adapter(
                            new Luid(desc.LuidLow, desc.LuidHigh),
                            name,
                            (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0,
                            desc.VendorId,
                            desc.DeviceId,
                            DedicatedVideoMemory(desc)));
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }

            return result;
        }

        /// <summary>
        /// 显存字节数。
        /// <para>
        /// ⚠️ <c>SIZE_T</c> 在 <b>32 位进程</b>里读一个超过 4 GB 的值会抛
        /// <see cref="OverflowException"/> —— 而 8K 方案要看的恰恰是
        /// "显存够不够 8 GB"（NVIDIA 官方 8K60 的前提之一）。
        /// 这里把异常吞成 <c>0</c>，让调用方看到"这个数不可信"，
        /// 而不是让整个适配器枚举挂掉、连带其它遥测一起失效。
        /// </para>
        /// </summary>
        private static ulong DedicatedVideoMemory(AdapterDesc1 desc)
        {
            try
            {
                return desc.DedicatedVideoMemory.ToUInt64();
            }
            catch (OverflowException)
            {
                return 0;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 兜底：D3DKMT（gdi32，服务里的可用性更好）
    //
    // 只调两个函数，且**不需要枚举结构体** ——
    // D3DKMT_ADAPTERINFO 的尾部随 Windows 版本变（stride 会变），
    // 而 D3DKMTOpenAdapterFromLuid 是"我已有 LUID，只求个句柄"，两个结构体都是固定的。
    // ═══════════════════════════════════════════════════════════════

    private static class D3dkmt
    {
        /// <summary><c>KMTQAITYPE_ADAPTERREGISTRYINFO</c>：返回适配器在注册表里的那串描述。</summary>
        private const int KmtQueryAdapterRegistryInfo = 8;

        /// <summary>
        /// 🔥 <c>D3DKMT_ADAPTERREGISTRYINFO</c> 的大小 = <b>4</b> × <c>WCHAR[260]</c> = 2080 字节。
        /// <para>
        /// 这个"4"是<b>实测出来的</b>：按 5 段（2600 字节）传，<c>D3DKMTQueryAdapterInfo</c>
        /// 返回 <c>0xC000000D</c>（STATUS_INVALID_PARAMETER）—— 驱动要求尺寸<b>精确匹配</b>，
        /// 多给也照样拒。探针扫描 4/8/16/260/520/1040/2080/2600/3120 九种尺寸才定下来。
        /// </para>
        /// </summary>
        private const int RegistryInfoBytes = 4 * 260 * 2;

        /// <summary>读型号名时最多取多少个宽字符（第一个字段 <c>AdapterString</c>）。</summary>
        private const int AdapterStringChars = 260;

        [StructLayout(LayoutKind.Sequential)]
        private struct LuidStruct
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenAdapterFromLuid
        {
            public LuidStruct Luid;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryAdapterInfo
        {
            public uint hAdapter;
            public int Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CloseAdapter
        {
            public uint hAdapter;
        }

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid args);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

        /// <summary>按 LUID 问型号。任何一步失败都返回 null（调用方退回占位名）。</summary>
        internal static string? QueryModelName(Luid luid)
        {
            var open = new OpenAdapterFromLuid
            {
                Luid = new LuidStruct { LowPart = luid.Low, HighPart = luid.High },
            };

            int hr;

            try
            {
                hr = D3DKMTOpenAdapterFromLuid(ref open);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }

            if (hr != 0 || open.hAdapter == 0) return null;

            try
            {
                var buffer = Marshal.AllocHGlobal(RegistryInfoBytes);

                try
                {
                    for (var i = 0; i < RegistryInfoBytes; i++) Marshal.WriteByte(buffer, i, 0);

                    var query = new QueryAdapterInfo
                    {
                        hAdapter = open.hAdapter,
                        Type = KmtQueryAdapterRegistryInfo,
                        pPrivateDriverData = buffer,
                        PrivateDriverDataSize = RegistryInfoBytes,
                    };

                    if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;

                    var name = Marshal.PtrToStringUni(buffer, AdapterStringChars)?.Trim();

                    return string.IsNullOrEmpty(name) ? null : name;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                var close = new CloseAdapter { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
    }
}
