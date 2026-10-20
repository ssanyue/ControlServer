namespace ControlServer.Core;

/// <summary>
/// 配置文档键（<c>config_documents</c> 表的主键）。
/// <para>
/// 指令库刻意**独立成键** —— 与旧版 <c>commands.json</c> 独立于配置文件的语义一致，
/// 使它**不参与配置导入导出**。
/// </para>
/// </summary>
public static class ConfigKeys
{
    /// <summary>全局配置（按钮 / 子按钮集 / 外观 / 端口）。</summary>
    public const string Settings = "settings";

    /// <summary>指令库。</summary>
    public const string Commands = "commands";

    /// <summary>保留：布局相关（M2.5 自由排布若需独立存储时使用）。</summary>
    public const string Layout = "layout";
}
