using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_2_AgentTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_inventories",
                columns: table => new
                {
                    AgentId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_inventories", x => x.AgentId);
                });

            migrationBuilder.CreateTable(
                name: "agent_metrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    AtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CpuPct = table.Column<double>(type: "REAL", nullable: true),
                    MemUsedMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemTotalMb = table.Column<long>(type: "INTEGER", nullable: true),
                    DiskReadBps = table.Column<double>(type: "REAL", nullable: true),
                    DiskWriteBps = table.Column<double>(type: "REAL", nullable: true),
                    NetUpBps = table.Column<double>(type: "REAL", nullable: true),
                    NetDownBps = table.Column<double>(type: "REAL", nullable: true),
                    GpuPct = table.Column<double>(type: "REAL", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_metrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_metrics_daily",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlineSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvgPct = table.Column<double>(type: "REAL", nullable: true),
                    CpuMaxPct = table.Column<double>(type: "REAL", nullable: true),
                    MemAvgMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemMaxMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemTotalMb = table.Column<long>(type: "INTEGER", nullable: true),
                    NetDownAvgBps = table.Column<double>(type: "REAL", nullable: true),
                    NetUpAvgBps = table.Column<double>(type: "REAL", nullable: true),
                    DiskReadAvgBps = table.Column<double>(type: "REAL", nullable: true),
                    DiskWriteAvgBps = table.Column<double>(type: "REAL", nullable: true),
                    GpuAvgPct = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_metrics_daily", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_power_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    AtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_power_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_metrics_AgentId_AtMs",
                table: "agent_metrics",
                columns: new[] { "AgentId", "AtMs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_metrics_daily_AgentId_Day",
                table: "agent_metrics_daily",
                columns: new[] { "AgentId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_power_events_AgentId_AtMs_EventId",
                table: "agent_power_events",
                columns: new[] { "AgentId", "AtMs", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_inventories");

            migrationBuilder.DropTable(
                name: "agent_metrics");

            migrationBuilder.DropTable(
                name: "agent_metrics_daily");

            migrationBuilder.DropTable(
                name: "agent_power_events");
        }
    }
}
