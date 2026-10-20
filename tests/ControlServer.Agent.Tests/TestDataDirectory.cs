using ControlServer.Core.Abstractions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 测试用的数据目录：指向一个本进程独占的临时目录。
/// <para>
/// <b>为什么需要它</b>：<c>LogService</c>（会话侧通道的日志出口）要求一个 <see cref="IDataDirectory"/>。
/// 直接指向真实数据目录会让测试往 <c>%ProgramData%</c> 里写日志 ——
/// 测试污染被测机器的数据是绝对不能接受的。
/// </para>
/// </summary>
internal sealed class TestDataDirectory : IDataDirectory
{
    public TestDataDirectory(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "control.db");

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string FilesDirectory => Path.Combine(Root, "files");

    public string ImagesDirectory => Path.Combine(Root, "images");

    public string CertificateDirectory => Path.Combine(Root, "https");

    public string LicenseFile => Path.Combine(Root, "license.lic");

    public string TimeStampFile => Path.Combine(Root, "timestamp.bin");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(ImagesDirectory);
    }
}
