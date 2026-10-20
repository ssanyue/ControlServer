using ControlServer.Contracts;

namespace ControlServer.Core.Models;

/// <summary>
/// 一天的遥测聚合（M4.2 冷层）。
/// <para>
/// <b>存在的理由</b>：5 秒明细只留 30 天，但"这台机器上个月稳不稳"这类问题一年后还会被问。
/// 明细删掉之后，只剩日聚合能回答它。
/// </para>
/// <para>
/// ⚠️ <b>只存"平均值 + 峰值 + 采样数"，不存中位数/标准差</b>：
/// 那些需要保留完整分布才能算，而保留分布等于没有聚合。需要它们就查明细（30 天内）。
/// </para>
/// </summary>
/// <param name="Day">哪一天（北京时间）。</param>
/// <param name="SampleCount">当天聚合进去的原始样本条数（"数据够不够"的唯一线索）。</param>
/// <param name="OnlineSeconds">当天估算的在线时长（秒）= 采样数 × 采样间隔，上限 86400。</param>
/// <param name="CpuTempAvgC">当天 CPU 温度均值（M4.3）；未启用温度监控时整天为 null。</param>
/// <param name="CpuTempMaxC">当天 CPU 温度峰值。**这是日聚合里温度最有用的一个数** —— "这台机器这周最高到过多少度"只能靠它回答。</param>
/// <param name="GpuTempAvgC">当天 GPU 温度均值；没有独显 / 取不到传感器时整天为 null。</param>
/// <param name="StorageTempAvgC">当天磁盘温度（多块取最高那块）的均值。</param>
public sealed record AgentMetricsDaily(
    DateOnly Day,
    int SampleCount,
    int OnlineSeconds,
    double? CpuAvgPct,
    double? CpuMaxPct,
    long? MemAvgMb,
    long? MemMaxMb,
    long? MemTotalMb,
    double? NetDownAvgBps,
    double? NetUpAvgBps,
    double? DiskReadAvgBps,
    double? DiskWriteAvgBps,
    double? GpuAvgPct,
    double? CpuTempAvgC = null,
    double? CpuTempMaxC = null,
    double? GpuTempAvgC = null,
    double? StorageTempAvgC = null);

/// <summary>
/// 一台机器最新一份硬件清单（M4.2）。
/// <para><b>只留最新一份</b> —— 型号和序列号不会变，留历史只会让库无限膨胀。</para>
/// </summary>
/// <param name="Inventory">
/// 清单本体。<b>可以为 null</b>：库里那一列 JSON 若损坏（手工改过 / 版本不兼容），
/// 反序列化会失败 —— 那时应当显示"清单缺失"，而不是让整张详情页打不开。
/// </param>
public sealed record AgentInventoryRecord(
    int AgentId,
    DateTimeOffset CapturedAt,
    AgentInventoryDto? Inventory);
