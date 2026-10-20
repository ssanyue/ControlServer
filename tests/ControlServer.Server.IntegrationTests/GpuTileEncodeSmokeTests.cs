using System.Diagnostics;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;
using Xunit.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>分块编码在真实 GPU 上真的能跑</b>（M5.5 第 4 项）。
///
/// <para>
/// 🔥🔥 <b>本机没有 8K 屏，所以这里用"把块宽压到 1024"来代替。</b>
/// 这两件事走的是<b>完全相同</b>的一条代码路径：
/// <c>EncodeRegionLayout.Tiles</c> → 每块一个 <c>TileLane</c>（各自的转换器 + 编码器）
/// → 每块一路独立码流 → <c>EncodedRegionPayload</c>。
/// 唯一的差别是数字大小，而"数字大小"不是我们代码的不确定性 ——
/// 它是<b>显卡/编码器能不能吃 8192 宽</b>，那由 <c>EncoderCapabilityProbe</c> 回答。
/// </para>
///
/// <para>
/// 也就是说：这条用例证明的是<b>"机制"</b>（切块、每块单独编码、几何与帧头正确），
/// 而不是"这台机器能编 8K"。后者换一台机器结果就不同，不该由一条用例来断言。
/// </para>
///
/// <para>
/// 🔴 <b>为什么必须真跑</b>：分块最危险的三个错误<b>都不会报错</b> ——
/// ① 各块塞进同一个编码器 ⇒ 参考链互相污染，画面花但码流"成功"；
/// ② 帧头把块尺寸当成画面尺寸 ⇒ 前端画布缩成一角；
/// ③ 某块的 SPS/PPS 用了第 0 块的 ⇒ 宽度不一致时直接花屏。
/// 这三条里，①③ 只有真的编出来才看得见。
/// </para>
/// </summary>
[Collection(DesktopDuplicationCollection.Name)]
public sealed class GpuTileEncodeSmokeTests
{
    private readonly ITestOutputHelper _output;

    public GpuTileEncodeSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Tiled_encode_produces_one_independent_stream_per_region()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sessionId = Process.GetCurrentProcess().SessionId;

        // 1024 是个"一定会切成好几块"的值（本机 2160 宽 ⇒ 3 块）。
        const int tileWidth = 1024;

        var encoder = WindowsGpuFrameEncoder.TryCreate(
            sessionId,
            VideoCodec.H264,
            fps: 30,
            out var verdict,
            maxTileWidth: tileWidth,
            disablePreprocessing: true);

        if (encoder is null)
        {
            // 与 DXGI 冒烟同一条口径：环境不允许时不造假 FAIL（理由见 DxgiCaptureSmokeTests）。
            _output.WriteLine($"跳过：GPU 管线建不起来（{verdict?.Stage}）。{verdict?.Reason}");
            return;
        }

