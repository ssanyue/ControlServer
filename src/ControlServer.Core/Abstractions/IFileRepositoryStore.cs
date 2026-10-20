using ControlServer.Core.Models;

namespace ControlServer.Core.Abstractions;

/// <summary>
/// 文件仓库的持久化端口（M4.4）。
/// <para>
/// <b>只负责"元数据行"</b>（<c>file_repo_items</c> / <c>file_upload_sessions</c>），
/// <b>不负责字节</b>：文件本身的读写在 <c>FileRepositoryService</c>（Core，走 System.IO）。
/// 这样分层的好处是"文件的物理布局"与"数据库长什么样"互不捆绑 ——
/// 后者由 EF provider 决定，前者由我们自己决定。
/// </para>
/// <para>实现见 Infrastructure 的 <c>EfFileRepositoryStore</c>。</para>
/// </summary>
public interface IFileRepositoryStore
{
    // ── 仓库条目 ──

    /// <summary>列出仓库条目（按上传时间倒序）。<paramref name="kind"/> 为 null 表示不过滤。</summary>
    Task<IReadOnlyList<FileRepoItem>> ListAsync(string? kind, CancellationToken cancellationToken = default);

    /// <summary>按 id 取一条（不存在返回 null）。</summary>
    Task<FileRepoItem?> FindAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增一条仓库条目并返回落库后的记录（含真正的 <c>Id</c>）。
    /// <para>⚠️ 传入值的 <c>Id</c> <b>会被忽略</b>（由数据库生成）。</para>
    /// </summary>
    Task<FileRepoItem> AddAsync(FileRepoItem item, CancellationToken cancellationToken = default);

    /// <summary>删除一条元数据。<b>不删磁盘文件</b> —— 那一步由服务层在删除成功后做。</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    // ── 上传会话 ──

    /// <summary>新建一个上传会话。</summary>
    Task CreateSessionAsync(FileUploadSession session, CancellationToken cancellationToken = default);

    /// <summary>按 id 取会话。</summary>
    Task<FileUploadSession?> FindSessionAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>列出已过期（或状态非 open）的会话，供清理任务回收它们的 <c>.part</c> 文件。</summary>
    Task<IReadOnlyList<FileUploadSession>> ListZombieSessionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出<b>全部</b>会话（含仍然 open 的）。
    /// <para>
    /// <b>为什么需要它</b>：<see cref="ListZombieSessionsAsync"/> 只返回"该回收的"，
    /// 所以拿它去判断"<c>uploads/</c> 里这个 <c>.part</c> 有没有主人"会得出<b>相反的结论</b> ——
    /// 一个<b>活着</b>的上传会话恰好<b>不在</b>僵尸列表里，从而被误判成无主文件删掉，
    /// 客户端续传时就会"从 0 字节重新开始"且没有任何报错。
    /// </para>
    /// <para>对账（<c>RepositoryGroomer</c>）必须用这个。</para>
    /// </summary>
    Task<IReadOnlyList<FileUploadSession>> ListSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>删掉一个会话记录。</summary>
    Task<bool> DeleteSessionAsync(string id, CancellationToken cancellationToken = default);
}
