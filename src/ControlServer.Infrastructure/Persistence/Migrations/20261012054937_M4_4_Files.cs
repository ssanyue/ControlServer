using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_4_Files : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "distribution_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReceivedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartedAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAtMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "distribution_tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetDir = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_distribution_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "file_repo_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UploadedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginAgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_repo_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "file_upload_sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OriginAgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_upload_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_distribution_items_AgentId_State",
                table: "distribution_items",
                columns: new[] { "AgentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_distribution_items_TaskId_AgentId",
                table: "distribution_items",
                columns: new[] { "TaskId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_distribution_tasks_CreatedAtMs",
                table: "distribution_tasks",
                column: "CreatedAtMs");

            migrationBuilder.CreateIndex(
                name: "IX_file_repo_items_Kind_UploadedAtMs",
                table: "file_repo_items",
                columns: new[] { "Kind", "UploadedAtMs" });

            migrationBuilder.CreateIndex(
                name: "IX_file_repo_items_OriginAgentId",
                table: "file_repo_items",
                column: "OriginAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_file_repo_items_StoredName",
                table: "file_repo_items",
                column: "StoredName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_upload_sessions_ExpiresAtMs",
                table: "file_upload_sessions",
                column: "ExpiresAtMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "distribution_items");

            migrationBuilder.DropTable(
                name: "distribution_tasks");

            migrationBuilder.DropTable(
                name: "file_repo_items");

            migrationBuilder.DropTable(
                name: "file_upload_sessions");
        }
    }
}
