namespace ControlServer.Core.Abstractions;

/// <summary>
/// 机器码来源。Windows 走 WMI（CPU/主板/UUID），Linux 走 /etc/machine-id + DMI UUID。
/// M1 提供 Windows 实现 + 回退；完整的跨平台一致性在 M3 收敛。
/// </summary>
public interface IMachineCodeProvider
{
    /// <summary>原始指纹（可能含分隔符，用于调试）。</summary>
    string GetRawFingerprint();

    /// <summary>XXXX-XXXX-XXXX-XXXX 形式的机器码。</summary>
    string GetMachineCode();
}
