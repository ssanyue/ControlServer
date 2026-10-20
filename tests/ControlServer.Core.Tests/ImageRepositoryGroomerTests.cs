using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 图片目录对账（<c>images/</c>）：<b>配置里的引用 ↔ 盘上的文件</b>。
///
/// <para>
/// <b>为什么这批用例必须存在</b>：图片对账的判据与文件仓库<b>完全不同</b> ——
/// 文件仓库问"库里有记录吗"，图片问"配置里还有人引用吗"。而后者的匹配口径
/// （按文件名最后一段、忽略大小写）必须与删除端点、前端 <c>imageUrl()</c> 三处一致。
/// 没有用例钉住，"对账认为没人用"与"删除端点认为有人用"就会各走一边。
/// </para>
///
/// <para>
/// 🔴 <b>最要紧的一条是 <c>New_upload_is_not_orphan_inside_grace</c></b>：
/// 上传的落盘与"配置里写上这个名字"是<b>两次独立的写</b>。少了宽限期，
/// 一个"看起来更激进"的实现会在用户正看着编辑页面时删掉他刚上传的图。
/// </para>
///
/// <para>
/// 🔴 <b>第二要紧的是 <c>Config_unreadable_never_orphans</c></b>：
/// 配置损坏时"没人引用"只说明我们没读懂，不能推出"可以删"。这条一旦失守，
/// 一次配置损坏会连图一起删掉 —— 那是不可恢复的（配置还能从备份恢复，图没了就没了）。
/// </para>
/// </summary>
public sealed class ImageRepositoryGroomerTests : IDisposable
{
    /// <summary>固定的"现在"，理由同 <see cref="RepositoryGroomerTests"/>。</summary>
    private static readonly DateTime NowUtc = new(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "imggroom-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string _images;

    public ImageRepositoryGroomerTests()
    {
        _images = Path.Combine(_root, "images");
        Directory.CreateDirectory(_images);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响结论。
        }
    }

    // ═══════════════════════════════════════════════════
    // 孤儿：盘上有、没人引用
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Unreferenced_and_old_is_orphan_and_gets_deleted()
    {
        MakeFile(_images, "imgdead.png", bytes: 256, ageHours: 30);

        var survey = Scan(settings: new ControlSettings());

        survey.Orphans.Should().ContainSingle()
            .Which.Name.Should().Be("imgdead.png", "要指名道姓，不能只报个数");

        survey.OrphanBytes.Should().Be(256);
        survey.DiskFiles.Should().Be(1);
        survey.DiskBytes.Should().Be(256);

        var result = ImageRepositoryGroomer.Collect(survey, _images);

        result.DeletedFiles.Should().Be(1);
        result.DeletedBytes.Should().Be(256);
        result.Failures.Should().BeEmpty();

        File.Exists(Path.Combine(_images, "imgdead.png")).Should().BeFalse();
    }

    [Fact]
    public void New_upload_is_not_orphan_inside_grace()
    {
        // 刚落盘、配置里还没写上它 —— 这正是"上传成功、用户还没点保存"的那几秒。
        MakeFile(_images, "imgfresh.png", bytes: 64, ageHours: 0.5);

        var survey = Scan(settings: new ControlSettings());

        survey.Orphans.Should().BeEmpty("宽限期内的文件可能正处在「已落盘、配置未保存」的窗口里");
        survey.DiskFiles.Should().Be(1, "它仍然是一张在盘上的图，计数要算它");
    }

    [Fact]
    public void Background_reference_keeps_file()
    {
        MakeFile(_images, "imgbg.png", bytes: 100, ageHours: 48);

        var settings = new ControlSettings { BackgroundImagePath = @"D:\data\images\imgbg.png" };

        var survey = Scan(settings);

        survey.Orphans.Should().BeEmpty();
        survey.Lost.Should().BeEmpty();
    }

    [Fact]
    public void Button_reference_matches_by_last_segment_ignoring_case()
    {
        // 盘上是小写，配置里写的是大写 + 别的目录 —— 前端 imageUrl() 就是按最后一段取的。
        MakeFile(_images, "imgabc.png", bytes: 100, ageHours: 48);

        var settings = new ControlSettings
        {
            Buttons =
            [
                new ButtonConfig { Label = "开灯", ButtonImage = @"E:\old\place\IMGABC.PNG" },
            ],
        };

        var survey = Scan(settings);

        survey.Orphans.Should().BeEmpty("按最后一段、忽略大小写匹配，与删除端点/前端保持一致");
    }

