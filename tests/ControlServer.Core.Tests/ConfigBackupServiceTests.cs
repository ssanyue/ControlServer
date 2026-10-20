using System.Text;
using System.Text.Json;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 配置导入 / 导出内核（M2-F）。
/// <para>
/// 重点覆盖三件事：①**旧版 <c>.controlbak</c> 必须原样导入**（这是用户迁移配置的唯一路径）；
/// ②图片 base64 往返；③**不可信输入的安全边界**（路径穿越、任意 JSON 被当成空配置）。
/// </para>
/// </summary>
public sealed class ConfigBackupServiceTests
{
    // ═══════════════════════════════════════════════════════════
    // 解析：四种输入形态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_accepts_desktop_plain_text_backup_file()
    {
        // 旧版「导出配置」产出：明文缩进 camelCase 的 ConfigBackup JSON。
        const string desktopFile = """
        {
          "version": "1.0",
          "settings": {
            "appName": "展厅中控",
            "listenPort": 9999,
            "buttons": [
              { "id": "b1", "order": 0, "label": "开灯", "pairingCode": "LIGHT-ON", "actions": [] }
            ],
            "subButtonSets": []
          },
          "images": {}
        }
        """;

        var result = ConfigBackupService.Parse(desktopFile);

        result.Ok.Should().BeTrue(result.Error);
        result.Format.Should().Be(ConfigBackupFormats.JsonBackup);
        result.Backup!.Settings.AppName.Should().Be("展厅中控");
        result.Backup.Settings.Buttons.Should().ContainSingle()
            .Which.PairingCode.Should().Be("LIGHT-ON");
    }

