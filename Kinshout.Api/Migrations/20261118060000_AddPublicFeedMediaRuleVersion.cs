using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

[Migration("20261118060000_AddPublicFeedMediaRuleVersion")]
public partial class AddPublicFeedMediaRuleVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PublicFeedMediaRuleVersion",
            table: "Discussions",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PublicFeedMediaRuleVersion", table: "Discussions");
    }
}
