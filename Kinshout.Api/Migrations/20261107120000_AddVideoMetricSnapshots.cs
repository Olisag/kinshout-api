using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

[Migration("20261107120000_AddVideoMetricSnapshots")]
public partial class AddVideoMetricSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VideoMetricSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                ExternalId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Views = table.Column<int>(type: "int", nullable: false),
                Likes = table.Column<int>(type: "int", nullable: false),
                Comments = table.Column<int>(type: "int", nullable: false),
                TrendScore = table.Column<int>(type: "int", nullable: false),
                FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VideoMetricSnapshots", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VideoMetricSnapshots_Provider_ExternalId_FetchedAt",
            table: "VideoMetricSnapshots",
            columns: ["Provider", "ExternalId", "FetchedAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "VideoMetricSnapshots");
    }
}
