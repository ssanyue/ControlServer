using ControlServer.Core.Abstractions;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 端口覆盖文件（<c>server-overrides.json</c>）的落盘格式与读回能力。
///
/// <para>
/// <b>为什么要单独钉死"格式"</b>：这个文件同时充当一个标准 JSON 配置源，
/// 写入的形态必须能被 <c>Server:Http:Urls</c>（<b>string</b> 属性）绑定回来。
/// 曾经写成 JSON 数组 —— 数组会展开成 <c>Urls:0</c> / <c>Urls:1</c> 两组子键，
/// 父节点 <c>Urls</c> 的值是 null，绑定时被直接跳过；
/// 于是<b>写进去的值自己读不回来</b>，重启后端口悄悄回到默认值。
/// 这种"写的时候一切正常、重启才发现丢了"的缺陷只能靠格式级用例拦住。
/// </para>
/// </summary>
public sealed class ServerAddressOverrideStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ServerAddressOverrideStore NewStore() => new(_dir);

    [Fact]
    public void Save_writes_a_scalar_that_the_configuration_binder_reads_back()
    {
        var store = NewStore();

        store.Save(new[] { "http://0.0.0.0:9000" });

        // 用真实的配置绑定链路验证 —— 只断言文件文本会漏掉"格式对但绑不回"这一类问题。
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(store.FilePath)
            .Build();

        configuration["Server:Http:Urls"].Should().Be("http://0.0.0.0:9000");
    }

    [Fact]
    public void Save_joins_multiple_urls_into_one_scalar_instead_of_an_array()
    {
        var store = NewStore();

        store.Save(new[] { "http://0.0.0.0:9000", "http://0.0.0.0:9443" });

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(store.FilePath)
            .Build();

        configuration["Server:Http:Urls"]
            .Should().Be("http://0.0.0.0:9000;http://0.0.0.0:9443",
                "分号分隔的标量才能被 string 属性绑定回来；数组形式会静默丢失");

        // 数组展开出来的子键不该存在。
        configuration["Server:Http:Urls:0"].Should().BeNull();
    }

    [Fact]
    public void Save_then_read_round_trips_a_single_url()
    {
        var store = NewStore();

        store.Save(new[] { "http://127.0.0.1:8123" });

        store.TryReadUrls().Should().Equal(new[] { "http://127.0.0.1:8123" });
    }

    [Fact]
    public void Save_then_read_round_trips_multiple_urls()
    {
        var store = NewStore();

        store.Save(new[] { "http://0.0.0.0:9000", "http://0.0.0.0:9443" });

        store.TryReadUrls().Should().Equal(new[] { "http://0.0.0.0:9000", "http://0.0.0.0:9443" });
    }

    /// <summary>
    /// 早期版本写出的数组格式自身读不回来，但若要看"文件里到底记了什么"，
    /// 这里是唯一能看见真相的地方 —— 收敛逻辑依赖它。
    /// </summary>
    [Fact]
    public void Read_accepts_the_legacy_array_format()
    {
        var store = NewStore();

        File.WriteAllText(
            store.FilePath,
            """{"Server":{"Http":{"Urls":["http://0.0.0.0:1234","http://0.0.0.0:5678"]}}}""");

        store.TryReadUrls().Should().Equal(new[] { "http://0.0.0.0:1234", "http://0.0.0.0:5678" });
    }

    [Fact]
    public void Read_returns_null_when_the_file_is_absent()
    {
        var store = NewStore();

        store.TryReadUrls().Should().BeNull("不存在与'持久化了一份空列表'必须可区分");
        store.Exists.Should().BeFalse();
    }

    [Fact]
    public void Read_returns_null_for_a_corrupt_file()
    {
        var store = NewStore();
        File.WriteAllText(store.FilePath, "{ 这不是 JSON");

        store.TryReadUrls().Should().BeNull();
    }

    [Fact]
    public void Read_returns_null_when_the_section_is_missing()
    {
        var store = NewStore();
        File.WriteAllText(store.FilePath, """{"Server":{"Storage":{"DataDir":"D:\\x"}}}""");

        store.TryReadUrls().Should().BeNull();
    }

    /// <summary>
    /// 合并式写入：文件里与端口无关的键必须原样保留（用户可能手加过注释性配置）。
    /// </summary>
    [Fact]
    public void Save_preserves_unrelated_keys()
    {
        var store = NewStore();
        File.WriteAllText(
            store.FilePath,
            """{"Server":{"Storage":{"DataDir":"D:\\keep-me"},"Http":{"Urls":"http://0.0.0.0:1111"}}}""");

        store.Save(new[] { "http://0.0.0.0:2222" });

        var text = File.ReadAllText(store.FilePath);
        text.Should().Contain("keep-me", "与端口无关的键不能被擦掉");
        text.Should().Contain("2222");
        text.Should().NotContain("1111");
    }

    /// <summary>
    /// 写坏的文件不该让保存失败 —— 覆盖掉坏内容比留着它更安全。
    /// </summary>
    [Fact]
    public void Save_recovers_from_a_corrupt_file()
    {
        var store = NewStore();
        File.WriteAllText(store.FilePath, "{ 这不是 JSON");

        store.Save(new[] { "http://0.0.0.0:3333" });

        store.TryReadUrls().Should().Equal(new[] { "http://0.0.0.0:3333" });
    }

    [Fact]
    public void Clear_removes_the_file_and_is_idempotent()
    {
        var store = NewStore();
        store.Save(new[] { "http://0.0.0.0:4444" });

        store.Clear();
        store.Exists.Should().BeFalse();

        store.Clear();  // 再删一次不该抛
        store.Exists.Should().BeFalse();
    }

    /// <summary>每个实例一个独立临时目录，<see cref="Dispose"/> 时整棵删除。</summary>
    private sealed class TempDir : IDataDirectory, IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "cs-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string DatabaseFile => Path.Combine(Root, "control.db");

        public string ConfigDirectory => Path.Combine(Root, "config");

        public string LogDirectory => Path.Combine(Root, "logs");

        public string FilesDirectory => Path.Combine(Root, "files");

        public string ImagesDirectory => Path.Combine(Root, "images");

        public string CertificateDirectory => Path.Combine(Root, "https");

        public string LicenseFile => Path.Combine(Root, "license.json");

        public string TimeStampFile => Path.Combine(Root, "timestamp.json");

        public void EnsureCreated() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}
