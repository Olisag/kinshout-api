using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class AddCommunityVisibilityAndMembers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Visibility",
            table: "Communities",
            type: "nvarchar(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "public");

        migrationBuilder.CreateTable(
            name: "CommunityMembers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CommunityMembers", x => x.Id);
                table.ForeignKey(
                    name: "FK_CommunityMembers_Communities_CommunityId",
                    column: x => x.CommunityId,
                    principalTable: "Communities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CommunityMembers_Users_ReviewedByUserId",
                    column: x => x.ReviewedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_CommunityMembers_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CommunityMembers_CommunityId_Status_CreatedAt",
            table: "CommunityMembers",
            columns: ["CommunityId", "Status", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_CommunityMembers_CommunityId_UserId",
            table: "CommunityMembers",
            columns: ["CommunityId", "UserId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CommunityMembers");
        migrationBuilder.DropColumn(name: "Visibility", table: "Communities");
    }
}
