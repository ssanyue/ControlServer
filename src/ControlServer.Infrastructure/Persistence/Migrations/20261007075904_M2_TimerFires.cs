using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_TimerFires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "timer_fires",
                columns: table => new
                {
                    RefId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FireDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    FireTime = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    FiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timer_fires", x => new { x.RefId, x.FireDate, x.FireTime });
                });

            migrationBuilder.CreateIndex(
                name: "IX_timer_fires_FireDate",
                table: "timer_fires",
                column: "FireDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "timer_fires");
        }
    }
}
