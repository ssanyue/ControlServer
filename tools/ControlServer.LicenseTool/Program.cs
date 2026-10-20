using System.Text;
using ControlServer.Core.Licensing;
using ControlServer.LicenseTool;
using ControlServer.LicenseTool.Commands;

// ═══════════════════════════════════════════════════════════════════════
// 授权签发工具（厂商侧）的入口。
//
// 设计取向：**所有"可能出错的事"都必须以人能读懂的方式说清楚并给出下一步**。
// 用这个工具的人往往不是开发者（销售、现场支持），而且通常是在"客户催着要授权"
// 的时间压力下用它。一句"参数错误"和一句"…请加 --force"的差别，就是十分钟和半小时。
//
// 退出码：0 成功 / 1 用法有误 / 2 授权不可用或规则不允许 / 3 未预期错误
// ═══════════════════════════════════════════════════════════════════════

TryUseUtf8Console();

if (args.Length == 0)
{
    Usage.Print();
    return 1;
}

var command = args[0];

if (command is "-h" or "--help" or "help" or "/?")
{
    Usage.Print();
    return 0;
}

if (command is "-v" or "--version" or "version")
{
    Console.WriteLine($"{Usage.ToolName} {Usage.Version}");
    return 0;
}

try
{
    return command.ToLowerInvariant() switch
    {
        "new-key" or "newkey" => await NewKeyCommand.RunAsync(args[1..]),
        "gen" or "generate" => await GenerateCommand.RunAsync(args[1..]),
        "inspect" or "show" => await InspectCommand.RunAsync(args[1..]),
        _ => Unknown(command),
    };
}
catch (UsageException ex)
{
    ConsoleOut.Error(ex.Message);
    ConsoleOut.Hint($"看用法：{Usage.ToolName} {ex.Command} --help");
    return 1;
}
catch (LicenseValidationException ex)
{
    // 规则不允许（永久+通用、有效期已过…）或授权文本不可解析。文案已经是人话。
    ConsoleOut.Error(ex.Message);
    return 2;
}
catch (ToolAbortException ex)
{
    // 命令已经自己说明过原因了，这里只交回退出码，不重复打印。
    return ex.ExitCode;
}
catch (Exception ex)
{
    ConsoleOut.Error($"未预期的错误：{ex.GetType().Name}: {ex.Message}");
    ConsoleOut.Hint("这属于工具自身的缺陷，请把上面的信息一并反馈给开发方。");
    return 3;
}

static int Unknown(string command)
{
    ConsoleOut.Error($"未知命令「{command}」。");
    Usage.Print();
    return 1;
}

static void TryUseUtf8Console()
{
    // 尽力而为：把输出固定成 UTF-8，避免授权文件路径/客户名里的中文在重定向时变成乱码。
    // 某些宿主（无控制台、输出已被接到管道）会让这次赋值抛异常 —— 那不该妨碍一次签发。
    try
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
    catch
    {
        // 忽略：正文本来就是 UTF-8 字节，读的人按 UTF-8 解即可。
    }
}
