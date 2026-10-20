using System.Diagnostics;
using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// <b>探针真的接在推流循环上</b>（M7.4 §6.2）—— 三处打点 + 一处出报告。
///
/// <para>
/// 🔥 <b>为什么不满足于"探针类自己的单测"</b>：那个类算得对，但它完全可能在循环里
/// <b>一处都没被调到</b> —— 而那种坏法<b>没有任何症状</b>，只是日志里永远少一行。
/// 现场于是会以为"这台机器太安静、没有耗时"，而不是"探针没接上"。
/// 本文件用一路<b>真的在推</b>的会话（桩抓屏 ＋ 桩编码 ＋ 真管道）把这条链走通。
/// </para>
///
/// <para>
/// ⚠️ 报告周期被压到 <b>150 ms</b>（生产是 5 秒）：这是本仓"用例不睡长觉"的一贯做法，
/// 停住的那 0.9 秒只为让循环转上几轮 —— 判据是"有没有那一行"，不是"数字准到毫秒"。
/// </para>
/// </summary>
public sealed class RemoteStreamProbeWiringTests
{
    private readonly List<string> _logs = new();

    [Fact]
    public async Task An_enabled_probe_reports_all_three_stages_on_a_live_stream()
    {
        var sessionId = "probe-on-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer();

        encoder.CapturerToReport = capturer;

        // 生产侧用的是默认构造（5 秒、真时钟）；这里只把周期压短。
        var probe = new RemoteStreamProbe(enabled: true, Stopwatch.GetTimestamp, TimeSpan.FromMilliseconds(150));

        await using var host = NewHost(encoder, capturer, probe);

        using var receiver = StartReceiver(sessionId);

        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 30))
            .Ok.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(900));

        var reports = Snapshot().Where(line => line.Contains("[Stream] 探针")).ToArray();

        reports.Should().NotBeEmpty("探针开着的时候，循环必须按周期交报告 —— 一行都没有 = 三处打点一处都没接上");

        reports.Should().ContainMatch(
            "*抓屏*编码*写管道*",
            "三段必须同时出现在同一行：只报一段的话，剩下两段照样只能靠猜");

        reports.Should().ContainMatch(
            "*目标 30*",
            "报告要带上**此刻生效的**目标帧率 —— 它是「改档有没有到托盘」唯一的自证");
    }

    [Fact]
    public async Task A_disabled_probe_stays_out_of_the_log()
    {
        var sessionId = "probe-off-" + Guid.NewGuid().ToString("N")[..8];

        var encoder = new ScriptedSurfaceEncoder();
        var capturer = new StubCapturer();

        encoder.CapturerToReport = capturer;

        var probe = new RemoteStreamProbe(enabled: false, Stopwatch.GetTimestamp, TimeSpan.FromMilliseconds(150));

        await using var host = NewHost(encoder, capturer, probe);

        using var receiver = StartReceiver(sessionId);

        host.Start(new RemoteStreamRequest(RemoteSessionActions.Start, sessionId, MaxFps: 30))
            .Ok.Should().BeTrue();

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // 🔥 这一条守的是"默认不许污染日志"：托盘那份日志本来就不长，
        //    一段每 5 秒一行的耗时报告会把真正的故障淹掉 —— 那是本仓反复踩过的坑。
        Snapshot().Should().NotContain(
            line => line.Contains("[Stream] 探针"),
            "探针默认关闭：没打开的时候一行都不该出现");
    }

    private string[] Snapshot()
    {
        lock (_logs) return _logs.ToArray();
    }

    private RemoteStreamHost NewHost(ScriptedSurfaceEncoder encoder, StubCapturer capturer, RemoteStreamProbe probe)
        => new(
            capturers: () => capturer,
            encoders: _ => encoder,
            injector: null,
            inputAllowed: () => false,
            log: line =>
            {
                lock (_logs) _logs.Add(line);
            },
            audioCapturers: null,
            audioEncoders: null,
            probes: () => probe);

    /// <summary>
    /// 接上这条会话的码流管道并把帧读掉（不接的话推流循环根本不会开始抓屏）。
    /// </summary>
    private static CancellationTokenSource StartReceiver(string sessionId)
    {
        var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".", RemoteStreamProtocol.PipeNameForSession(sessionId), PipeDirection.In, PipeOptions.Asynchronous);

                await client.ConnectAsync(3000, cts.Token);

                while (!cts.Token.IsCancellationRequested)
                {
                    var frame = await RemoteStreamProtocol.ReadFrameAsync(client, cts.Token);

                    if (frame is null) break;
                }
            }
            catch
            {
                // 收场 / 超时都无所谓：断言在用例里。
            }
        }, CancellationToken.None);

        return cts;
    }

    /// <summary>最小抓屏器：每轮都给一张"新画面"（这组用例要的是"有帧在流"）。</summary>
    private sealed class StubCapturer : IScreenCapturer
    {
        private static readonly ScreenDescriptor Primary =
            new("screen-primary", "主屏", 2, 2, IsPrimary: true);

        public IReadOnlyList<ScreenDescriptor> EnumerateScreens() => [Primary];

        public Task<CapturedFrame> CaptureAsync(string screenId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CapturedFrame(2, 2, 8, new byte[16], DateTimeOffset.UtcNow));

        public Task<CapturedSurface?> CaptureSurfaceAsync(
            string screenId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CapturedSurface?>(
                new CapturedSurface(2, 2, TextureHandle: 1, SurfacePixelFormat.Bgra8, false, DateTimeOffset.UtcNow));
    }

    /// <summary>脚本化编码器：每轮交出一段形状合法的 Annex-B 访问单元。</summary>
    private sealed class ScriptedSurfaceEncoder : ISurfaceFrameEncoder
    {
        private static readonly byte[] AccessUnit = VideoTestVectors.AnnexB(
            VideoTestVectors.RealH264Sps,
            VideoTestVectors.RealH264Pps,
            VideoTestVectors.RealH264IdrHead);

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public IScreenCapturer? CapturerToReport { get; set; }

        public IScreenCapturer Capturer => CapturerToReport
            ?? throw new InvalidOperationException("用例必须先给一个抓屏器（见 CapturerToReport 的说明）。");

        public string ScreenId => "screen-primary";

        public string ScreenName => "主屏";

        public (int Width, int Height) FrameSize => (2, 2);

        public string Codec => StreamCodecs.H264;

        public string? LastFailure => null;

        public long DiscardedFrames => 0;

        public bool Grayscale { get; set; }

        public bool TryEncodeSurface(CapturedSurface surface, bool forceKeyframe, out EncodedStreamPayload? encoded)
        {
            encoded = new EncodedStreamPayload(
                Codec: StreamCodecs.H264,
                IsKeyframe: true,
                Width: surface.Width,
                Height: surface.Height,
                CapturedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data: AccessUnit,
                Description: null,
                CapturedMonoTicks: _clock.ElapsedTicks);

            return true;
        }

        public bool TryReadParameterSets(out byte[] parameterSets)
        {
            parameterSets = [];

            return false;
        }

        public void Dispose()
        {
        }
    }
}
