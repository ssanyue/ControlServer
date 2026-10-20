using ControlServer.Core.Abstractions;

namespace ControlServer.Infrastructure.Storage;

/// <summary>基于文件系统的数据目录实现（Windows / Linux 通用，根路径由宿主解析后注入）。</summary>
public sealed class FileSystemDataDirectory : IDataDirectory
{
    public FileSystemDataDirectory(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("数据目录不能为空", nameof(root));
        Root = root;
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "control.db");

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string FilesDirectory => Path.Combine(Root, "files");

    public string ImagesDirectory => Path.Combine(Root, "images");

    public string CertificateDirectory => Path.Combine(Root, "https");

    public string LicenseFile => Path.Combine(Root, "license.lic");

    public string TimeStampFile => Path.Combine(Root, ".ts");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(CertificateDirectory);
    }
}
