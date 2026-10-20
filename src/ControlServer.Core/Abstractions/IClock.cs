namespace ControlServer.Core.Abstractions;

/// <summary>时间端口。全应用统一走北京时间（UTC+8），与系统时区解耦。</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}
