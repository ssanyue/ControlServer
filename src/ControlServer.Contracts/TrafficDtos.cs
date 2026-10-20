namespace ControlServer.Contracts;

/// <summary>
/// 一条**收发日志**（M2-F）。
/// <para>
/// 与运维日志 <see cref="LogDto"/> 是两回事：本类型记录的是「某个按钮的某个动作，
/// 向哪个地址发了什么字节 / 从哪个地址收到了什么字节」，对应旧版主界面的「最近日志」面板。
/// </para>
/// </summary>
/// <param name="Time">北京时间。</param>
/// <param name="Direction">「发送」/「接收」。</param>
/// <param name="Protocol">UDP / TCP / WoL。</param>
/// <param name="TargetIp">对端 IP（接收方向为来源 IP）。</param>
/// <param name="Port">端口。</param>
/// <param name="Data">数据内容（服务端已按 200 字符截断）。</param>
/// <param name="Success">是否成功（接收方向恒为 true）。</param>
/// <param name="Message">附加说明（失败原因 / 命中的按钮等）。</param>
/// <param name="ButtonName">触发的按钮 / 子按钮名称。</param>
/// <param name="ActionName">触发的动作名称。</param>
public sealed record TrafficLogDto(
    DateTimeOffset Time,
    string Direction,
    string Protocol,
    string TargetIp,
    int Port,
    string Data,
    bool Success,
    string Message,
    string ButtonName,
    string ActionName);
