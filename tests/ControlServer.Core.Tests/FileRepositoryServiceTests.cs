using System.Security.Cryptography;
using System.Text;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <see cref="FileRepositoryService"/> 的用例（M4.4）：分块上传状态机 + 原子落地。
/// <para>
/// 这一组用<b>真·临时目录</b>（不是 mock 文件系统）—— 要验的恰恰是"文件到底落在哪、
/// 有没有留半个、回滚有没有把 <c>.part</c> 挪回来"。mock 掉 IO 就等于把被测对象删了一半。
/// </para>
/// </summary>
public sealed class FileRepositoryServiceTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeFileRepositoryStore _store = new();

    public void Dispose() => _dir.Dispose();

    private FileRepositoryService NewService(long maxBytes = 1024 * 1024)
        => new(
            _store,
            _dir,
            new LogService(new NoopDataDirectory(), flushIntervalMs: 60_000),
            new FileRepositoryOptions { MaxFileBytes = maxBytes });

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Task<FileUploadAppendResult> AppendAsync(FileRepositoryService svc, string id, long offset, byte[] data)
        => svc.AppendChunkAsync(id, offset, new MemoryStream(data));

    // ═══════════════════════════════════════════════════════════
    // 创建会话
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_rejects_a_non_positive_size()
    {
        var svc = NewService();

        (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.txt", 0), "admin", null)).Status
            .Should().Be(FileUploadCreateStatus.Invalid);
        (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.txt", -5), "admin", null)).Status
            .Should().Be(FileUploadCreateStatus.Invalid);
    }

    [Fact]
    public async Task Create_rejects_a_file_over_the_configured_limit()
    {
        var svc = NewService(maxBytes: 100);

        var result = await svc.CreateSessionAsync(new FileUploadCreateRequest("big.bin", 500), "admin", null);

        result.Status.Should().Be(FileUploadCreateStatus.TooLarge,"单文件上限是一道防呆线：挡住误选一个 50GB 镜像把磁盘写爆");
        result.Session.Should().BeNull();
    }

    [Fact]
    public async Task Create_sanitizes_the_display_name_and_generates_a_stored_name()
    {
        var svc = NewService();

        var result = await svc.CreateSessionAsync(
            new FileUploadCreateRequest(@"..\..\etc\passwd", 10), "admin", null);

        result.Status.Should().Be(FileUploadCreateStatus.Ok);

        var session = result.Session!;
        session.FileName.Should().Be("passwd", "展示名只取最后一段");
        session.StoredName.Should().StartWith("f_").And.NotContain("..", "落盘名由服务端生成，不含任何目录成分");
        session.State.Should().Be(FileUploadStates.Open);
    }

    [Fact]
    public async Task Create_honors_a_forced_kind_and_defaults_unknown_kinds_to_normal()
    {
        var svc = NewService();

        // 客户机发起的上传强制 collected（不让它自称 kind，否则能把回传伪装成 installer）。
        var forced = await svc.CreateSessionAsync(
            new FileUploadCreateRequest("a.bin", 10, FileKinds.Installer), null, 7, forcedKind: FileKinds.Collected);

        forced.Session!.Kind.Should().Be(FileKinds.Collected, "forcedKind 覆盖客户端自报的 kind");
        forced.Session.OriginAgentId.Should().Be(7);

        var unknown = await svc.CreateSessionAsync(
            new FileUploadCreateRequest("b.bin", 10, "something-weird"), "admin", null);

        unknown.Session!.Kind.Should().Be(FileKinds.Normal, "未知 kind 退回 normal，而不是原样落库");
    }

    [Fact]
    public async Task Create_writes_an_empty_part_file_that_is_the_source_of_truth_for_progress()
    {
        var svc = NewService();

        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 100), "admin", null)).Session!;

        // ⚠️ 已收字节的唯一事实来源是 .part 的文件长度，不是数据库计数器。
        svc.ReceivedBytes(session).Should().Be(0);
        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part")).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // 追加分块（续传的核心）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Append_accepts_chunks_at_the_current_offset()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 6), "admin", null)).Session!;

        var first = await AppendAsync(svc, session.Id, 0, Bytes("abc"));
        first.Status.Should().Be(FileUploadAppendStatus.Ok);
        first.ReceivedBytes.Should().Be(3);

        var second = await AppendAsync(svc, session.Id, 3, Bytes("def"));
        second.Status.Should().Be(FileUploadAppendStatus.Ok);
        second.ReceivedBytes.Should().Be(6);
    }

    /// <summary>
    /// 🔥 偏移不匹配返回 <see cref="FileUploadAppendStatus.OffsetMismatch"/> + <b>真实长度</b>。
    /// <para>
    /// <b>这不是错误，是续传的正常一环</b>：客户端断线重连后先问"收到哪了"，
    /// 服务端回真实偏移，客户端从那里接着传。若允许"按任意偏移写"，
    /// 两个并发的重试就会把文件拼成一段怪物 —— 而它的长度甚至可能正好等于声明大小，
    /// 从而骗过长度校验。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Append_rejects_a_mismatched_offset_and_reports_the_real_length()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 100), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, Bytes("0123456789")); // 已收 10

        var mismatch = await AppendAsync(svc, session.Id, 999, Bytes("xyz"));

        mismatch.Status.Should().Be(FileUploadAppendStatus.OffsetMismatch);
        mismatch.ReceivedBytes.Should().Be(10, "要把真实进度回给客户端，它才能重新对齐");
    }

    [Fact]
    public async Task Append_that_would_overflow_is_rejected_and_truncated_back()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 5), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, Bytes("abc")); // 3 / 5

        var overflow = await AppendAsync(svc, session.Id, 3, Bytes("DEFGH")); // 3 + 5 = 8 > 5

        overflow.Status.Should().Be(FileUploadAppendStatus.TooLarge);
        overflow.ReceivedBytes.Should().Be(3, "写多了要截回去，让会话保持可继续的状态（不是清空重来）");

        // 会话仍然可用：补上剩下的 2 个字节即可完成。
        var retry = await AppendAsync(svc, session.Id, 3, Bytes("DE"));
        retry.Status.Should().Be(FileUploadAppendStatus.Ok);
        retry.ReceivedBytes.Should().Be(5);
    }

    [Fact]
    public async Task Append_on_a_missing_session_is_not_found()
    {
        var svc = NewService();

        (await AppendAsync(svc, "u-does-not-exist", 0, Bytes("x"))).Status
            .Should().Be(FileUploadAppendStatus.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 完成
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Complete_before_all_bytes_arrive_is_incomplete()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 10), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, Bytes("abc"));

        var result = await svc.CompleteSessionAsync(session.Id, null);

        result.Status.Should().Be(FileUploadCompleteStatus.Incomplete);
        result.ReceivedBytes.Should().Be(3);
        result.Item.Should().BeNull();
    }

    /// <summary>
    /// 摘要不符 → 拒绝入库，但<b>保留会话</b>：客户端可能只是传错了文件，
    /// 重传一遍比重新建会话省事（而且大数据重传中途可续）。
    /// </summary>
    [Fact]
    public async Task Complete_with_a_mismatched_hash_keeps_the_session_for_a_retry()
    {
        var svc = NewService();
        var payload = Bytes("hello");

        var session = (await svc.CreateSessionAsync(
            new FileUploadCreateRequest("a.bin", payload.Length, Sha256: new string('0', 64)), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, payload);

        var result = await svc.CompleteSessionAsync(session.Id, null);

        result.Status.Should().Be(FileUploadCompleteStatus.HashMismatch);
        _store.Items.Should().BeEmpty("摘要不符绝不能入库");
        (await _store.FindSessionAsync(session.Id)).Should().NotBeNull("保留会话，让客户端能直接重传");
        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part")).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_moves_the_file_into_the_repository_and_records_metadata()
    {
        var svc = NewService();
        var payload = Bytes("the quick brown fox");

        var session = (await svc.CreateSessionAsync(
            new FileUploadCreateRequest("report.txt", payload.Length, Sha256: Sha(payload)), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, payload);

        var result = await svc.CompleteSessionAsync(session.Id, null);

        result.Status.Should().Be(FileUploadCompleteStatus.Ok);

        var item = result.Item!;
        item.Name.Should().Be("report.txt");
        item.Size.Should().Be(payload.Length);
        item.Sha256.Should().Be(Sha(payload), "摘要是服务端自己算的");
        item.UploadedBy.Should().Be("admin");

        _store.Items.Should().ContainSingle();

        // 文件确实落进了仓库目录，内容一字不差；暂存 .part 已经不在（是 Move 走的，不是复制）。
        var repoPath = Path.Combine(svc.RepositoryDirectory, item.StoredName);
        File.Exists(repoPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(repoPath)).Should().Equal(payload);
        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part"))
            .Should().BeFalse("完成即原子落位，暂存文件不该残留");

        // 会话完成即注销：它没有"已完成"状态，完成就是删除。
        (await _store.FindSessionAsync(session.Id)).Should().BeNull();
    }

    /// <summary>
    /// 🔥 落库失败 ⇒ <b>把文件挪回暂存</b>，绝不在仓库里留一个"没有元数据的孤儿文件"。
    /// <para>孤儿文件不会被列表显示、不会被删除、只会在磁盘上越积越多 —— 运维永远发现不了。</para>
    /// </summary>
    [Fact]
    public async Task Complete_rolls_the_file_back_when_the_metadata_write_fails()
    {
        var svc = NewService();
        var payload = Bytes("rollback me");

        var session = (await svc.CreateSessionAsync(
            new FileUploadCreateRequest("a.bin", payload.Length, Sha256: Sha(payload)), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, payload);

        _store.FailAdd = true;

        var result = await svc.CompleteSessionAsync(session.Id, null);

        result.Status.Should().Be(FileUploadCompleteStatus.WriteFailed);

        File.Exists(Path.Combine(svc.RepositoryDirectory, session.StoredName))
            .Should().BeFalse("落库失败后仓库里不能留下孤儿文件");
        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part"))
            .Should().BeTrue("文件已被挪回暂存，会话仍可继续");
    }

    [Fact]
    public async Task Complete_of_a_missing_session_is_not_found()
    {
        var svc = NewService();

        (await svc.CompleteSessionAsync("u-nope", null)).Status.Should().Be(FileUploadCompleteStatus.NotFound);
    }

    // ═══════════════════════════════════════════════════════════
    // 中止 / 过期回收 / 删除
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Abort_removes_the_part_file_and_the_session()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 10), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, Bytes("abc"));

        (await svc.AbortSessionAsync(session.Id)).Should().BeTrue();

        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part")).Should().BeFalse();
        (await _store.FindSessionAsync(session.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Purge_reclaims_expired_sessions_and_their_part_files()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 10), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, Bytes("abc"));

        // 把它改成已经过期（用同一个 Id 覆写记录）。
        await _store.CreateSessionAsync(session with { ExpiresAt = DateTimeOffset.Now.AddHours(-1) });

        var purged = await svc.PurgeExpiredSessionsAsync();

        purged.Should().Be(1);
        File.Exists(Path.Combine(svc.StagingDirectory, session.StoredName + ".part")).Should().BeFalse();
        (await _store.FindSessionAsync(session.Id)).Should().BeNull();
    }

    /// <summary>
    /// 读的时候就把过期判定做掉 —— 不等后台任务。
    /// <para>后台任务可能还没跑到，过期会话就会被用上（继续往里写）。</para>
    /// </summary>
    [Fact]
    public async Task Find_session_reports_an_expired_session_as_expired()
    {
        var svc = NewService();
        var session = (await svc.CreateSessionAsync(new FileUploadCreateRequest("a.bin", 10), "admin", null)).Session!;

        await _store.CreateSessionAsync(session with { ExpiresAt = DateTimeOffset.Now.AddHours(-1) });

        var found = await svc.FindSessionAsync(session.Id);

        found!.State.Should().Be(FileUploadStates.Expired, "过期判定在读取时做，不靠后台任务抢先标记");

        // 过期会话不能再追加。
        (await AppendAsync(svc, session.Id, 0, Bytes("x"))).Status
            .Should().Be(FileUploadAppendStatus.NotOpen);
    }

    [Fact]
    public async Task Delete_removes_the_metadata_and_the_disk_file()
    {
        var svc = NewService();
        var payload = Bytes("delete me");

        var session = (await svc.CreateSessionAsync(
            new FileUploadCreateRequest("a.bin", payload.Length, Sha256: Sha(payload)), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, payload);
        var item = (await svc.CompleteSessionAsync(session.Id, null)).Item!;

        var repoPath = Path.Combine(svc.RepositoryDirectory, item.StoredName);
        File.Exists(repoPath).Should().BeTrue();

        (await svc.DeleteAsync(item.Id)).Should().BeTrue();

        _store.Items.Should().BeEmpty();
        File.Exists(repoPath).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_of_a_missing_file_returns_false()
        => (await NewService().DeleteAsync(4242)).Should().BeFalse();

    [Fact]
    public async Task Resolve_stored_path_stays_inside_the_repository()
    {
        var svc = NewService();
        var payload = Bytes("x");

        var session = (await svc.CreateSessionAsync(
            new FileUploadCreateRequest("a.bin", payload.Length, Sha256: Sha(payload)), "admin", null)).Session!;

        await AppendAsync(svc, session.Id, 0, payload);
        var item = (await svc.CompleteSessionAsync(session.Id, null)).Item!;

        FileTransferMath.IsInside(svc.RepositoryDirectory, svc.ResolveStoredPath(item))
            .Should().BeTrue("落盘名是服务端生成的单段名，解析出的路径必然在仓库目录内");
    }
}
