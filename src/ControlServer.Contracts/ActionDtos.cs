namespace ControlServer.Contracts;

/// <summary>
/// 「执行一组动作」请求（用于前端「测试发送」与编辑页的「单独执行本条动作」，<b>不落配置</b>）。
/// <para>
/// 三种来源：
/// <list type="bullet">
///   <item><b>ButtonId</b>：从当前配置里取该按钮的全部动作；</item>
///   <item><b>SubButtonId</b>：从配置里取该子按钮的动作，并自动套用所属集合的统一 IP / 端口；</item>
///   <item><b>Actions</b>：内联动作（来自<strong>尚未保存</strong>的编辑草稿）。</item>
/// </list>
/// 前两种可再叠加 <see cref="ActionId"/>，只执行其中<strong>一条</strong>动作。
/// 三者都为空时返回 400。
/// </para>
/// </summary>
/// <param name="ButtonId">按已保存的按钮执行（可选）。</param>
/// <param name="SubButtonId">按已保存的子按钮执行（可选）。</param>
/// <param name="ActionId">
/// 只执行指定的一条动作（必须与 <see cref="ButtonId"/> 或 <see cref="SubButtonId"/> 搭配）。
/// <para>
/// 与内联 <see cref="Actions"/> 的分工：本字段按<strong>已保存</strong>的配置取动作，
/// 适合脚本 / 运维「单独触发某条动作」；而编辑页里动作可能刚改过还没保存，
/// 那时走内联路径才能测到"屏幕上看到的这一条"。孤立地传本字段会返回 400 ——
/// 没有宿主按钮就无从判断该动作属于谁，猜一个等于凭空执行任意数据。
/// </para>
/// </param>
/// <param name="Label">展示用的名称；缺省时用按钮名 / 按钮名 · 动作名 / 「临时动作」。</param>
/// <param name="OverrideIp">临时覆盖目标 IP（内联动作时可用）。</param>
/// <param name="OverridePort">临时覆盖目标端口（内联动作时可用）。</param>
/// <param name="Actions">内联动作列表（测试发送）。</param>
public sealed record ActionExecuteRequest(
    string? ButtonId = null,
    string? SubButtonId = null,

    /// <summary>
    /// 组合按钮 Id（2026-10-15，需求⑤）：执行**整个组合** —— 按成员顺序逐段执行、段间带延迟。
    /// <para>
    /// ⚠️ 与 <see cref="ButtonId"/> 是**互斥**的两条路：组合是"一串按钮"，
    /// 单按钮是"一个按钮"，同时给两个只会让"到底执行了什么"变得含糊。
    /// 同时给时**以组合为准**并返回 400 说明（见 ActionsController）。
    /// </para>
    /// </summary>
    string? ComboId = null,

    string? ActionId = null,
    string? Label = null,
    string? OverrideIp = null,
    int? OverridePort = null,
    IReadOnlyList<ActionExecuteItem>? Actions = null);

/// <summary>内联的单条动作（字段与 <c>Core.Models.ButtonAction</c> 对齐，全部可选以便前端只填要测的字段）。</summary>
public sealed record ActionExecuteItem(
    string? Name = null,
    string? TargetIp = null,
    int? TargetPort = null,
    string? Protocol = null,
    string? DataType = null,
    string? DataContent = null,
    int? DelayMs = null,
    string? PairingCode = null,
    string? PairingCodeType = null);

/// <summary>单条动作的执行结果。</summary>
public sealed record ActionStepDto(
    int Index,
    string ActionName,
    string Protocol,
    string TargetIp,
    int TargetPort,
    bool Ok,
    int BytesSent,
    string Message,
    string DataPreview,
    string? ResolvedFromCommand);

/// <summary>一次多动作执行的完整结果。</summary>
public sealed record ActionExecutionDto(
    string RunId,
    string ButtonName,
    DateTimeOffset StartedAt,
    long ElapsedMs,
    bool Ok,
    int SuccessCount,
    int FailureCount,
    string Summary,
    IReadOnlyList<ActionStepDto> Steps);

/// <summary>组合按钮里**一段**的执行结果（2026-10-15，需求⑤）。</summary>
/// <param name="ButtonId">成员按钮 Id。</param>
/// <param name="ButtonLabel">成员按钮显示名。</param>
/// <param name="DelayAfterMs">本段执行完之后等待的毫秒数（最后一段恒为 0）。</param>
/// <param name="Result">这一段内部的动作执行结果；<c>null</c> = 这一段没有执行（见 <paramref name="Skipped"/>）。</param>
/// <param name="Skipped">被跳过的原因（例如"这个按钮里没有动作"）。</param>
public sealed record ComboSegmentDto(
    string ButtonId,
    string ButtonLabel,
    int DelayAfterMs,
    ActionExecutionDto? Result,
    string? Skipped = null);

/// <summary>
/// 一次「组合按钮执行」的结果（2026-10-15，需求⑤）。
/// <para>
/// 🔥 <b>分段而不是平铺</b>：前端要能说清"哪个按钮、等了多少、成没成"，
/// 平铺成一条动作列表会把这个信息丢掉（见 <c>ComboExecutionResult</c> 的注释）。
/// </para>
/// </summary>
public sealed record ComboExecutionDto(
    string RunId,
    string ComboLabel,
    DateTimeOffset StartedAt,
    long ElapsedMs,
    bool Ok,
    int SegmentCount,
    int SuccessCount,
    int FailureCount,
    string Summary,
    IReadOnlyList<ComboSegmentDto> Segments,
    IReadOnlyList<string> SkippedButtonIds);

/// <summary>编码预览请求。</summary>
/// <param name="DataType">String / Hex / ANSI / ASCII / UTF-8。</param>
/// <param name="DataContent">待编码内容（Hex 时是十六进制串；WoL 时是 MAC）。</param>
public sealed record EncodePreviewRequest(string? DataType = null, string? DataContent = null);

/// <summary>
/// 编码预览结果 —— 前端「发送前先看看会发什么字节」用。
/// </summary>
/// <param name="DataType">归一化后的数据类型名。</param>
/// <param name="Text">按该类型还原出的可读文本。</param>
/// <param name="Hex">以空格分隔的十六进制（如 <c>E4 B8 AD</c>）。</param>
/// <param name="HexCompact">无分隔的十六进制（对接码 Hex 比较用）。</param>
/// <param name="Length">字节数。</param>
/// <param name="AnsiEncoding">当前生效的 ANSI 编码名（诊断用，正常为 <c>gb2312</c> / <c>gbk</c>）。</param>
/// <param name="Error">编码失败原因（如 Hex 长度为奇数）；成功为 <c>null</c>。</param>
public sealed record EncodePreviewDto(
    string DataType,
    string Text,
    string Hex,
    string HexCompact,
    int Length,
    string AnsiEncoding,
    string? Error);
