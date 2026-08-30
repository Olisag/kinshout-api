using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class AddCommunityIsActive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            table: "Communities",
            type: "bit",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsActive", table: "Communities");
    }
}
