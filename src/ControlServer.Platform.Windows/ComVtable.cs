using System.Runtime.InteropServices;

namespace ControlServer.Platform.Windows;

/// <summary>
/// <b>裸 COM vtable 调用助手</b>（M5.4 零拷贝管线的地基）。
///
/// <para>
/// 🔥 <b>为什么不用 <c>[ComImport]</c> 把接口声明全</b>：本批真正要调的槽位不到 30 个，
/// 而 <c>ID3D11DeviceContext</c> 有 <b>110+ 个槽</b>、<c>ID3D11Device</c> 有 43 个。
/// 抄全了等于凭空引入几十处「顺序写错也不报错、只是行为诡异」的风险 ——
/// 而其中绝大多数槽永远不会被调用，抄它们只有成本、没有收益。
/// </para>
///
/// <para>
/// ⇒ 本类只做一件事：<b>按槽位号取函数指针</b>。每个槽位号都在调用点写成具名常量，
/// 并注明依据（头文件）。这与仓内既有做法一致 ——
/// <c>MediaFoundationEncoders</c> 里的 <c>GetAllocatedString = 槽位 13</c> 就是先例。
/// </para>
///
/// <para>
/// ⚠️ <b>槽位号错一个不会抛异常</b>，只会调到别的函数上去，症状通常是
/// 「返回一个奇怪的 HRESULT」或「指针被当成别的指针用」。所以
/// <b>每一条槽位常量都必须对着头文件核</b>（见 <c>docs/M5.4-实施方案.md</c> §3.1 那张表）。
/// </para>
///
/// <para>
/// <b>不用 <c>delegate* unmanaged</c></b>：本工程没开 <c>AllowUnsafeBlocks</c>，
/// 而为了一个调用助手去改全局编译开关，代价比它换来的那点性能大得多
/// （本批的调用频率是每帧个位数，不是每像素）。
/// </para>
/// </summary>
internal static class ComVtable
{
    /// <summary><c>IUnknown::QueryInterface</c> 的槽位。所有 COM 接口都是第 0 槽。</summary>
    internal const int QueryInterface = 0;

    /// <summary><c>IUnknown::AddRef</c> 的槽位。</summary>
    internal const int AddRef = 1;

    /// <summary><c>IUnknown::Release</c> 的槽位。</summary>
    internal const int Release = 2;

    /// <summary>
    /// 取 <paramref name="instance"/> 第 <paramref name="slot"/> 个槽位的函数指针。
    /// <para><paramref name="instance"/> 必须是一个<b>接口指针</b>（即指向 vtable 指针的那个指针）。</para>
    /// </summary>
    internal static IntPtr FunctionPointer(IntPtr instance, int slot)
    {
        if (instance == IntPtr.Zero) throw new ArgumentNullException(nameof(instance));

        var table = Marshal.ReadIntPtr(instance);

        if (table == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"COM 对象 {instance:X} 的 vtable 为空 —— 这不是「方法不可用」，是「这个指针根本不是接口指针」。");
        }

        var fn = Marshal.ReadIntPtr(table, checked(slot * IntPtr.Size));

        if (fn == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"COM 对象 {instance:X} 的第 {slot} 槽是空指针。槽位号对错都是这个症状 —— " +
                "先核 docs/M5.4-实施方案.md §3.1 那张表，别怀疑驱动。");
        }

        return fn;
    }

    /// <summary>按槽位取函数指针并绑定成委托。⚠️ <b>委托要缓存</b>，别在每帧的循环里重复绑定。</summary>
    internal static T Bind<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(FunctionPointer(instance, slot));

    /// <summary>
    /// <c>QueryInterface</c>。成功时 <paramref name="result"/> 是<b>已加引用</b>的接口指针，
    /// <b>调用方负责 <see cref="Release(IntPtr)"/></b>。
    /// </summary>
    internal static int QueryInterfaceRaw(IntPtr instance, ref Guid iid, out IntPtr result)
    {
        var fn = Bind<QueryInterfaceFn>(instance, QueryInterface);

        return fn(instance, ref iid, out result);
    }

    /// <summary><c>Release</c>（并把指针置零，防重复释放）。</summary>
    internal static void ReleasePointer(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;

        try
        {
            var fn = Bind<ReleaseFn>(instance, Release);
            fn(instance);
        }
        catch (Exception)
        {
            // 释放路径上不抛异常：一个释放失败不该把整条清理链打断，
            // 那会让后面的资源全部泄漏，比这一个泄漏严重得多。
        }
        finally
        {
            instance = IntPtr.Zero;
        }
    }

    /// <summary><c>AddRef</c>（返回新的引用计数，仅用于诊断）。</summary>
    internal static uint AddRefPointer(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return 0;

        var fn = Bind<AddRefFn>(instance, AddRef);

        return fn(instance);
    }

    /// <summary><c>HRESULT</c> 成功判定（<c>SUCCEEDED</c>：非负即为成功，不是"等于 0"）。</summary>
    internal static bool Succeeded(int hr) => hr >= 0;

    /// <summary>把 HRESULT 写成可读形式（日志用）。</summary>
    internal static string Describe(int hr) => $"0x{hr:X8}";

    // ── IUnknown ──

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint AddRefFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint ReleaseFn(IntPtr self);
}
