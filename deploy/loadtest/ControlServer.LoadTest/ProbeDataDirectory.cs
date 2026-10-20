using ControlServer.Core.Abstractions;

namespace ControlServer.LoadTest;

/// <summary>
/// 一个只给 <c>LogService</c> 用的临时数据目录。
///
/// <para>
/// 🔥 <b>为什么不复用服务端的 <c>FileSystemDataDirectory</c></b>：那个类在
/// <c>ControlServer.Infrastructure</c> 里，会把 EF / SQLite 一整串依赖拉进压测工具 ——
/// 而这个工具<b>一行服务端存储代码都不该有</b>（它必须只能通过网络看中心，
/// 否则"压测结果"就有可能是自己读写自己的假象）。
/// </para>
///
/// <para>
/// 它写的是假 Agent 的连接日志（每次连/断一行），落在系统临时目录，进程退出时删掉。
/// </para>
/// </summary>
public sealed class ProbeDataDirectory : IDataDirectory
{
    private readonly string _root;

    public ProbeDataDirectory()
    {
        _root = Path.Combine(Path.GetTempPath(), "controlserver-loadtest", Environment.ProcessId.ToString());
        EnsureCreated();
    }

    public string Root => _root;

    public string DatabaseFile => Path.Combine(_root, "loadtest.db");

    public string ConfigDirectory => Path.Combine(_root, "config");

    public string LogDirectory => Path.Combine(_root, "logs");

    public string FilesDirectory => Path.Combine(_root, "files");

    public string ImagesDirectory => Path.Combine(_root, "images");

    public string CertificateDirectory => Path.Combine(_root, "https");

    public string LicenseFile => Path.Combine(_root, "license.json");

    public string TimeStampFile => Path.Combine(_root, "timestamp.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>退出时清掉（删不掉也不算错 —— 临时目录会被系统回收）。</summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 故意空：压测结果的正确性不依赖这个目录是否删干净。
        }
    }
}
