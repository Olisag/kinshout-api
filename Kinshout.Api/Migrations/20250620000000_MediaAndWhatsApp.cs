using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

public partial class MediaAndWhatsApp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WhatsAppNumber",
            table: "Users",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImageUrlsJson",
            table: "Adverts",
            type: "nvarchar(max)",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<string>(
            name: "ResumeUrl",
            table: "Adverts",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE Adverts
            SET ImageUrlsJson = CONCAT('["', REPLACE(ImageUrl, '"', '\"'), '"]')
            WHERE ImageUrl IS NOT NULL AND LTRIM(RTRIM(ImageUrl)) <> ''
              AND (ImageUrlsJson IS NULL OR ImageUrlsJson = '[]')
            """);

        migrationBuilder.Sql(
            """
            UPDATE Users
            SET WhatsAppNumber = Phone
            WHERE (WhatsAppNumber IS NULL OR LTRIM(RTRIM(WhatsAppNumber)) = '')
              AND Phone IS NOT NULL AND LTRIM(RTRIM(Phone)) <> ''
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "WhatsAppNumber", table: "Users");
        migrationBuilder.DropColumn(name: "ImageUrlsJson", table: "Adverts");
        migrationBuilder.DropColumn(name: "ResumeUrl", table: "Adverts");
    }
}
