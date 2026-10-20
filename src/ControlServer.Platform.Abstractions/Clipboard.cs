namespace ControlServer.Platform.Abstractions;

/// <summary>
/// <b>本机剪贴板的文本读写</b>（M5.5）。
///
/// <para>
/// 🔥 <b>为什么它是平台抽象、而且刻意只做文本</b>：
/// 剪贴板里能放图片、文件列表、私有格式，而"把 200 MB 的文件列表从 A 传 B"
/// 要的是文件传输，不是剪贴板 —— 拿这条通道传文件，会把一个 60 KB 的消息
/// 变成几十 MB，而接收方会先卡死再报一个谁也看不懂的错。
/// 所以本接口<b>只承诺文本</b>，其余格式一律回答"这里没有文本"。
/// </para>
///
/// <para>
/// ⚠️ <b>Windows 上剪贴板是全局独占资源</b>：某个程序打开它的时候别人打不开。
/// 所以读/写都可能<b>短暂失败</b>，实现必须重试几次（见 Windows 那份实现），
/// 而不是第一次失败就报"剪贴板坏了"。
/// </para>
/// </summary>
public interface IClipboardAccess
{
    /// <summary>
    /// 读本机剪贴板里的文本。
    /// </summary>
    /// <returns>
    /// 文本；<c>null</c> = <b>剪贴板里没有文本</b>（有图片 / 空 / 被占用）。
    /// 失败原因写在 <paramref name="note"/> 里，且只在"真的失败"时有值 ——
    /// "没有文本"不是失败。
    /// </returns>
    string? ReadText(out string? note);

    /// <summary>把文本写进本机剪贴板。</summary>
    /// <returns>成功与否；失败原因写在 <paramref name="note"/> 里。</returns>
    bool WriteText(string text, out string? note);
}

/// <summary>
/// <b>剪贴板不可用时的空实现</b>（非 Windows / 尚未接线）。
/// <para>
/// ⚠️ 它存在的理由与 <c>UnsupportedAudioController</c> 一样：让调用方<b>一个 if 都不用写</b>，
/// 拿到的是一句可读的原因，而不是 <c>NullReferenceException</c>。
/// </para>
/// </summary>
public sealed class UnsupportedClipboardAccess : IClipboardAccess
{
    private readonly string _reason;

    public UnsupportedClipboardAccess(string reason) => _reason = reason;

    /// <inheritdoc />
    public string? ReadText(out string? note)
    {
        note = _reason;
        return null;
    }

    /// <inheritdoc />
    public bool WriteText(string text, out string? note)
    {
        note = _reason;
        return false;
    }
}
