using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class AddDiscussionVisibilityAndParticipants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Visibility",
            table: "Discussions",
            type: "nvarchar(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "public");

        migrationBuilder.CreateTable(
            name: "DiscussionParticipants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DiscussionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscussionParticipants", x => x.Id);
                table.ForeignKey(
                    name: "FK_DiscussionParticipants_Discussions_DiscussionId",
                    column: x => x.DiscussionId,
                    principalTable: "Discussions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_DiscussionParticipants_Users_ReviewedByUserId",
                    column: x => x.ReviewedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_DiscussionParticipants_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DiscussionParticipants_DiscussionId_Status_CreatedAt",
            table: "DiscussionParticipants",
            columns: ["DiscussionId", "Status", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_DiscussionParticipants_DiscussionId_UserId",
            table: "DiscussionParticipants",
            columns: ["DiscussionId", "UserId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DiscussionParticipants");
        migrationBuilder.DropColumn(name: "Visibility", table: "Discussions");
    }
}
