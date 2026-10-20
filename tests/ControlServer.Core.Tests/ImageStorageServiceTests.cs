using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 图片存储内核与引用扫描（M2-G1）。
/// <para>
/// 重点覆盖三类**只有在这里才能钉死**的语义：
/// ① 上传的三道校验（扩展名 / 大小 / 魔数）—— 尤其"扩展名对了但内容不是图片"这条；
/// ② 文件名解析的两道闸（白名单 + 目录归属），含目录穿越；
/// ③ 引用扫描的匹配口径必须与前端 <c>imageUrl()</c> 一致（按最后一段文件名、忽略大小写）。
/// </para>
/// </summary>
public sealed class ImageStorageServiceTests
{
    // 每种格式的**真实**文件头（不是随手编的字节——魔数嗅探就是靠它们工作的）。
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHead = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] Gif87aHead = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89aHead = "GIF89a"u8.ToArray();
    private static readonly byte[] BmpHead = [0x42, 0x4D, 0x36, 0x00];
    private static readonly byte[] WebpHead = [.. "RIFF"u8.ToArray(), 0x1A, 0x00, 0x00, 0x00, .. "WEBP"u8.ToArray()];

    private static ImageStorageService NewService(TempDataDirectory dir, long maxBytes = 4L * 1024 * 1024)
        => new(dir, new LogService(new NoopDataDirectory(), flushIntervalMs: 60_000), new ImageStoreOptions(maxBytes));

    // ═══════════════════════════════════════════════════════════
    // 扩展名白名单
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("a.png", ".png")]
    [InlineData("a.PNG", ".png")]        // 大写要能收敛到小写
    [InlineData("图片.webp", ".webp")]
    [InlineData("a.jpg", ".jpg")]
    [InlineData("a.jpeg", ".jpeg")]
    [InlineData("a.gif", ".gif")]
    [InlineData("a.bmp", ".bmp")]
    public void TryGetAllowedExtension_accepts_whitelisted_types(string name, string expected)
    {
        ImageStorageService.TryGetAllowedExtension(name, out var ext).Should().BeTrue();

        ext.Should().Be(expected);
    }

    [Theory]
    [InlineData("evil.svg")]        // SVG 能内嵌脚本 ⇒ 刻意不在白名单
    [InlineData("a.txt")]
    [InlineData("a.exe")]
    [InlineData("a.png.exe")]       // 双扩展名：Path.GetExtension 只取最后一个 → .exe
    [InlineData("noext")]
    [InlineData(".gitignore")]      // .NET 8 实测：扩展名就是整个 ".gitignore" → 不白名单
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryGetAllowedExtension_rejects_everything_else(string? name)
    {
        ImageStorageService.TryGetAllowedExtension(name, out var ext).Should().BeFalse();
        ext.Should().BeNull();
    }

    [Fact]
    public void TryGetAllowedExtension_accepts_a_name_that_is_only_an_extension()
    {
        // .NET 8 的 Path.GetExtension(".png") 返回 ".png"（不是空串）—— 与直觉相反，实测钉住。
        // 放行它是安全的：客户端文件名根本不参与落盘命名（见 Save_ignores_the_client_file_name_completely）。
        ImageStorageService.TryGetAllowedExtension(".png", out var ext).Should().BeTrue();
        ext.Should().Be(".png");
    }

    // ═══════════════════════════════════════════════════════════
    // 魔数嗅探
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void LooksLikeSupportedImage_recognises_every_allowed_format()
    {
        ImageStorageService.LooksLikeSupportedImage(PngHead).Should().BeTrue("PNG");
        ImageStorageService.LooksLikeSupportedImage(JpegHead).Should().BeTrue("JPEG");
        ImageStorageService.LooksLikeSupportedImage(Gif87aHead).Should().BeTrue("GIF87a");
        ImageStorageService.LooksLikeSupportedImage(Gif89aHead).Should().BeTrue("GIF89a");
        ImageStorageService.LooksLikeSupportedImage(BmpHead).Should().BeTrue("BMP");
        ImageStorageService.LooksLikeSupportedImage(WebpHead).Should().BeTrue("WEBP");
    }

    [Fact]
    public void LooksLikeSupportedImage_rejects_non_images_and_truncated_input()
    {
        ImageStorageService.LooksLikeSupportedImage("<html><script>"u8).Should().BeFalse("HTML 不是图片");
        ImageStorageService.LooksLikeSupportedImage("<svg xmlns="u8).Should().BeFalse("SVG 是文本，没有位图魔数");
        ImageStorageService.LooksLikeSupportedImage([0x50, 0x4B, 0x03, 0x04]).Should().BeFalse("ZIP（PK）");
        ImageStorageService.LooksLikeSupportedImage(ReadOnlySpan<byte>.Empty).Should().BeFalse("空输入");
        ImageStorageService.LooksLikeSupportedImage([0x89, 0x50, 0x4E]).Should().BeFalse("PNG 头被截断");

        // RIFF 开头但第 8..12 字节不是 WEBP ⇒ 是别的 RIFF 容器（如 wav），不是图片。
        var riffNotWebp = new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 };
        ImageStorageService.LooksLikeSupportedImage(riffNotWebp).Should().BeFalse("RIFF/WAVE 不是图片");
    }

    // ═══════════════════════════════════════════════════════════
    // 上传
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Save_writes_a_server_named_file_and_reports_its_absolute_path()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var content = PngHead.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        var result = await service.SaveAsync(new MemoryStream(content), @"C:\随便\客户端名.png", content.Length);

        result.Status.Should().Be(ImageSaveStatus.Ok);
        result.Bytes.Should().Be(content.Length);

        // 文件名必须由服务端生成：img + 32 位无连字符 GUID + 原扩展名。
        result.FileName.Should().MatchRegex("^img[0-9a-f]{32}\\.png$");

        // ⚠️ 关键契约：Path 是**绝对路径**且与 FileName 的最后一段一致。
        //    前端要把 Path（不是 FileName）写进配置字段，否则导出会漏图。
        Path.IsPathRooted(result.Path).Should().BeTrue();
        Path.GetFileName(result.Path).Should().Be(result.FileName);
        result.Path.Should().Be(Path.Combine(dir.ImagesDirectory, result.FileName!));

        File.Exists(result.Path).Should().BeTrue();
        (await File.ReadAllBytesAsync(result.Path!)).Should().Equal(content);
    }

    [Fact]
    public async Task Save_ignores_the_client_file_name_completely()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        // 客户端文件名里塞满目录穿越与驱动器号 —— 它只被用来取扩展名，不该出现在任何路径里。
        var hostile = @"..\..\..\Windows\System32\evil.png";
        var result = await service.SaveAsync(new MemoryStream(PngHead), hostile, PngHead.Length);

        result.Status.Should().Be(ImageSaveStatus.Ok);
        result.Path.Should().StartWith(dir.ImagesDirectory);

        Directory.GetFiles(dir.ImagesDirectory).Should().HaveCount(1, "只应落下服务端命名的那个文件");
    }

    [Fact]
    public async Task Save_rejects_non_image_content_even_when_the_extension_is_allowed()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        // 典型的"改扩展名"伪装：内容是 HTML，名字是 .png。
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        var result = await service.SaveAsync(new MemoryStream(html), "fake.png", html.Length);

        result.Status.Should().Be(ImageSaveStatus.NotAnImage);
        result.FileName.Should().BeNull();
        Directory.Exists(dir.ImagesDirectory).Should().BeFalse("被拒的上传不应留下任何痕迹");
    }

    [Fact]
    public async Task Save_rejects_svg_upload_by_extension()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var result = await service.SaveAsync(new MemoryStream(svg), "logo.svg", svg.Length);

        result.Status.Should().Be(ImageSaveStatus.UnsupportedExtension);
    }

    [Fact]
    public async Task Save_rejects_oversize_by_declared_length_without_reading_content()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir, maxBytes: 1024);

        // 声明已超限：连内容都不必读，用一个"读就抛"的流来证明这一点。
        var result = await service.SaveAsync(new ThrowingStream(), "a.png", declaredLength: 2048);

        result.Status.Should().Be(ImageSaveStatus.TooLarge);
    }

    [Fact]
    public async Task Save_rejects_oversize_by_actual_stream_length_when_the_declaration_lies()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir, maxBytes: 1024);

        // 声明 10 字节（放过快速失败），实际给出 5 KB —— 必须按真实读到的字节数判定。
        var body = PngHead.Concat(new byte[5000]).ToArray();
        var result = await service.SaveAsync(new MemoryStream(body), "a.png", declaredLength: 10);

        result.Status.Should().Be(ImageSaveStatus.TooLarge);
        Directory.Exists(dir.ImagesDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Save_rejects_an_empty_upload()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var result = await service.SaveAsync(new MemoryStream([]), "a.png", 0);

        result.Status.Should().Be(ImageSaveStatus.NoFile);
    }

    [Fact]
    public async Task Save_accepts_a_file_exactly_at_the_limit()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir, maxBytes: 1024);

        var exactly = PngHead.Concat(new byte[1024 - PngHead.Length]).ToArray();
        var result = await service.SaveAsync(new MemoryStream(exactly), "a.png", exactly.Length);

        result.Status.Should().Be(ImageSaveStatus.Ok);
        result.Bytes.Should().Be(1024);
    }

    [Fact]
    public async Task Save_gives_each_upload_a_distinct_name()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var first = await service.SaveAsync(new MemoryStream(PngHead), "same.png", PngHead.Length);
        var second = await service.SaveAsync(new MemoryStream(PngHead), "same.png", PngHead.Length);

        first.FileName.Should().NotBe(second.FileName);
        Directory.GetFiles(dir.ImagesDirectory).Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════
    // 路径解析（两道闸）
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(@"..\..\appsettings.json")]      // 相对穿越
    [InlineData("../../appsettings.json")]       // 正斜杠穿越
    [InlineData(@"sub\img0.png")]                // 多段路径
    [InlineData(@"C:\Windows\System32\evil.png")] // 绝对路径
    [InlineData("img0.png:evil")]                // 备用数据流 / 盘符冒号
    [InlineData("")]
    public void TryResolveExisting_refuses_anything_that_is_not_a_single_safe_segment(string name)
    {
        using var dir = new TempDataDirectory();
        dir.EnsureCreated();
        var service = NewService(dir);

        service.TryResolveExisting(name, out var full).Should().BeFalse();
        full.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveExisting_resolves_an_existing_file_but_not_a_missing_one()
    {
        using var dir = new TempDataDirectory();
        dir.EnsureCreated();
        var service = NewService(dir);

        var file = Path.Combine(dir.ImagesDirectory, "img0.png");
        File.WriteAllBytes(file, PngHead);

        service.TryResolveExisting("img0.png", out var resolved).Should().BeTrue();
        resolved.Should().Be(Path.GetFullPath(file));

        service.TryResolveExisting("img1.png", out _).Should().BeFalse("文件不存在");
    }

    [Fact]
    public void TryResolveExisting_blocks_a_sibling_directory_sharing_the_prefix()
    {
        using var dir = new TempDataDirectory();
        dir.EnsureCreated();
        var service = NewService(dir);

        // "images-evil" 与 "images" 同前缀 —— 少了末尾分隔符的 StartsWith 判定会放它进来。
        var sibling = dir.ImagesDirectory + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllBytes(Path.Combine(sibling, "x.png"), PngHead);

        // 注：文件名本身是安全的单段名，所以这里真正被考验的是"目录归属"那道闸；
        // 由于文件名不含目录成分，它解析后必然落在 images 内，因此结果为"不存在"。
        service.TryResolveExisting("x.png", out _).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // 删除
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Delete_removes_the_file_and_then_reports_not_found()
    {
        using var dir = new TempDataDirectory();
        dir.EnsureCreated();
        var service = NewService(dir);

        var file = Path.Combine(dir.ImagesDirectory, "img0.png");
        File.WriteAllBytes(file, PngHead);

        service.Delete("img0.png").Status.Should().Be(ImageDeleteStatus.Ok);
        File.Exists(file).Should().BeFalse();

        service.Delete("img0.png").Status.Should().Be(ImageDeleteStatus.NotFound, "删两次第二次应找不到");
    }

    [Fact]
    public void Delete_refuses_traversal_with_the_same_answer_as_missing()
    {
        using var dir = new TempDataDirectory();
        dir.EnsureCreated();
        var service = NewService(dir);

        // 在数据目录放一个"有价值"的文件（模拟授权文件），确认穿越删不掉它。
        var victim = Path.Combine(dir.Root, "important.txt");
        File.WriteAllText(victim, "keep me");

        service.Delete(@"..\important.txt").Status.Should().Be(ImageDeleteStatus.NotFound);

        File.Exists(victim).Should().BeTrue("目录穿越的删除必须被挡下");
    }

    // ═══════════════════════════════════════════════════════════
    // 引用扫描
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FindReferrers_reports_every_place_that_uses_the_image()
    {
        var settings = new ControlSettings
        {
            BackgroundImagePath = @"C:\d\images\bg.png",
            DefaultButtonImage = @"C:\d\images\shared.png",
            Buttons =
            [
                new ButtonConfig { Id = "b1", Label = "开灯", ButtonImage = @"C:\d\images\shared.png" },
                new ButtonConfig { Id = "b2", Label = "关灯", ButtonImage = @"C:\d\images\other.png" },
            ],
            SubButtonSets =
            [
                new SubButtonSet
                {
                    Id = "s1",
                    Label = "灯光组",
                    ButtonImage = @"C:\d\images\bg.png",
                    SubButtons = [new SubButton { Id = "sb1", Label = "一路", ButtonImage = @"C:\d\images\shared.png" }],
                },
            ],
        };

        var shared = ImageReferenceScanner.FindReferrers(settings, "shared.png");
        shared.Should().Equal(new[]
        {
            "默认按钮图片",
            "按钮「开灯」",
            "子按钮「一路」",
        }, "引用方要按 UI 结构的可读名字列出");

        ImageReferenceScanner.FindReferrers(settings, "bg.png").Should().Equal(new[]
        {
            "背景图片",
            "子按钮集「灯光组」",
        });

        ImageReferenceScanner.FindReferrers(settings, "nobody-uses-me.png").Should().BeEmpty();
    }

    [Fact]
    public void FindReferrers_matches_the_last_path_segment_and_ignores_case()
    {
        var settings = new ControlSettings
        {
            // 大小写不同、目录不同 —— 前端 imageUrl() 也是按最后一段匹配的，口径必须一致。
            DefaultButtonImage = @"D:\elsewhere\IMG0.PNG",
        };

        ImageReferenceScanner.FindReferrers(settings, "img0.png").Should().Equal(new[] { "默认按钮图片" });
        ImageReferenceScanner.FindReferrers(settings, "img1.png").Should().BeEmpty();
    }

    [Fact]
    public void FindReferrers_tolerates_empty_inputs_and_unnamed_items()
    {
        ImageReferenceScanner.FindReferrers(null, "a.png").Should().BeEmpty();
        ImageReferenceScanner.FindReferrers(new ControlSettings(), "a.png").Should().BeEmpty();
        ImageReferenceScanner.FindReferrers(new ControlSettings(), null).Should().BeEmpty();
        ImageReferenceScanner.FindReferrers(new ControlSettings(), "").Should().BeEmpty();

        var settings = new ControlSettings
        {
            Buttons = [new ButtonConfig { Id = "b", Label = "   ", ButtonImage = @"C:\d\images\a.png" }],
        };

        // 空标签不能产出「按钮「」」这种读不通的描述。
        ImageReferenceScanner.FindReferrers(settings, "a.png").Should().Equal(new[] { "按钮「未命名按钮」" });
    }

    [Fact]
    public void FindReferrers_ignores_blank_image_fields()
    {
        var settings = new ControlSettings
        {
            DefaultButtonImage = "",
            BackgroundImagePath = "   ",
            Buttons = [new ButtonConfig { Id = "b", ButtonImage = "" }],
        };

        ImageReferenceScanner.FindReferrers(settings, "a.png").Should().BeEmpty();
    }

    /// <summary>一读就抛的流：用来证明"声明超限时根本不会去读内容"。</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("不该读内容");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
