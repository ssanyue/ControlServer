using System.Text.Json;

namespace ControlServer.Contracts;

/// <summary>
/// 配置数量统计。
/// </summary>
/// <param name="Buttons">按钮数。</param>
/// <param name="SubButtonSets">子按钮集数。</param>
/// <param name="SubButtons">全部集合内的子按钮总数。</param>
public sealed record ConfigCountsDto(int Buttons, int SubButtonSets, int SubButtons);

/// <summary>
/// 配置读取结果。
/// <para>
/// ⚠️ <b>设计说明</b>：<see cref="Settings"/> 刻意用 <see cref="JsonElement"/> 而非强类型 DTO ——
/// 分层上 <c>Contracts</c> 在 <c>Core</c> 之下，不能反向引用 <c>Core.Models</c>；
/// 而把 <c>ControlSettings</c> 在契约层再抄一份会造成**两处真相**（改一处忘另一处 → 字段静默丢失）。
/// 因此配置的形状**只有一份真相**（<c>Core.Models</c>），HTTP 层只做透传：
/// 出参由 <c>ControlJson</c> 序列化，入参由 <c>ControlJson.DeserializeLenient</c> 还原。
/// 前端 TS 类型与 <c>Core.Models</c> camelCase 一一对应。
/// </para>
/// </summary>
/// <param name="Settings">全量配置（camelCase，形状同旧版 control_config.json）。</param>
/// <param name="Version">乐观锁版本号；PUT 时须原样回传。</param>
/// <param name="State">加载状态：Ok / Missing / Corrupt。</param>
/// <param name="Notes">本次清洗实际做了哪些迁移 / 校正（面向用户展示）。</param>
/// <param name="Counts">数量统计。</param>
/// <param name="IsEmpty">是否「空配置」（无按钮且无子按钮集）—— 前端据此提示导入。</param>
public sealed record ConfigViewDto(
    JsonElement Settings,
    int Version,
    string State,
    IReadOnlyList<string> Notes,
    ConfigCountsDto Counts,
    bool IsEmpty);

/// <summary>
/// 整体覆盖保存配置（乐观锁）。
/// </summary>
/// <param name="Settings">全量配置；形状同 <see cref="ConfigViewDto.Settings"/>。</param>
/// <param name="ExpectedVersion">期望版本；与存储不一致时返回 409。传 <c>null</c> 表示强制覆盖。</param>
/// <param name="UpdatedBy">写入者标识（写入审计与日志）。</param>
public sealed record ConfigSaveRequest(
    JsonElement Settings,
    int? ExpectedVersion = null,
    string? UpdatedBy = null);

/// <summary>
/// 配置导出结果（单文件自包含）。
/// </summary>
/// <param name="FileName">建议文件名（含扩展名）。</param>
/// <param name="Encoding"><c>base64</c>（单行文本，便于复制粘贴）/ <c>json</c>（明文，便于人工查看与 diff）。</param>
/// <param name="Content">文件内容。</param>
/// <param name="Version">导出时的配置版本。</param>
/// <param name="ImageCount">内嵌图片数量。</param>
/// <param name="Bytes">实际备份 JSON 的字节数（base64 模式下为解码后大小，用于前端提示）。</param>
public sealed record ConfigExportDto(
    string FileName,
    string Encoding,
    string Content,
    int Version,
    int ImageCount,
    int Bytes);

/// <summary>
/// 配置导入请求。
/// <para>
/// <see cref="Content"/> 支持**三种输入**（自动识别，与旧版互为兼容）：
/// <list type="number">
///   <item>旧版「导出配置」产出的 <c>.controlbak</c> / <c>.json</c> —— <b>明文 ConfigBackup JSON</b>；</item>
///   <item>服务版导出的 <b>base64 单行文本</b>（对上面那份 JSON 做 UTF-8 → Base64）；</item>
///   <item>裸 <c>ControlSettings</c> JSON（没有 <c>settings</c> 外层的旧文件）。</item>
/// </list>
/// </para>
/// </summary>
/// <param name="Content">备份内容文本（明文 JSON 或 base64）。</param>
/// <param name="ExpectedVersion">期望版本；不一致返回 409。传 <c>null</c> 表示强制覆盖。</param>
/// <param name="UpdatedBy">写入者标识。</param>
public sealed record ConfigImportRequest(
    string? Content = null,
    int? ExpectedVersion = null,
    string? UpdatedBy = null);

