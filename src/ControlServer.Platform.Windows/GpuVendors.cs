namespace ControlServer.Platform.Windows;

/// <summary>
/// 把「适配器的 <c>PCI VendorId</c>」与「编码器 MFT 的显示名」对上号（M5.0 第二批）。
///
/// <para>
/// 🔥 <b>为什么需要这一层</b>：Media Foundation 的枚举只给显示名
/// （<c>"Intel® Quick Sync Video H.264 Encoder MFT"</c>），
/// <b>不带任何适配器标识</b>；而 DXGI 的枚举只给 VendorId / DeviceId，不给编码器。
/// 两边要合起来才能回答"<b>哪一块卡有硬编</b>"——只能靠名字里的厂商词做映射。
/// </para>
///
/// <para>
/// ⚠️ <b>名字匹配是启发式，不是契约</b>（厂商可以随时改显示名）。
/// 所以这里的策略是：<b>认不出就返回 false（不归属），绝不错归属</b> ——
/// 一块卡少报一项能力，比把 Intel 的编码器算到 NVIDIA 头上要安全得多，
/// 后者会让"这台机器能 8K60"变成一个<b>看起来有依据的假结论</b>。
/// </para>
/// </summary>
internal static class GpuVendors
{
    /// <summary>NVIDIA（<c>PCI_VENDOR_ID_NVIDIA</c>）。</summary>
    private const uint Nvidia = 0x10DE;

    /// <summary>Intel（<c>PCI_VENDOR_ID_INTEL</c>）。</summary>
    private const uint Intel = 0x8086;

    /// <summary>AMD / ATI（<c>PCI_VENDOR_ID_ATI</c>）。</summary>
    private const uint Ati = 0x1002;

    /// <summary>AMD（<c>PCI_VENDOR_ID_AMD</c>；与 <see cref="Ati"/> 并存，两者都出现）。</summary>
    private const uint Amd = 0x1022;

    /// <summary>Qualcomm（Adreno）。</summary>
    private const uint Qualcomm = 0x5143;

    /// <summary>Microsoft（WARP 软件光栅器）。</summary>
    private const uint Microsoft = 0x1414;

    /// <summary>
    /// 这个显示名是不是<b>该厂商</b>注册的编码器。认不出该厂商时返回 <c>false</c>。
    /// </summary>
    internal static bool FriendlyNameMatchesVendor(string? friendlyName, uint vendorId)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;

        return vendorId switch
        {
            Nvidia => Contains(friendlyName, "nvidia"),
            Intel => Contains(friendlyName, "intel"),
            Ati or Amd => Contains(friendlyName, "amd")
                          || Contains(friendlyName, "advanced micro devices")
                          || Contains(friendlyName, "ati")
                          || Contains(friendlyName, "radeon"),
            Qualcomm => Contains(friendlyName, "qualcomm") || Contains(friendlyName, "adreno"),
            Microsoft => Contains(friendlyName, "microsoft"),
            _ => false,
        };
    }

    /// <summary>
    /// 用在**日志/依据文案**里的厂商可读名；认不出时给十六进制 ID
    /// （<c>null</c> 会让依据句子缺主语，那比给出 ID 更糟）。
    /// </summary>
    internal static string Describe(uint vendorId) => vendorId switch
    {
        Nvidia => "NVIDIA",
        Intel => "Intel",
        Ati or Amd => "AMD",
        Qualcomm => "Qualcomm",
        Microsoft => "Microsoft",
        _ => $"厂商 0x{vendorId:X4}",
    };

    private static bool Contains(string text, string token)
        => text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
