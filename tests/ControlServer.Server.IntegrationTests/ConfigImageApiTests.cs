using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 图片上传 / 删除端点（M2-G1）的端到端测试 —— 真宿主 + 真文件系统 + 真 HTTP 管线。
/// <para>
/// 重点不是"上传能返回 200"，而是四组**容易出事的语义**：
/// ① 文件名一律服务端生成（客户端文件名不进路径）；
/// ② 三道校验各自独立生效（扩展名 / 大小 / 内容真的是图片）；
/// ③ 删除的两道闸 + 引用保护；
/// ④ **上传结果能被导出** —— 这一条把"前端该存 Path 还是存 FileName"这个契约钉死，
///    存错了会导致导出静默丢图，且任何单点测试都发现不了。
/// </para>
/// </summary>
public sealed class ConfigImageApiTests : IDisposable
{
    private readonly ControlServerFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string ImagesDirectory => Path.Combine(_factory.DataDirectory, "images");

    /// <summary>
    /// 图片目录里的文件清单。
    /// <para>
    /// ⚠️ 不能断言"目录不存在"：<c>Program.cs</c> 在组合根之前就调了
    /// <c>IDataDirectory.EnsureCreated()</c>（日志路径依赖它），图片目录因此**启动即存在**。
    /// 被拒的上传应当表现为"目录里没有新文件"。
    /// </para>
    /// </summary>
    private string[] FilesInImages()
        => Directory.Exists(ImagesDirectory) ? Directory.GetFiles(ImagesDirectory) : [];

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin123"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "默认管理员必须能登录");
        return client;
    }

    /// <summary>一段内容像样的 PNG：文件头是真的（魔数嗅探靠它），后面跟几个可校验的字节。</summary>
    private static byte[] Png(params byte[] tail)
        => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. tail];

    private static MultipartFormDataContent Multipart(byte[] bytes, string fileName, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private async Task<ConfigImageUploadResultDto> UploadAsync(HttpClient client, byte[] bytes, string fileName)
    {
        var response = await client.PostAsync("/api/config/images", Multipart(bytes, fileName));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "上传应成功");

        var dto = await response.Content.ReadFromJsonAsync<ConfigImageUploadResultDto>();
        dto.Should().NotBeNull();
        return dto!;
    }

    // ═══════════════════════════════════════════════════════════
    // 鉴权
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Image_upload_and_delete_reject_anonymous()
    {
        var client = _factory.CreateClient();

        var upload = await client.PostAsync(
            "/api/config/images",
            Multipart(Png(1, 2, 3), "a.png"));
        upload.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var delete = await client.DeleteAsync("/api/config/images/img0.png");
        delete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════
    // 上传：成功路径
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Uploaded_image_is_stored_under_a_server_generated_name_and_readable_back()
    {
        var client = await LoginAsync();
        var bytes = Png(1, 2, 3, 4, 5);

        var dto = await UploadAsync(client, bytes, "我的图片.png");

        dto.FileName.Should().MatchRegex("^img[0-9a-f]{32}\\.png$", "文件名必须由服务端生成");
        Path.IsPathRooted(dto.Path).Should().BeTrue("返回的是绝对路径");
        Path.GetFileName(dto.Path).Should().Be(dto.FileName);
        dto.Bytes.Should().Be(bytes.Length);

        File.Exists(dto.Path).Should().BeTrue();
        File.ReadAllBytes(dto.Path).Should().Equal(bytes);

        // 读回来必须字节一致，且 Content-Type 按其扩展名给出。
        var fetched = await client.GetAsync($"/api/config/images/{dto.FileName}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        fetched.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await fetched.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task Upload_never_uses_the_client_file_name_for_the_path()
    {
        var client = await LoginAsync();

        // 客户端文件名里塞满目录穿越 —— 它只被用来取扩展名。
        var dto = await UploadAsync(client, Png(9, 9), @"..\..\..\evil.png");

        dto.Path.Should().StartWith(ImagesDirectory, "落盘位置只能是图片目录");
        Directory.GetFiles(ImagesDirectory).Should().ContainSingle();
        File.Exists(Path.Combine(_factory.DataDirectory, "evil.png")).Should().BeFalse();
    }

    [Theory]
    [InlineData("a.webp")]
    [InlineData("a.jpeg")]
    [InlineData("a.gif")]
    [InlineData("a.bmp")]
    public async Task Upload_accepts_every_whitelisted_extension(string fileName)
    {
        var client = await LoginAsync();

        // 每种格式给一个真实的文件头，否则会被魔数校验拒掉。
        byte[] head = Path.GetExtension(fileName) switch
        {
            ".webp" => [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50],
            ".jpeg" => [0xFF, 0xD8, 0xFF, 0xE0],
            ".gif" => "GIF89a"u8.ToArray(),
            _ => [0x42, 0x4D, 0x00, 0x00],
        };

        var dto = await UploadAsync(client, head, fileName);

        dto.FileName.Should().EndWith(Path.GetExtension(fileName));
    }

    // ═══════════════════════════════════════════════════════════
    // 上传：三道校验
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("evil.svg")]     // SVG 可内嵌脚本 ⇒ 刻意不支持
    [InlineData("a.txt")]
    [InlineData("a.exe")]
    [InlineData("noext")]
    public async Task Upload_rejects_extension_outside_the_whitelist(string fileName)
    {
        var client = await LoginAsync();

        var response = await client.PostAsync("/api/config/images", Multipart(Png(1), fileName));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        FilesInImages().Should().BeEmpty("被拒的上传不该留下任何文件");
    }

    [Fact]
    public async Task Upload_rejects_content_that_is_not_an_image_even_with_a_valid_extension()
    {
        var client = await LoginAsync();

        // 典型伪装：内容是 HTML，扩展名是 .png。只校验扩展名会把它放进来。
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        var response = await client.PostAsync("/api/config/images", Multipart(html, "logo.png"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var message = await response.Content.ReadAsStringAsync();
        message.Should().Contain("图片", "提示要说清是「内容不是图片」，而不是别的");

        FilesInImages().Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_rejects_a_file_larger_than_the_configured_limit()
    {
        var client = await LoginAsync();

        // 4 MB 上限 + 一点点 ⇒ 落在 FormOptions 的缓冲上限之内，
        // 由端点自己的大小校验拦下（返回 413 而不是 400）。
        var oversize = Png(new byte[(4 * 1024 * 1024) + 4096]);

        var response = await client.PostAsync("/api/config/images", Multipart(oversize, "big.png"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Contain("4 MB");

        FilesInImages().Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_rejects_a_body_larger_than_the_multipart_buffer_limit()
    {
        var client = await LoginAsync();

        // 上限（4 MB）+ 1 MB 余量 = FormOptions 的 MultipartBodyLengthLimit。
        //
        // 这个请求会在**MVC 调用 action 之前**就被框架拒绝（值提供器读表单时超限），
        // 因此它检验的不是端点里的分支，而是组合根里那段
        // 「把框架默认的 400 + 英文内部文案翻成 413 + 人话」的映射。
        // 若哪次 .NET 改了那句文案，这条用例会红 —— 这正是它存在的意义。
        var huge = Png(new byte[(5 * 1024 * 1024) + (512 * 1024)]);

        var response = await client.PostAsync("/api/config/images", Multipart(huge, "huge.png"));

        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.RequestEntityTooLarge,
            $"必须是 413（图片太大），而不是框架默认的 400 + 英文内部文案；实际响应体：{body}");
        body.Should().Contain("4 MB", "提示要说人话，而不是甩出 5242880 这个内部数字");
        body.Should().NotContain("Multipart body length limit", "不该把框架的内部措辞透给用户");
    }

    [Fact]
    public async Task Upload_without_a_file_field_is_rejected_with_a_pointing_message()
    {
        var client = await LoginAsync();

        var form = new MultipartFormDataContent { { new StringContent("x"), "notFile" } };
        var response = await client.PostAsync("/api/config/images", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file");
    }

    [Fact]
    public async Task Upload_without_multipart_content_type_is_rejected()
    {
        var client = await LoginAsync();

        var response = await client.PostAsJsonAsync("/api/config/images", new { file = "not really a file" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════
    // 删除
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_removes_the_file_and_then_reports_not_found()
    {
        var client = await LoginAsync();
        var dto = await UploadAsync(client, Png(7, 7, 7), "a.png");

        var first = await client.DeleteAsync($"/api/config/images/{dto.FileName}");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(dto.Path).Should().BeFalse();

        var second = await client.DeleteAsync($"/api/config/images/{dto.FileName}");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_refuses_path_traversal()
    {
        var client = await LoginAsync();

        // 在数据目录（图片目录之外）放一个"有价值"的文件，确认穿越删不掉它。
        var victim = Path.Combine(_factory.DataDirectory, "keep-me.txt");
        await File.WriteAllTextAsync(victim, "keep");

        foreach (var name in new[] { "..%5Ckeep-me.txt", "control.db", "nope.png" })
        {
            var response = await client.DeleteAsync($"/api/config/images/{name}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "「{0}」不该被删除", name);
        }

        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_is_refused_while_the_image_is_still_referenced()
    {
        var client = await LoginAsync();
        var dto = await UploadAsync(client, Png(1, 1), "shared.png");

        // 让配置真的引用这张图（存的是**绝对路径**，与导入路径的形状一致）。
        await SetSingleButtonImageAsync(client, dto.Path);

        var refused = await client.DeleteAsync($"/api/config/images/{dto.FileName}");

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await refused.Content.ReadFromJsonAsync<ConfigImageInUseDto>();
        payload.Should().NotBeNull();
        payload!.ReferencedBy.Should().Equal(new[] { "按钮「开灯」" });
        File.Exists(dto.Path).Should().BeTrue("被引用的图片不能被删掉");

        // 明确要求强行删除时才放行。
        var forced = await client.DeleteAsync($"/api/config/images/{dto.FileName}?force=true");
        forced.StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(dto.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_reports_every_referrer_including_the_default_button_image()
    {
        var client = await LoginAsync();
        var dto = await UploadAsync(client, Png(2, 2), "shared.png");

        // 默认按钮图 + 两个按钮 + 一个子按钮都指向同一张图。
        var settings = $$"""
        {
          "appName": "多处引用",
          "defaultButtonImage": {{JsonSerializer.Serialize(dto.Path)}},
          "buttons": [
            { "id": "b1", "order": 0, "label": "开灯", "buttonImage": {{JsonSerializer.Serialize(dto.Path)}}, "actions": [] },
            { "id": "b2", "order": 1, "label": "关灯", "actions": [] }
          ],
          "subButtonSets": [
            { "id": "s1", "order": 2, "label": "灯光组", "subButtons": [
                { "id": "sb1", "label": "一路", "buttonImage": {{JsonSerializer.Serialize(dto.Path)}} }
            ] }
          ]
        }
        """;

        var put = await client.PutAsJsonAsync("/api/config", new
        {
            settings = JsonSerializer.Deserialize<JsonElement>(settings),
            expectedVersion = 0,
            updatedBy = "test",
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var refused = await client.DeleteAsync($"/api/config/images/{dto.FileName}?force=false");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var payload = await refused.Content.ReadFromJsonAsync<ConfigImageInUseDto>();
        payload!.ReferencedBy.Should().Equal(new[]
        {
            "默认按钮图片",
            "按钮「开灯」",
            "子按钮「一路」",
        });
    }

    // ═══════════════════════════════════════════════════════════
    // 契约：上传结果必须能被导出（存 Path 而不是 FileName）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Uploaded_image_is_embedded_when_the_config_is_exported()
    {
        var client = await LoginAsync();
        var bytes = Png(3, 1, 4, 1, 5, 9, 2, 6);
        var dto = await UploadAsync(client, bytes, "button.png");

        await SetSingleButtonImageAsync(client, dto.Path);

        var export = await client.GetFromJsonAsync<ConfigExportDto>("/api/config/export?format=json");

        export!.ImageCount.Should().Be(1, "被引用的图片必须内嵌进备份");

        using var doc = JsonDocument.Parse(export.Content);
        var images = doc.RootElement.GetProperty("images");

        // 导出侧把键名改写成 imgN.ext（与上传侧的 img{GUID}.ext 是同一种命名思想）。
        images.EnumerateObject().Should().ContainSingle();
        var embedded = images.EnumerateObject().Single();
        embedded.Name.Should().MatchRegex("^img\\d+\\.png$");

        Convert.FromBase64String(embedded.Value.GetString()!).Should().Equal(bytes);
    }

    [Fact]
    public async Task Storing_only_the_file_name_would_lose_the_image_on_export()
    {
        var client = await LoginAsync();
        var bytes = Png(4, 4);
        var dto = await UploadAsync(client, bytes, "button.png");

        // 反向守门：把**裸文件名**写进配置（即"前端存错字段"的后果）。
        await SetSingleButtonImageAsync(client, dto.FileName);

        var export = await client.GetFromJsonAsync<ConfigExportDto>("/api/config/export?format=json");

        export!.ImageCount.Should().Be(
            0,
            "导出侧按 File.Exists(字段值) 收集图片，裸文件名会被当成相对路径而判定为不存在 —— "
            + "这条用例的存在，就是为了让「上传接口必须回传绝对路径」这一约定无法被悄悄改掉");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>把配置设成"一个按钮、图片指向给定值"，用于验证配置侧的引用行为。</summary>
    private static async Task SetSingleButtonImageAsync(HttpClient client, string imageValue)
    {
        var settings = $$"""
        {
          "appName": "上传测试",
          "buttons": [
            { "id": "b1", "order": 0, "label": "开灯", "buttonImage": {{JsonSerializer.Serialize(imageValue)}}, "actions": [] }
          ],
          "subButtonSets": []
        }
        """;

        var response = await client.PutAsJsonAsync("/api/config", new
        {
            settings = JsonSerializer.Deserialize<JsonElement>(settings),
            expectedVersion = 0,
            updatedBy = "test",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "配置保存应成功");
    }
}
