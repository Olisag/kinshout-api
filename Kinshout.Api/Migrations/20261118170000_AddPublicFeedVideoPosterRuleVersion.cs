using Microsoft.EntityFrameworkCore.Migrations;

namespace Kinshout.Api.Migrations;

[Migration("20261118170000_AddPublicFeedVideoPosterRuleVersion")]
public partial class AddPublicFeedVideoPosterRuleVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PublicFeedVideoPosterRuleVersion",
            table: "Discussions",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PublicFeedVideoPosterRuleVersion", table: "Discussions");
    }
}
