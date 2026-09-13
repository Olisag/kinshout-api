using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class AddVideoAssets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VideoAssets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                VideoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                PosterUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ByteSize = table.Column<long>(type: "bigint", nullable: false),
                OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VideoAssets", x => x.Id);
                table.ForeignKey(
                    name: "FK_VideoAssets_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VideoAssets_UserId_CreatedAt",
            table: "VideoAssets",
            columns: ["UserId", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_VideoAssets_VideoUrl",
            table: "VideoAssets",
            column: "VideoUrl");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "VideoAssets");
    }
}
