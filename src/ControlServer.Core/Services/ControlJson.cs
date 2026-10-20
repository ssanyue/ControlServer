using System.Text.Json;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 配置对象的 JSON 序列化约定：**camelCase**（与旧版 <c>control_config.json</c> 一致）。
/// 全项目唯一入口，禁止各处 new 自己的 JsonSerializerOptions —— 否则旧配置读取会出现字段名漂移。
/// </summary>
public static class ControlJson
{
    /// <summary>配置读写 / 导入导出统一使用的序列化设置。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 中文（如「指令库」数据类型、按钮名）不转义为 \uXXXX，便于人读与 git diff。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>配置读取（宽松）：忽略未知字段，容忍 null 与缺失。</summary>
    public static readonly JsonSerializerOptions LenientOptions = new(Options)
    {
        // 旧配置里数值字段可能是字符串（如 "height": "80"），宽松模式允许转换。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// <b>高频落盘</b>专用（不缩进 + 不写 null），只给"每秒写几十行"的遥测明细列用。
    /// <para>
    /// 与 <see cref="Options"/> 的两点差别都是有意的：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>不缩进</b>：缩进在配置文件里是优点（人能读、git diff 友好），
    ///         但在 <c>agent_metrics.DevicesJson</c> 里纯属浪费 —— 一台机器 6 块硬件的 JSON
    ///         会从约 1.2 KB 涨到约 1.8 KB，按 300 台 × 5 秒算就是<b>每天多写 GB 级的空白字符</b>。</item>
    ///   <item><b>不写 null</b>：逐设备读数是"每种设备只填一两个字段"的形状
    ///         （磁盘只填读写、网卡只填上下行，见 <c>AgentDeviceReadingDto</c>），
    ///         原样写出去的话每块硬件都跟着 4 个 <c>null</c>。省略它们不影响往返 ——
    ///         反序列化时缺失的属性就落回它本来的默认值 <c>null</c>。</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>不要拿它存配置</b>：配置要能被人读、被 diff，那是 <see cref="Options"/> 的活。
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>紧凑序列化（见 <see cref="CompactOptions"/>）；<b>只用于高频落盘的内部数据</b>。</summary>
    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, CompactOptions);

    /// <summary>紧凑反序列化（与 <see cref="SerializeCompact{T}"/> 配对）。</summary>
    public static T? DeserializeCompact<T>(string json) => JsonSerializer.Deserialize<T>(json, CompactOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>宽松反序列化（用于导入外部 / 旧版本文件）。</summary>
    public static T? DeserializeLenient<T>(string json) => JsonSerializer.Deserialize<T>(json, LenientOptions);
}

/// <summary>
/// 深拷贝工具：复制配置对象并**重新生成所有 Id**（对应「复制按钮 / 复制子按钮集」语义）。
/// 未填的字段全部原样保留。
/// </summary>
public static class ConfigCloner
{
    /// <summary>深拷贝一个按钮（含动作、定时、月度计划），并生成新 Id。</summary>
    public static ButtonConfig CloneButton(ButtonConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = ControlJson.Deserialize<ButtonConfig>(ControlJson.Serialize(source)) ?? new ButtonConfig();
        RegenerateIds(clone);
        return clone;
    }

    /// <summary>深拷贝一个子按钮集（含子按钮及其动作），并生成新 Id。</summary>
    public static SubButtonSet CloneSubButtonSet(SubButtonSet source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = ControlJson.Deserialize<SubButtonSet>(ControlJson.Serialize(source)) ?? new SubButtonSet();
        RegenerateIds(clone);
        return clone;
    }

    /// <summary>深拷贝一个子按钮（含动作），并生成新 Id。</summary>
    public static SubButton CloneSubButton(SubButton source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = ControlJson.Deserialize<SubButton>(ControlJson.Serialize(source)) ?? new SubButton();
        RegenerateIds(clone);
        return clone;
    }

    /// <summary>深拷贝一份完整配置。</summary>
    public static ControlSettings CloneSettings(ControlSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return ControlJson.Deserialize<ControlSettings>(ControlJson.Serialize(source)) ?? new ControlSettings();
    }

    private static void RegenerateIds(ButtonConfig button)
    {
        button.Id = Guid.NewGuid().ToString();
        if (button.Actions is null) return;

        foreach (var action in button.Actions)
        {
            action.Id = Guid.NewGuid().ToString();
        }

        // 月度计划及其 Id 一并重生成，避免与新按钮下其它计划冲突。
        if (button.MonthlyPlans is not null)
        {
            foreach (var plan in button.MonthlyPlans)
            {
                plan.Id = Guid.NewGuid().ToString();
            }
        }
    }

    private static void RegenerateIds(SubButtonSet set)
    {
        set.Id = Guid.NewGuid().ToString();
        if (set.SubButtons is null) return;

        foreach (var sub in set.SubButtons)
        {
            RegenerateIds(sub);
        }
    }

    private static void RegenerateIds(SubButton sub)
    {
        sub.Id = Guid.NewGuid().ToString();
        if (sub.Actions is null) return;

        foreach (var action in sub.Actions)
        {
            action.Id = Guid.NewGuid().ToString();
        }
    }
}
