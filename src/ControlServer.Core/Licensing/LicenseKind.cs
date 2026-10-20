namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权类型（授权文件里的 <c>lt</c> 字段）。取值与参照实现（HudongJingling）一致。
/// </summary>
public static class LicenseKind
{
    /// <summary>正式授权。</summary>
    public const string Full = "full";

    /// <summary>试用授权（本地生成，有到期时间）。</summary>
    public const string Trial = "trial";

    /// <summary>临时授权（给现场短期调试用，语义同正式，只是期限短）。</summary>
    public const string Temp = "temp";

    /// <summary>全部合法取值（顺序即界面展示顺序）。</summary>
    public static readonly IReadOnlyList<string> All = new[] { Full, Trial, Temp };

    /// <summary>是否为合法类型。<b>大小写敏感</b> —— 该值直接参与签名载荷，不能容忍大小写漂移。</summary>
    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>中文显示名（仅用于界面，不参与任何比较）。</summary>
    public static string DisplayName(string? kind) => kind switch
    {
        Full => "正式授权",
        Trial => "试用授权",
        Temp => "临时授权",
        _ => kind ?? "未知",
    };
}
