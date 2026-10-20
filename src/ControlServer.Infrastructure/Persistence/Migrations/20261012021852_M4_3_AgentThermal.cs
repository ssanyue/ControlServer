using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_3_AgentThermal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuTempAvgC",
                table: "agent_metrics_daily",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CpuTempMaxC",
                table: "agent_metrics_daily",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpuTempAvgC",
                table: "agent_metrics_daily",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StorageTempAvgC",
                table: "agent_metrics_daily",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CpuTempC",
                table: "agent_metrics",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpuTempC",
                table: "agent_metrics",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StorageTempC",
                table: "agent_metrics",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuTempAvgC",
                table: "agent_metrics_daily");

            migrationBuilder.DropColumn(
                name: "CpuTempMaxC",
                table: "agent_metrics_daily");

            migrationBuilder.DropColumn(
                name: "GpuTempAvgC",
                table: "agent_metrics_daily");

            migrationBuilder.DropColumn(
                name: "StorageTempAvgC",
                table: "agent_metrics_daily");

            migrationBuilder.DropColumn(
                name: "CpuTempC",
                table: "agent_metrics");

            migrationBuilder.DropColumn(
                name: "GpuTempC",
                table: "agent_metrics");

            migrationBuilder.DropColumn(
                name: "StorageTempC",
                table: "agent_metrics");
        }
    }
}
