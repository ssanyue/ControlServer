using System.Net;
using ControlServer.Agent.Transfer;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 回传（读）方向的<b>范围</b>：<b>不限制目录</b>（2026-10-15 用户拍板）。
///
/// <para>
/// <b>为什么这条演进值得写下来</b>：这里曾经是"白名单"，而且是本项目唯一的安全边界之一。
/// 它的失败模式很有代表性 —— 浏览（看得见什么）与回传（读得到什么）各自维护一份集合，
/// 只改了其中一处，于是"在浏览里挑好的文件"建单后必然被拒，现场同一条路径连撞 5 次
/// （<c>源路径不在本机白名单目录内：D:\…（允许：…\outbox）</c>）。
/// 先修成"默认可读整盘、可配置收窄"，用户随后要求<b>连同机制一起删掉</b>：
/// 留一个"配了才生效"的收窄开关只是把陷阱换个位置。
/// </para>
///
/// <para>
/// 所以这一组用例现在钉的是<b>三件别再退回去的事</b>：
/// ① 任意绝对路径都能过（含系统目录、别的盘）；
/// ② 相对路径仍然被拒（"客户机进程的当前目录"随安装方式变化 ⇒ 会取到不一样的文件）；
/// ③ 空路径 / 文件不存在仍然是<b>致命</b>错误（不重试）。
/// </para>
/// </summary>
public sealed class TransferReadScopeTests
{
    /// <summary>永远回 500 的桩：用来把"过没过范围校验"这件事和"服务端能不能用"彻底分开。</summary>
    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private static string CurrentDriveRoot
        => Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;

    private static string StateDirectory
        => Path.Combine(Path.GetTempPath(), "cs-read-scope-state", Guid.NewGuid().ToString("N"));

