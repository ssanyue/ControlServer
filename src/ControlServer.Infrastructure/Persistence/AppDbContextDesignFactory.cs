using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 设计时（<c>dotnet ef</c>）DbContext 工厂。
/// <para>
/// 为什么需要它：宿主 <c>Program.cs</c> 是顶层语句 + 一大堆副作用（建目录、开日志、绑端口）。
/// 若让 EF 通过 "启动项目" 去构造宿主，会把**服务真的拉起来**，生成迁移这种离线操作完全不该如此。
/// 这里提供一个纯离线的构造路径 —— 连接串只是为了满足构造要求，<c>migrations add</c> 不访问数据库。
/// </para>
/// </summary>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=controlserver-designtime.db")
            .Options;

        return new AppDbContext(options);
    }
}
