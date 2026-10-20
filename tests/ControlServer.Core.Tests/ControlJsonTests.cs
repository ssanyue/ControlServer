using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 序列化约定（camelCase）与旧配置读取兼容性测试。
/// 这是"旧 <c>control_config.json</c> 近乎直读"这一承诺的守门测试。
/// </summary>
public sealed class ControlJsonTests
{
    [Fact]
    public void Serialization_uses_camelCase_keys()
    {
        var json = ControlJson.Serialize(new ControlSettings());

        json.Should().Contain("\"appName\"");
        json.Should().Contain("\"subButtonSets\"");
        json.Should().NotContain("\"AppName\"");
    }

    [Fact]
    public void Chinese_content_is_not_unicode_escaped()
    {
        var button = new ButtonConfig
        {
            Label = "客厅灯",
            Actions = new List<ButtonAction> { new() { DataType = DataTypes.CommandLibrary, DataContent = "开灯" } },
        };

        var json = ControlJson.Serialize(new ControlSettings { Buttons = { button } });

        json.Should().Contain("客厅灯");
        json.Should().Contain("指令库");
        json.Should().Contain("开灯");
        json.Should().NotContain("\\u");
    }

    [Fact]
    public void Full_settings_object_round_trips_without_loss()
    {
        var original = BuildRichSettings();

        var json = ControlJson.Serialize(original);
        var restored = ControlJson.DeserializeLenient<ControlSettings>(json)!;

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Legacy_desktop_config_is_read_and_desktop_only_fields_are_ignored()
    {
        // 模拟旧版写出的 control_config.json：多出 userPasswordHash / windowWidth / autoStart 等
        // 服务版不存在的字段，并且只有旧的 timerTime（没有 timerTimes）。
        const string legacyJson = """
        {
          "appName": "老中控",
          "userPasswordHash": "DEADBEEF",
          "backgroundImagePath": "",
          "listenPort": 9999,
          "defaultButtonImage": "",
          "autoStart": true,
          "minimizeOnClose": true,
          "windowWidth": 1280,
          "windowHeight": 720,
          "windowLocked": true,
          "freeLayoutEnabled": false,
          "buttons": [
            {
              "id": "b1",
              "order": 0,
              "label": "大门",
              "pairingCode": "OPEN",
              "pairingCodeType": "String",
              "buttonColor": "#512DA8",
              "textColor": "#FFFFFF",
              "nameFontSize": 13,
              "buttonImage": "",
              "width": 140,
              "height": 80,
              "layoutX": 0,
              "layoutY": 0,
              "timerEnabled": true,
              "timerMode": "Daily",
              "timerTime": "07:45",
              "timerDays": [1, 2, 3, 4, 5],
              "actions": [
                {
                  "id": "a1",
                  "name": "开",
                  "targetIp": "10.0.0.8",
                  "targetPort": 5000,
                  "protocol": "UDP",
                  "dataType": "Hex",
                  "dataContent": "0x41 0x42",
                  "delayMs": 100,
                  "pairingCode": "",
                  "pairingCodeType": "String"
                }
              ],
              "monthlyPlans": []
            }
          ],
          "subButtonSets": []
        }
        """;

        var act = () => ControlJson.DeserializeLenient<ControlSettings>(legacyJson);

        act.Should().NotThrow("单机版专有字段必须被静默忽略而不是抛异常");

        var settings = ControlJson.DeserializeLenient<ControlSettings>(legacyJson)!;
        settings.AppName.Should().Be("老中控");
        settings.Buttons.Should().HaveCount(1);

        var button = settings.Buttons[0];
        button.Label.Should().Be("大门");
        button.PairingCode.Should().Be("OPEN");
        button.Actions[0].TargetIp.Should().Be("10.0.0.8");
        button.Actions[0].DataType.Should().Be(DataTypes.Hex);
        button.Actions[0].DelayMs.Should().Be(100);

        // 旧字段被读进来，随后由清洗器迁移。
        button.TimerTime.Should().Be("07:45");
        button.TimerTimes.Should().BeEmpty();

        var notes = ControlSettingsNormalizer.Normalize(settings);
        button.TimerTimes.Should().Equal(new[] { "07:45:00" }, "清洗器统一补秒，旧值语义得以保留");
        notes.Should().Contain(n => n.Contains("07:45"));
    }

    [Fact]
    public void Numeric_fields_written_as_strings_are_accepted()
    {
        // 宽松模式：容忍 "height": "80" 这类脏数据。
        const string json = """
        {
          "buttons": [
            { "id": "b1", "label": "B", "height": "120", "width": "200" }
          ],
          "subButtonSets": []
        }
        """;

        var settings = ControlJson.DeserializeLenient<ControlSettings>(json)!;

        settings.Buttons[0].Height.Should().Be(120);
        settings.Buttons[0].Width.Should().Be(200);
    }

    private static ControlSettings BuildRichSettings() => new()
    {
        AppName = "测试中控",
        BackgroundImagePath = "bg.png",
        DefaultButtonImage = "def.png",
        ListenPort = 9000,
        FreeLayoutEnabled = true,
        Buttons =
        {
            new ButtonConfig
            {
                Id = "btn-1",
                Order = 0,
                Label = "总闸",
                PairingCode = "MAIN",
                PairingCodeType = PairingCodeTypes.Hex,
                ButtonColor = "#111111",
                TextColor = "#EEEEEE",
                NameFontSize = 15,
                ButtonImage = "b.png",
                Width = 180,
                Height = 90,
                LayoutX = 12.5,
                LayoutY = 30.25,
                TimerEnabled = true,
                TimerMode = TimerModes.SpecificDays,
                TimerTimes = new List<string> { "08:00", "20:30" },
                TimerDays = new List<int> { 1, 3, 5 },
                Actions =
                {
                    new ButtonAction
                    {
                        Id = "act-1",
                        Name = "发送",
                        TargetIp = "192.168.1.50",
                        TargetPort = 6000,
                        Protocol = TransportProtocols.Tcp,
                        DataType = DataTypes.Ansi,
                        DataContent = "中文数据",
                        DelayMs = 250,
                        PairingCode = "A1",
                        PairingCodeType = PairingCodeTypes.Utf8,
                    },
                },
                MonthlyPlans =
                {
                    new MonthlyPlan
                    {
                        Id = "plan-1",
                        Name = "月末",
                        Enabled = true,
                        Times = new List<string> { "23:00" },
                        MonthDays = new List<MonthDaySchedule>
                        {
                            new() { Month = 12, Days = new List<int> { 24, 31 } },
                        },
                    },
                },
            },
        },
        SubButtonSets =
        {
            new SubButtonSet
            {
                Id = "set-1",
                Order = 1,
                Label = "灯组",
                ButtonColor = "#222222",
                TextColor = "#DDDDDD",
                NameFontSize = 14,
                ButtonImage = "s.png",
                Width = 150,
                Height = 70,
                LayoutX = 5,
                LayoutY = 6,
                TargetIp = "10.10.10.10",
                TargetPort = 7000,
                SubButtons =
                {
                    new SubButton
                    {
                        Id = "sub-1",
                        Label = "灯1",
                        ButtonColor = "#333333",
                        TextColor = "#CCCCCC",
                        NameFontSize = 12,
                        ButtonImage = "sub.png",
                        Width = 90,
                        Height = 90,
                        PairingCode = "L1",
                        PairingCodeType = PairingCodeTypes.Utf8,
                        Actions =
                        {
                            new ButtonAction
                            {
                                Id = "subact-1",
                                Name = "开灯",
                                TargetIp = "1.1.1.1",
                                TargetPort = 1,
                                Protocol = TransportProtocols.Udp,
                                DataType = DataTypes.Hex,
                                DataContent = "AA BB",
                                DelayMs = 10,
                            },
                        },
                    },
                },
            },
        },
    };
}
