using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

[Migration("20261108110000_AddPublicFeedModerationFlags")]
public partial class AddPublicFeedModerationFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PublicFeedBlocked",
            table: "Discussions",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "PublicFeedRuleVersion",
            table: "Discussions",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PublicFeedBlocked", table: "Discussions");
        migrationBuilder.DropColumn(name: "PublicFeedRuleVersion", table: "Discussions");
    }
}