        using (encoder)
        {
            var surface = await CaptureAnyAsync(encoder.Capturer);

            if (surface is null)
            {
                _output.WriteLine("跳过：一帧都抓不到（桌面/会话状态不允许抓屏）。");
                return;
            }

            _output.WriteLine($"源画面 {surface.Width}×{surface.Height}，单块上限 {tileWidth}。");

            var regions = encoder.Regions;

            // ── 几何：切开了、没超限、**一个像素都没少** ──
            regions.Count.Should().BeGreaterThan(
                1,
                $"{surface.Width} 宽超过单块上限 {tileWidth} ⇒ 必须切成多块");

            regions.Should().OnlyContain(
                r => r.Width <= tileWidth,
                "单块宽度不能超过上限 —— 超了就是「切了等于没切」，而编码器多半会拒绝或糊掉");

            regions.Sum(r => r.Width).Should().Be(
                surface.Width,
                "各块宽度之和必须等于源宽：少一个像素，画面右边就会缺一条（且不报错）");

            regions.Select(r => r.X).Should().BeInAscendingOrder("块必须从左到右排列");

            // ── 编码：每块各自出一路码流 ──
            //
            // 🔥 **必须连喂几帧，不能只喂一帧**：异步 MFT 的输出**晚一次调用才到**
            //    （M5.4-b 实测：喂 17 帧出 16 帧）。第一帧返回 false 完全正常，
            //    而把"第一帧没出来"当成"分块编码坏了"，正是那条老用例
            //    `RemoteStreamOversizeTests` 曾经踩过的方向。
            //    ⚠️ 这里连喂的是**同一张 surface**：我们要的是"每块都能编出码流"，
            //    与画面有没有变化无关（静止检测在 Agent 那一层，不在这里）。
            List<EncodedRegionPayload>? batch = null;

            for (var attempt = 0; attempt < 6 && batch is null; attempt++)
            {
                var candidate = new List<EncodedRegionPayload>(regions.Count);

                encoder.TryEncodeSurfaceRegions(surface, forceKeyframe: true, candidate);

                // ⚠️ 判据是"**每块**都出齐"，不是"方法返回 true"：
                //    第 0 块成功、后续块失败时它照样返回 false，但 candidate 里已经有货了。
                if (candidate.Count == regions.Count) batch = candidate;
                else await Task.Delay(40);
            }

            batch.Should().NotBeNull(
                $"连喂 6 帧都没凑齐 {regions.Count} 块。最近一次失败：{encoder.LastFailure}");

            var tiles = batch!;

            tiles.Should().HaveCount(
                regions.Count,
                "一块一路码流：数量对不上说明有块被静默跳过了（那块的区域在画面上会一直停在旧内容）");

            tiles.Select(b => b.TileIndex).Should().Equal(
                Enumerable.Range(0, regions.Count),
                "块序号必须从 0 连续递增 —— 前端按它选缓冲与画布位置");

            tiles.Should().OnlyContain(
                b => b.TileCount == regions.Count,
                "每块都要带总数：前端靠它判断「块数变了，全部缓冲作废」");

            tiles.Select(b => b.Region).Should().Equal(
                regions,
                "帧头里的区域必须与编码器算出来的清单逐项一致 —— 差一块位置，画面就会错位");

            tiles.Should().OnlyContain(
                b => b.FrameWidth == surface.Width && b.FrameHeight == surface.Height,
                "🔥 FrameWidth/Height 必须报**源画面**尺寸，不是这一块的尺寸 —— "
                + "报成块尺寸会让前端画布缩成一角，而且不报错");

            tiles.Should().OnlyContain(
                b => b.Stream.Data.Length > 0,
                "每块都必须真的编出字节");

            tiles.Should().OnlyContain(
                b => b.Stream.IsKeyframe,
                "🔥 强制关键帧时**每一块**都必须是关键帧：每块是独立码流，"
                + "P 帧只能参考同一路 —— 某块从 P 帧开始，那个区域的解码器就永远起不来");

            foreach (var tile in tiles)
            {
                _output.WriteLine(
                    $"  块 {tile.TileIndex}/{tile.TileCount} {tile.Region} "
                    + $"码流 {tile.Stream.Data.Length} 字节 关键帧={tile.Stream.IsKeyframe}");
            }

            // 描述串是现场唯一的"这一路到底切成几块"的入口，别让它撒谎。
            encoder.Describe().Should().Contain(
                $"{regions.Count} 块",
                "Describe() 是托盘日志与页面说明的来源，块数写错就没人能核对");
        }
    }

    /// <summary>
    /// 抓一帧；桌面完全静止时 DDA 会回 <c>null</c>（那不是失败），所以重试几次。
    /// </summary>
    private static async Task<CapturedSurface?> CaptureAnyAsync(IScreenCapturer capturer, int attempts = 6)
    {
        for (var i = 0; i < attempts; i++)
        {
            // ⚠️ `null` = 主屏。接口签名标成非空，但实现明确接受 null（见 EnsureBound 的注释），
            //    这里用 `null!` 而不是改接口 —— 改签名会波及所有平台实现。
            var captured = await capturer.CaptureSurfaceAsync(null!, CancellationToken.None).ConfigureAwait(false);

            if (captured is not null) return captured;

            await Task.Delay(120).ConfigureAwait(false);
        }

        return null;
    }
}
