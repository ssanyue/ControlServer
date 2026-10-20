namespace ControlServer.Contracts;

/// <summary>服务运行状态快照（M1 骨架字段；M2 会补按钮数、定时任务数等）。</summary>
public sealed record StatusSnapshot(
    string Product,
    string Version,
    string MachineName,
    string OsPlatform,
    DateTimeOffset ServerTime,
    string TimeZone,
    int HttpPort,
    int PairingPort,
    bool ListenerRunning,
    long UptimeSeconds,
    int RecentLogCount);

public sealed record ActionResultDto(string RunId, bool Ok, string Message, DateTimeOffset Timestamp);

public sealed record UdpTestRequest(string Host, int Port, string Payload);

public sealed record UdpTestResponse(string RunId, bool Ok, string Message, int BytesSent, string Target);

public sealed record LoopbackTestResponse(string RunId, bool Ok, string Message, int TargetPort, int BytesSent);
