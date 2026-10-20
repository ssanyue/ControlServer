using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 指令库端口。动作的数据类型为「指令库」时，<c>DataContent</c> 存的是**指令名**，
/// 发送前必须经这里解析成真实的「协议 + 数据类型 + 数据」。
/// <para>
/// 独立成端口（而不是让 Core 直接 new 一个实现）的理由：指令库要能被单测替换掉，
/// 让「动作执行」的测试向量不依赖数据库。
/// </para>
/// </summary>
public interface ICommandLibrary
{
    /// <summary>取全部指令。</summary>
    Task<IReadOnlyList<CommandItem>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按名称解析指令（<b>大小写不敏感</b>，与旧版 <c>CommandLibraryService.Resolve</c> 一致）。
    /// 名称为空或未找到返回 <c>null</c>。
    /// </summary>
    Task<CommandItem?> ResolveAsync(string? name, CancellationToken cancellationToken = default);
}
