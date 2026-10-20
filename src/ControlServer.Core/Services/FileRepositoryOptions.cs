namespace ControlServer.Core.Services;

/// <summary>
/// 文件仓库的行为参数（M4.4）。由组合根从配置注入（Core 不认识配置框架）。
/// </summary>
public sealed class FileRepositoryOptions
{
    /// <summary>
    /// 单文件大小上限（字节）。默认 <b>4 GB</b>。
    /// <para>
    /// 验收要求"&gt;2GB 文件断点续传成功"，所以这个上限必须明显大于 2GB；
    /// 同时它又是一道防呆线 —— 挡住"误选了一个 50GB 的镜像"把磁盘写爆。
    /// </para>
    /// </summary>
    public long MaxFileBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>分块大小（字节），默认 8 MB。服务端把它告诉客户端，避免两边各拍一个数。</summary>
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>上传会话的有效期（小时），默认 24。过期后 <c>.part</c> 会被清理任务回收。</summary>
    public int SessionExpiryHours { get; set; } = 24;

    /// <summary>
    /// 孤儿文件的回收宽限期（小时），默认 <b>24</b>。
    /// <para>
    /// 🔴 <b>这个值不是"随便给的保守值"，它是防一个具体竞态</b>：
    /// 完成上传时的顺序是「<c>File.Move(.part → repo)</c>」然后「<c>AddAsync</c> 落库」。
    /// 两步之间有窗口，此刻文件<b>已经在 repo 里、库里还没有记录</b> ——
    /// 与"孤儿"长得一模一样。如果对账按"不在库里就删"执行，就会删掉一个
    /// <b>刚刚上传成功、正等着落库</b>的文件，用户看到的是"上传成功但文件没了"。
    /// </para>
    /// <para>
    /// 所以判据必须是<b>文件年龄</b>（mtime 早于 now − 宽限期）而不是"本次扫描时不在库里"。
    /// 宽限期远大于那个窗口（毫秒级），留得宽是因为删错了不可恢复。
    /// </para>
    /// </summary>
    public int OrphanGraceHours { get; set; } = 24;

    /// <summary>宽限期的强类型形式（≤0 按 1 小时处理，不允许"不设宽限期"）。</summary>
    public TimeSpan OrphanGracePeriod => TimeSpan.FromHours(Math.Max(1, OrphanGraceHours));
}