    // ═══════════════════════════════════════════════════════════
    // 一、不限制目录：越"不该"的位置越要测
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>别人家的目录</b>（既不是状态目录、也不是下发目录、也不在任何可配置名单里）必须能过。
    /// 失败点应当推进到"建上传会话"（桩回 500）—— 证明拦截已经不在这里。
    /// </summary>
    [Fact]
    public async Task Collect_from_an_unrelated_directory_reaches_the_upload_session()
    {
        var source = Path.Combine(Path.GetTempPath(), $"cs-read-scope-src-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(source, "现场日志内容");

        try
        {
            var (client, log) = NewClient(new TransferOptions());

            await using (log)
            {
                var error = await Record.ExceptionAsync(
                    () => client.UploadAsync(CollectTaskFor(source), (_, _) => Task.CompletedTask, CancellationToken.None));

                var transfer = error.Should().BeOfType<TransferException>().Subject;

                transfer.Fatal.Should().BeFalse("范围校验不该拦它；失败点是那台桩服务端的 500");
                transfer.Message.Should().NotContain("白名单");
                transfer.Message.Should().Contain("创建上传会话失败");
            }
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// <b>系统目录</b>也照样能取（<c>C:\Windows\System32\drivers\etc\hosts</c> 是每台机器都有的、
    /// 最典型的"以前必然被拒"的路径）。这里只断言"没被范围拦下" —— 文件本身是否可读
    /// （ACL / 占用）是另一回事，不该混进这条用例。
    /// </summary>
    [Fact]
    public async Task Collect_from_a_system_directory_is_not_blocked_by_any_scope()
    {
        var systemPath = Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory) ?? CurrentDriveRoot,
            "Windows", "System32", "drivers", "etc", "hosts");

        if (!File.Exists(systemPath))
            return; // 非 Windows / 精简系统：这条没有可断言的对象。

        var (client, log) = NewClient(new TransferOptions());

        await using (log)
        {
            var error = await Record.ExceptionAsync(
                () => client.UploadAsync(CollectTaskFor(systemPath), (_, _) => Task.CompletedTask, CancellationToken.None));

            var transfer = error.Should().BeOfType<TransferException>().Subject;

            transfer.Message.Should().NotContain("白名单");
            transfer.Message.Should().Contain("创建上传会话失败", "唯一允许的失败点是那台桩服务端");
        }
    }

    /// <summary>
    /// <b>别的盘</b>（只要本机真有第二个盘）同样不设限。单盘机器上这条跳过。
    /// </summary>
    [Fact]
    public async Task Collect_from_another_drive_is_allowed_too()
    {
        var other = DriveInfo.GetDrives()
            .Select(d => d.Name)
            .FirstOrDefault(n => !string.Equals(n, CurrentDriveRoot, StringComparison.OrdinalIgnoreCase)
                                 && Directory.Exists(n));

        if (other is null) return; // 单盘机器，无可断言对象。

        var (client, log) = NewClient(new TransferOptions());

        await using (log)
        {
            // 不真去碰那个盘上的文件：只把"另一个盘上的路径"喂进去看它放不放行。
            var error = await Record.ExceptionAsync(
                () => client.UploadAsync(
                    CollectTaskFor(Path.Combine(other, "cs-read-scope-probe.txt")),
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None));

            var transfer = error.Should().BeOfType<TransferException>().Subject;

            transfer.Message.Should().NotContain("白名单");
            transfer.Message.Should().StartWith("源文件不存在", "路径本身先要过关，然后才轮到文件存在性");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 二、取消的只是"范围"，不是"路径合法性"
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 相对路径必须仍然被拒，而且是<b>致命</b>的。
    /// <para>
    /// 它会以"客户机进程的当前目录"为基准，而那个目录随安装方式（服务 / 托盘 / 调试）变化 ——
    /// 同一份配置在两台机器上取到不同的文件，是比"取不到"更难查的一种错。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_relative_source_path_is_still_rejected_as_fatal()
    {
        var (client, log) = NewClient(new TransferOptions());

        await using (log)
        {
            var error = await Record.ExceptionAsync(
                () => client.UploadAsync(
                    CollectTaskFor(@"logs\field-log.txt"),
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None));

            var transfer = error.Should().BeOfType<TransferException>().Subject;

            transfer.Fatal.Should().BeTrue("重试也不会变绝对");
            transfer.Message.Should().Contain("绝对路径");
        }
    }

    /// <summary>空源路径仍然是致命错误（不重试、不猜）。</summary>
    [Fact]
    public async Task An_empty_source_path_is_fatal()
    {
        var (client, log) = NewClient(new TransferOptions());

        await using (log)
        {
            var error = await Record.ExceptionAsync(
                () => client.UploadAsync(
                    CollectTaskFor(null!),
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None));

            var transfer = error.Should().BeOfType<TransferException>().Subject;

            transfer.Fatal.Should().BeTrue();
            transfer.Message.Should().Contain("没有指定源路径");
        }
    }

    /// <summary>绝对路径但文件不在 ⇒ 致命（这在"范围放开"之后成了最常见的失败原因）。</summary>
    [Fact]
    public async Task A_missing_source_file_is_fatal()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cs-read-scope-missing-{Guid.NewGuid():N}.txt");

        var (client, log) = NewClient(new TransferOptions());

        await using (log)
        {
            var error = await Record.ExceptionAsync(
                () => client.UploadAsync(CollectTaskFor(missing), (_, _) => Task.CompletedTask, CancellationToken.None));

            var transfer = error.Should().BeOfType<TransferException>().Subject;

            transfer.Fatal.Should().BeTrue();
            transfer.Message.Should().Contain("源文件不存在");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 私有
    // ═══════════════════════════════════════════════════════════

    private static AgentTransferTaskDto CollectTaskFor(string sourcePath)
        => new(
            ItemId: 1,
            TaskId: 1,
            Direction: DistributionDirections.Collect,
            FileId: 0,
            FileName: Path.GetFileName(sourcePath ?? string.Empty),
            Size: 0,
            Sha256: null,
            TargetDir: null,
            SourcePath: sourcePath);

    private static (FileTransferClient Client, LogService Log) NewClient(TransferOptions options)
    {
        var log = new LogService(
            new TestDataDirectory(Path.Combine(Path.GetTempPath(), "cs-read-scope-logs", Guid.NewGuid().ToString("N"))),
            capacity: 200,
            flushIntervalMs: 600_000);

        // ⚠️ 两个 HttpClient 都用桩：本组用例只关心"范围放不放行"，
        //    真去连服务端只会把这件事和网络搅在一起。
        var client = new FileTransferClient(
            new HttpClient(new RejectingHandler()),
            new HttpClient(new RejectingHandler()),
            "http://127.0.0.1:1",
            "test-agent-key",
            options,
            StateDirectory,
            log);

        return (client, log);
    }
}