    [Fact]
    public void Sub_button_set_reference_keeps_file()
    {
        MakeFile(_images, "imgset.png", bytes: 10, ageHours: 48);

        var settings = new ControlSettings
        {
            SubButtonSets =
            [
                new SubButtonSet
                {
                    Label = "灯光组",
                    ButtonImage = @"D:\data\images\imgset.png",
                    SubButtons = [],
                },
            ],
        };

        var survey = Scan(settings);

        survey.Orphans.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════
    // 断链：配置里引用了、盘上没有
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Missing_referenced_image_is_lost_and_names_its_referrers()
    {
        var settings = new ControlSettings
        {
            DefaultButtonImage = @"D:\data\images\imgone.png",
            Buttons =
            [
                new ButtonConfig { Label = "开灯", ButtonImage = @"D:\data\images\imgone.png" },
            ],
        };

        var survey = Scan(settings);

        survey.Lost.Should().ContainSingle()
            .Which.Name.Should().Be("imgone.png", "断链要指名是哪一个文件");

        survey.Lost[0].Referrers.Should().Contain("默认按钮图片")
            .And.Contain("按钮「开灯」", "必须说清谁在用它，否则现场不知道要去改哪里");
    }

    [Fact]
    public void Lost_is_reported_regardless_of_grace()
    {
        // 引用已经写进配置了 —— 盘上没有就是没有，跟"新不新"无关。
        var settings = new ControlSettings { BackgroundImagePath = @"D:\data\images\imggone.png" };

        var survey = Scan(settings);

        survey.Lost.Should().ContainSingle();
    }

    // ═══════════════════════════════════════════════════
    // 配置读不出来
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Config_unreadable_never_orphans()
    {
        MakeFile(_images, "imgkeep.png", bytes: 100, ageHours: 100);

        var survey = ImageRepositoryGroomer.Scan(
            settings: null,
            _images,
            NowUtc,
            Grace);

        survey.Orphans.Should().BeEmpty("读不懂配置时，「没人引用」只说明我们不知道谁在引用");
        survey.DiskFiles.Should().Be(1, "但仍然要能看见这个目录有多大");
    }

    // ═══════════════════════════════════════════════════
    // 残留 .tmp（原子写的中间态）
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Stale_temp_is_reported_but_not_counted_as_an_image()
    {
        MakeFile(_images, "imgx.png.tmp", bytes: 40, ageHours: 48);

        var survey = Scan(new ControlSettings());

        survey.StaleTemps.Should().ContainSingle()
            .Which.Name.Should().Be("imgx.png.tmp");

        survey.Orphans.Should().BeEmpty("临时文件不是「没人引用的图片」，而是写了一半的中间态");
        survey.DiskFiles.Should().Be(0, "临时文件不是图片，不能计入 images.count");
        survey.DiskBytes.Should().Be(0);
    }

    [Fact]
    public void Fresh_temp_is_left_alone()
    {
        MakeFile(_images, "imgy.png.tmp", bytes: 40, ageHours: 0.1);

        var survey = Scan(new ControlSettings());

        survey.StaleTemps.Should().BeEmpty("正在写的临时文件不能碰");
    }

    // ═══════════════════════════════════════════════════
    // 回收只动清单里的
    // ═══════════════════════════════════════════════════

    [Fact]
    public void Collect_deletes_only_listed_files_and_leaves_referenced_ones()
    {
        MakeFile(_images, "imgused.png", bytes: 50, ageHours: 48);
        MakeFile(_images, "imgtrash.png", bytes: 60, ageHours: 48);
        MakeFile(_images, "imgstale.tmp", bytes: 70, ageHours: 48);

        var settings = new ControlSettings { DefaultButtonImage = @"D:\data\images\imgused.png" };

        var survey = Scan(settings);

        var result = ImageRepositoryGroomer.Collect(survey, _images);

        // 🔥 三问：删了几个、删的是哪个、**该留的还在不在**。
        result.DeletedFiles.Should().Be(2, "一个孤儿 + 一个残留临时文件");
        result.DeletedBytes.Should().Be(130);

        File.Exists(Path.Combine(_images, "imgused.png")).Should().BeTrue("被引用的图一个都不能少");
        File.Exists(Path.Combine(_images, "imgtrash.png")).Should().BeFalse();
        File.Exists(Path.Combine(_images, "imgstale.tmp")).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════

    private ImageSurvey Scan(ControlSettings? settings)
        => ImageRepositoryGroomer.Scan(settings, _images, NowUtc, Grace);

    private static string MakeFile(string dir, string name, int bytes, double ageHours)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);

        // 显式写 mtime —— 判龄靠它，不靠"创建时间"。
        File.SetLastWriteTimeUtc(path, NowUtc.AddHours(-ageHours));

        return path;
    }
}