/// <summary>
/// 配置导入结果。
/// </summary>
/// <param name="Ok">是否成功写入。</param>
/// <param name="Version">写入后的新版本；失败时为 0。</param>
/// <param name="Counts">导入后的数量统计。</param>
/// <param name="ImageCount">实际还原的图片数。</param>
/// <param name="IgnoredFields">被忽略的配置字段（服务版裁剪掉的单机版专有字段，如 window 尺寸、用户密码哈希）。</param>
/// <param name="Notes">清洗器产生的迁移 / 校正说明。</param>
/// <param name="Message">面向用户的结论文案。</param>
public sealed record ConfigImportResultDto(
    bool Ok,
    int Version,
    ConfigCountsDto Counts,
    int ImageCount,
    IReadOnlyList<string> IgnoredFields,
    IReadOnlyList<string> Notes,
    string Message);

/// <summary>恢复出厂设置请求。</summary>
/// <param name="ExpectedVersion">期望版本；不一致返回 409。</param>
/// <param name="UpdatedBy">写入者标识。</param>
public sealed record ConfigResetRequest(int? ExpectedVersion = null, string? UpdatedBy = null);

/// <summary>
/// 图片上传结果（<c>POST /api/config/images</c>）。
/// </summary>
/// <param name="FileName">
/// 服务端生成的**单段文件名**（<c>img{GUID:N}{扩展名}</c>）。
/// 客户端提供的文件名被刻意忽略 —— 它是不可信输入，从根上不参与路径拼接。
/// </param>
/// <param name="Path">
/// 落盘后的**绝对路径**（含目录）。
/// <para>
/// ⚠️ 前端必须把**这个值**（不是 <see cref="FileName"/>）写进
/// <c>ButtonImage</c> / <c>SubButton.ButtonImage</c> / <c>DefaultButtonImage</c> /
/// <c>BackgroundImagePath</c>。原因：配置里这些字段的契约就是**服务端绝对路径**，
/// 而导出侧（<c>ConfigBackupService.BuildBackup</c>）是按 <c>File.Exists(字段值)</c> 收集图片的。
/// 若存成裸文件名，<c>File.Exists("imgX.png")</c> 会按**进程工作目录**求值而返回 false，
/// 那张图**不会被导出**，表现为"导出再导入后按钮图丢了"且全程无报错。
/// </para>
/// </param>
/// <param name="Bytes">实际写入的字节数。</param>
public sealed record ConfigImageUploadResultDto(string FileName, string Path, long Bytes);

/// <summary>
/// 图片仍被配置引用的拒绝响应（<c>DELETE /api/config/images/{fileName}</c> → 409）。
/// </summary>
/// <param name="Message">给人看的一句话（含"仍被 N 处引用"与强行删除的指引）。</param>
/// <param name="ReferencedBy">引用方的可读描述，如 <c>默认按钮图片</c>、<c>按钮「开灯」</c>。</param>
public sealed record ConfigImageInUseDto(string Message, IReadOnlyList<string> ReferencedBy);

/// <summary>
/// 配置变更通知（SignalR 广播载荷）。
/// <para>
/// 前端收到后**只需重新拉一次 <c>GET /api/config</c>** —— 不把整份配置塞进推送，
/// 避免大配置（含图片 base64 时可达数 MB）把实时通道撑爆。
/// </para>
/// </summary>
/// <param name="Key">文档键（当前恒为 <c>settings</c>）。</param>
/// <param name="Version">变更后的新版本号。</param>
/// <param name="ChangedBy">变更者。</param>
/// <param name="At">变更时间（北京时间）。</param>
public sealed record ConfigChangedDto(string Key, int Version, string ChangedBy, DateTimeOffset At);
