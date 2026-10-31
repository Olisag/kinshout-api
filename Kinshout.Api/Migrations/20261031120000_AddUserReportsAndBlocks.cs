using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class AddUserReportsAndBlocks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserReports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReporterUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReportedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ResolvedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserReports", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserReports_Communities_CommunityId",
                    column: x => x.CommunityId,
                    principalTable: "Communities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_UserReports_Users_ReporterUserId",
                    column: x => x.ReporterUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_UserReports_Users_ReportedUserId",
                    column: x => x.ReportedUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_UserReports_Users_ResolvedByUserId",
                    column: x => x.ResolvedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "UserBlocks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BlockerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BlockedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserBlocks", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserBlocks_Users_BlockerUserId",
                    column: x => x.BlockerUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_UserBlocks_Users_BlockedUserId",
                    column: x => x.BlockedUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserReports_CommunityId_Status_CreatedAt",
            table: "UserReports",
            columns: ["CommunityId", "Status", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_UserReports_ReportedUserId",
            table: "UserReports",
            column: "ReportedUserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserBlocks_BlockerUserId_BlockedUserId",
            table: "UserBlocks",
            columns: ["BlockerUserId", "BlockedUserId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserBlocks_BlockedUserId",
            table: "UserBlocks",
            column: "BlockedUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserReports");
        migrationBuilder.DropTable(name: "UserBlocks");
    }
}
