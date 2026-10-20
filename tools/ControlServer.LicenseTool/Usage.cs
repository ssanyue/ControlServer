namespace ControlServer.LicenseTool;

/// <summary>顶层帮助。</summary>
internal static class Usage
{
    public const string ToolName = "ControlServer.LicenseTool";

    public static void Print()
    {
        Console.WriteLine();
        Console.WriteLine($"{ToolName} —— 授权签发工具（厂商侧）。");
        Console.WriteLine("生成密钥对、签发 .lic 授权文件、解读已有的授权文件。");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine($"  {ToolName} <命令> [参数…]");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  new-key   生成 ECDSA P-256 密钥对（换钥用；日常签发不需要它），并打印要替换进服务端的公钥");
        Console.WriteLine("  gen       按机器码或通用方式签发一份 .lic（默认使用工具内嵌的厂商密钥）");
        Console.WriteLine("  inspect   解读一份 .lic：字段、签名、在它自己的机器上会是什么状态");
        Console.WriteLine();
        Console.WriteLine("常用：");
        Console.WriteLine($"  {ToolName} new-key --out vendor-private.pem");
        Console.WriteLine($"  {ToolName} gen --type full --bind AAAA-BBBB-CCCC-DDDD --expire 2027-10-08 --customer \"XX展厅\"");
        Console.WriteLine($"  {ToolName} inspect 授权_XX展厅.lic");
        Console.WriteLine();
        Console.WriteLine("每个命令都支持 --help 查看完整参数。");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功；1 命令用法有误 / 文件不存在；2 授权文件不可用；3 未预期错误。");
    }

    /// <summary>工具版本（取自程序集）。</summary>
    public static string Version
        => typeof(Usage).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
