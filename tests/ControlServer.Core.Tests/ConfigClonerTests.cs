using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>深拷贝（复制按钮 / 子按钮集）的 Id 重生成与独立性测试。</summary>
public sealed class ConfigClonerTests
{
    [Fact]
    public void CloneButton_regenerates_all_ids_and_keeps_fields()
    {
        var source = BuildButton();

        var clone = ConfigCloner.CloneButton(source);

        clone.Id.Should().NotBe(source.Id);
        clone.Label.Should().Be(source.Label);
        clone.Order.Should().Be(source.Order);
        clone.TimerTimes.Should().Equal(source.TimerTimes);

        clone.Actions.Should().HaveCount(source.Actions.Count);
        clone.Actions[0].Id.Should().NotBe(source.Actions[0].Id);
        clone.Actions[0].TargetIp.Should().Be(source.Actions[0].TargetIp);

        clone.MonthlyPlans[0].Id.Should().NotBe(source.MonthlyPlans[0].Id);
    }

    [Fact]
    public void CloneButton_is_deep_copy()
    {
        var source = BuildButton();

        var clone = ConfigCloner.CloneButton(source);

        clone.Label = "改过的";
        clone.Actions[0].TargetIp = "0.0.0.0";
        clone.Actions.Add(new ButtonAction());

        source.Label.Should().Be("原始按钮");
        source.Actions[0].TargetIp.Should().Be("192.168.1.9");
        source.Actions.Should().HaveCount(1);
    }

    [Fact]
    public void CloneSubButtonSet_regenerates_set_sub_and_action_ids()
    {
        var source = new SubButtonSet
        {
            Label = "集",
            SubButtons =
            {
                new SubButton
                {
                    Label = "子",
                    Actions = { new ButtonAction { Name = "a" }, new ButtonAction { Name = "b" } },
                },
                new SubButton { Label = "子2" },
            },
        };

        var clone = ConfigCloner.CloneSubButtonSet(source);

        clone.Id.Should().NotBe(source.Id);
        clone.SubButtons[0].Id.Should().NotBe(source.SubButtons[0].Id);
        clone.SubButtons[1].Id.Should().NotBe(source.SubButtons[1].Id);
        clone.SubButtons[0].Actions[0].Id.Should().NotBe(source.SubButtons[0].Actions[0].Id);
        clone.SubButtons[0].Actions[1].Id.Should().NotBe(source.SubButtons[0].Actions[1].Id);

        // 集合内两个子按钮的 Id 也必须互不相同。
        clone.SubButtons.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CloneSettings_is_deep_copy()
    {
        var source = new ControlSettings { AppName = "A" };
        source.Buttons.Add(BuildButton());

        var clone = ConfigCloner.CloneSettings(source);

        clone.AppName = "B";
        clone.Buttons[0].Label = "改";

        source.AppName.Should().Be("A");
        source.Buttons[0].Label.Should().Be("原始按钮");
    }

    private static ButtonConfig BuildButton() => new()
    {
        Label = "原始按钮",
        Order = 3,
        TimerTimes = new List<string> { "08:00", "12:00" },
        Actions = { new ButtonAction { Name = "动作", TargetIp = "192.168.1.9" } },
        MonthlyPlans = { new MonthlyPlan { Name = "计划" } },
    };
}