    [Fact]
    public void Parse_accepts_base64_wrapped_backup()
    {
        var backup = NewBackup(label: "开灯");
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfigBackupService.ToJson(backup)));

        var result = ConfigBackupService.Parse(text);

        result.Ok.Should().BeTrue(result.Error);
        result.Format.Should().Be(ConfigBackupFormats.Base64Backup);
        result.Backup!.Settings.Buttons.Single().Label.Should().Be("开灯");
    }

    [Fact]
    public void Parse_accepts_base64_wrapped_backup_with_line_breaks()
    {
        // 用户从记事本 / 邮件里粘贴时常带换行，必须容忍。
        var backup = NewBackup(label: "开灯");
        var compact = Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfigBackupService.ToJson(backup)));
        var wrapped = string.Join("\n", Chunk(compact, 64));

        var result = ConfigBackupService.Parse(wrapped);

        result.Ok.Should().BeTrue(result.Error);
        result.Format.Should().Be(ConfigBackupFormats.Base64Backup);
    }

    [Fact]
    public void Parse_accepts_bare_settings_json()
    {
        const string bare = """
        { "appName": "裸配置", "buttons": [ { "id": "b1", "label": "甲" } ], "subButtonSets": [] }
        """;

        var result = ConfigBackupService.Parse(bare);

        result.Ok.Should().BeTrue(result.Error);
        result.Format.Should().Be(ConfigBackupFormats.JsonSettings);
        result.Backup!.Settings.Buttons.Single().Label.Should().Be("甲");
        result.Backup.Images.Should().BeEmpty();
    }

    [Fact]
    public void Parse_accepts_base64_wrapped_bare_settings()
    {
        const string bare = """{ "appName": "裸配置", "buttons": [ { "id": "b1", "label": "乙" } ] }""";
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes(bare));

        var result = ConfigBackupService.Parse(text);

        result.Ok.Should().BeTrue(result.Error);
        result.Format.Should().Be(ConfigBackupFormats.Base64Settings);
        result.Backup!.Settings.Buttons.Single().Label.Should().Be("乙");
    }

    [Fact]
    public void Parse_accepts_utf8_bom_prefixed_text()
    {
        var text = "\uFEFF" + ConfigBackupService.ToJson(NewBackup("甲"));

        ConfigBackupService.Parse(text).Ok.Should().BeTrue("带 BOM 的文本（Windows 记事本另存常见）必须能导入");
    }

    // ═══════════════════════════════════════════════════════════
    // 解析：拒绝路径（安全性）
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_empty_input(string? text)
    {
        var result = ConfigBackupService.Parse(text);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("为空");
    }

    [Fact]
    public void Parse_rejects_text_that_is_neither_json_nor_base64()
    {
        var result = ConfigBackupService.Parse("这不是 JSON，也不是 base64！！！");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("无法识别");
    }

    [Fact]
    public void Parse_rejects_random_plain_ascii_that_looks_like_base64()
    {
        // "YWJj" 是合法 base64，但解出来是 "abc"，不是 JSON —— 不能被当成配置。
        var result = ConfigBackupService.Parse("YWJj");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("无法识别");
    }

    [Fact]
    public void Parse_rejects_json_object_without_any_known_config_field()
    {
        // ⚠️ 关键安全护栏：没有它，任意 JSON 对象都会被宽松反序列化成"全默认空配置"，
        // 然后在导入流程里把用户现有配置整体覆盖 —— 一次点击丢全部数据。
        var result = ConfigBackupService.Parse("""{ "version": "1.0", "hello": "world" }""");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("没有任何已知的配置字段");
    }

    [Fact]
    public void Parse_rejects_json_whose_top_level_is_not_an_object()
    {
        var result = ConfigBackupService.Parse("[1, 2, 3]");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("顶层不是对象");
    }

    // ═══════════════════════════════════════════════════════════
    // 解析：被忽略字段的告知
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_lists_desktop_only_settings_fields_as_ignored()
    {
        const string desktopFile = """
        {
          "version": "1.0",
          "settings": {
            "appName": "展厅中控",
            "buttons": [],
            "windowWidth": 1280,
            "windowHeight": 800,
            "lockWindow": true,
            "minimizeOnClose": false,
            "autoStart": true,
            "userPasswordHash": "DEADBEEF"
          },
          "images": {}
        }
        """;

        var result = ConfigBackupService.Parse(desktopFile);

        result.Ok.Should().BeTrue(result.Error);
        result.IgnoredFields.Should().Equal(
            "autoStart", "lockWindow", "minimizeOnClose", "userPasswordHash", "windowHeight", "windowWidth");
    }

    [Fact]
    public void Parse_reports_no_ignored_fields_for_a_clean_service_backup()
    {
        var text = ConfigBackupService.ToJson(NewBackup("甲"));

        ConfigBackupService.Parse(text).IgnoredFields.Should().BeEmpty();
    }

    /// <summary>
    /// 🔥 <b>组合按钮必须能导出再导入</b>（2026-10-15，需求⑤）。
    ///
    /// <para>
    /// 这条是**被一个真缺陷逼出来的**：`KnownSettingsFields` 原先是手写的一张清单，
    /// 加了 <c>comboButtons</c> 却没同步它 ⇒ 上面那条"干净备份不该有忽略字段"的用例立刻变红
    /// （用户会看到「已忽略字段：comboButtons」，以为组合按钮全丢了）。
    /// 该清单现已改成从模型**反射**生成，这里再补一条"数据本身不丢"的端到端保障。
    /// </para>
    /// </summary>
    [Fact]
    public void Combo_buttons_survive_an_export_and_import_round_trip()
    {
        var backup = NewBackup("甲");

        backup.Settings.ComboButtons.Add(new ComboButton
        {
            Id = "c1",
            Label = "开场",
            PairingCode = "OPEN",
            Steps = new List<ComboButtonStep>
            {
                new() { ButtonId = "b1", DelayMs = 250 },
                new() { ButtonId = "b1", DelayMs = 0 },
            },
        });

        var parsed = ConfigBackupService.Parse(ConfigBackupService.ToJson(backup));

        parsed.Ok.Should().BeTrue(parsed.Error);
        parsed.IgnoredFields.Should().BeEmpty("组合按钮现在是**认得的**字段，不该被报成忽略");

        var combo = parsed.Backup!.Settings.ComboButtons.Should().ContainSingle().Subject;
        combo.Label.Should().Be("开场");
        combo.PairingCode.Should().Be("OPEN");
        combo.Steps.Should().HaveCount(2, "同一个按钮被引用两次也要原样留住");
        combo.Steps[0].DelayMs.Should().Be(250);
    }

    // ═══════════════════════════════════════════════════════════
    // 导出：图片内嵌
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildBackup_embeds_referenced_images_and_rewrites_paths_to_keys()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var imagePath = Path.Combine(dir.Root, "灯.png");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3, 4, 5 });

        var settings = new ControlSettings
        {
            DefaultButtonImage = imagePath,
            BackgroundImagePath = imagePath,   // 同一张图被引用两次 → 只应内嵌一次
            Buttons =
            {
                new ButtonConfig { Label = "开灯", ButtonImage = imagePath },
            },
        };

        var backup = service.BuildBackup(settings);

        backup.Version.Should().Be("1.0");
        backup.Images.Should().ContainSingle("重复引用只内嵌一份");
        backup.Images.Should().ContainKey("img0.png");
        Convert.FromBase64String(backup.Images["img0.png"]).Should().Equal(new byte[] { 1, 2, 3, 4, 5 });

        backup.Settings.DefaultButtonImage.Should().Be("img0.png");
        backup.Settings.BackgroundImagePath.Should().Be("img0.png");
        backup.Settings.Buttons.Single().ButtonImage.Should().Be("img0.png");

        // 原对象不得被改动（导出是只读操作）。
        settings.Buttons.Single().ButtonImage.Should().Be(imagePath);
    }

    [Fact]
    public void BuildBackup_keeps_path_when_image_file_is_missing()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var missing = Path.Combine(dir.Root, "不存在.png");
        var settings = new ControlSettings { DefaultButtonImage = missing };

        var backup = service.BuildBackup(settings);

        backup.Images.Should().BeEmpty("文件不存在就不内嵌");
        backup.Settings.DefaultButtonImage.Should().Be(missing, "路径原样保留，不做无声清空");
    }

    // ═══════════════════════════════════════════════════════════
    // 导入：图片还原与安全边界
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Apply_restores_images_into_images_directory_and_rewrites_to_absolute_paths()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var backup = new ConfigBackup
        {
            Settings = new ControlSettings
            {
                DefaultButtonImage = "img0.png",
                Buttons = { new ButtonConfig { Label = "开灯", ButtonImage = "img0.png" } },
            },
            Images = new Dictionary<string, string>
            {
                ["img0.png"] = Convert.ToBase64String(new byte[] { 9, 8, 7 }),
            },
        };

        var applied = service.Apply(backup);

        applied.RestoredImages.Should().Be(1);
        applied.SkippedImages.Should().BeEmpty();

        var expected = Path.Combine(dir.ImagesDirectory, "img0.png");
        applied.Settings.DefaultButtonImage.Should().Be(expected);
        applied.Settings.Buttons.Single().ButtonImage.Should().Be(expected);
        File.ReadAllBytes(expected).Should().Equal(new byte[] { 9, 8, 7 });
    }

    [Theory]
    [InlineData("../../evil.png")]
    [InlineData("..\\..\\evil.png")]
    [InlineData("sub/evil.png")]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("http://evil/x.png")]
    public void Apply_refuses_image_keys_that_try_to_escape_the_images_directory(string evilKey)
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        var backup = new ConfigBackup
        {
            Settings = new ControlSettings { DefaultButtonImage = evilKey },
            Images = new Dictionary<string, string>
            {
                [evilKey] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            },
        };

        var applied = service.Apply(backup);

        applied.RestoredImages.Should().Be(0);
        applied.SkippedImages.Should().Contain(evilKey);
        applied.Settings.DefaultButtonImage.Should().Be(evilKey, "路径原样保留但不落盘");

        // 真正的断言：图片目录之外没有多出任何文件。
        var escaped = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evil.png"));
        File.Exists(escaped).Should().BeFalse("目录穿越必须被挡住");
    }

    [Theory]
    [InlineData("img0.png", true)]
    [InlineData("灯 图标.png", true)]
    [InlineData("a_b-c(1).jpg", true)]
    [InlineData("../a.png", false)]
    [InlineData("a/b.png", false)]
    [InlineData("a\\b.png", false)]
    [InlineData("C:x.png", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeImageKey_only_accepts_single_segment_file_names(string key, bool expected)
    {
        ConfigBackupService.IsSafeImageKey(key).Should().Be(expected);
    }

    [Fact]
    public void Apply_keeps_value_untouched_when_key_is_not_in_embedded_images()
    {
        using var dir = new TempDataDirectory();
        var service = NewService(dir);

        // 兼容"直接导出原始配置"的场景：图片路径是绝对路径，不在 images 字典里。
        const string absolute = @"D:\老机器\pic\灯.png";
        var backup = new ConfigBackup
        {
            Settings = new ControlSettings { DefaultButtonImage = absolute },
            Images = new Dictionary<string, string>(),
        };

        var applied = service.Apply(backup);

        applied.Settings.DefaultButtonImage.Should().Be(absolute);
        applied.RestoredImages.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 端到端往返
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Export_then_import_round_trip_preserves_buttons_subbuttons_and_images()
    {
        using var source = new TempDataDirectory();
        using var target = new TempDataDirectory();

        var imagePath = Path.Combine(source.Root, "图标.png");
        var imageBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(imagePath, imageBytes);

        var original = new ControlSettings
        {
            AppName = "展厅中控",
            ListenPort = 9999,
            DefaultButtonImage = imagePath,
            Buttons =
            {
                new ButtonConfig
                {
                    Id = "b1",
                    Order = 0,
                    Label = "开灯",
                    PairingCode = "LIGHT-ON",
                    PairingCodeType = PairingCodeTypes.Utf8,
                    TimerEnabled = true,
                    TimerTimes = { "07:45", "18:30" },
                    Actions =
                    {
                        new ButtonAction
                        {
                            Name = "发 UDP",
                            Protocol = TransportProtocols.Udp,
                            TargetIp = "192.168.1.50",
                            TargetPort = 8000,
                            DataType = DataTypes.Ansi,
                            DataContent = "中",
                            DelayMs = 120,
                        },
                    },
                },
            },
            SubButtonSets =
            {
                new SubButtonSet
                {
                    Id = "s1",
                    Order = 1,
                    Label = "机房",
                    TargetIp = "10.0.0.9",
                    TargetPort = 5000,
                    SubButtons =
                    {
                        new SubButton { Id = "sub1", Label = "重启", ButtonImage = imagePath },
                    },
                },
            },
        };

        // 导出（base64 单文件）→ 导入到另一台"机器"
        var exported = ConfigBackupService.ToBase64(NewService(source).BuildBackup(original));
        var parsed = ConfigBackupService.Parse(exported);
        parsed.Ok.Should().BeTrue(parsed.Error);

        var applied = NewService(target).Apply(parsed.Backup!);

        applied.Settings.AppName.Should().Be("展厅中控");
        applied.Settings.Buttons.Should().ContainSingle();
        applied.Settings.Buttons.Single().TimerTimes.Should().Equal(new[] { "07:45", "18:30" }, "旧字段迁移后的多个时间点必须保住");
        applied.Settings.Buttons.Single().Actions.Single().DataContent.Should().Be("中");
        applied.Settings.Buttons.Single().Actions.Single().DelayMs.Should().Be(120);

        var set = applied.Settings.SubButtonSets.Single();
        set.TargetIp.Should().Be("10.0.0.9");
        set.TargetPort.Should().Be(5000);
        set.SubButtons.Single().Label.Should().Be("重启");

        applied.RestoredImages.Should().Be(1, "同一张图被两处引用，只还原一份");
        File.ReadAllBytes(Path.Combine(target.ImagesDirectory, "img0.png")).Should().Equal(imageBytes);
        set.SubButtons.Single().ButtonImage.Should().Be(Path.Combine(target.ImagesDirectory, "img0.png"));
    }

    [Fact]
    public void Suggested_file_name_uses_controlbak_extension()
    {
        ConfigBackupService.SuggestFileName().Should().EndWith(".controlbak");
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static ConfigBackupService NewService(IDataDirectory dir)
        => new(dir, new LogService(new NoopDataDirectory(), flushIntervalMs: 60_000));

    private static ConfigBackup NewBackup(string label)
        => new()
        {
            Version = "1.0",
            Settings = new ControlSettings
            {
                AppName = "中控",
                Buttons = { new ButtonConfig { Id = "b1", Label = label } },
            },
            Images = new Dictionary<string, string>(),
        };

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}
