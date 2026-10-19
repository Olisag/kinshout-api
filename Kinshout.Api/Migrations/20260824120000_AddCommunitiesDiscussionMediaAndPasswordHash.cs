using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

/// <inheritdoc />
public partial class AddCommunitiesDiscussionMediaAndPasswordHash : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PasswordHash",
            table: "Users",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "Communities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Communities", x => x.Id);
                table.ForeignKey(
                    name: "FK_Communities_Users_CreatedByUserId",
                    column: x => x.CreatedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "CommunityId",
            table: "Discussions",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImageUrlsJson",
            table: "Discussions",
            type: "nvarchar(max)",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<string>(
            name: "VideoUrlsJson",
            table: "Discussions",
            type: "nvarchar(max)",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateIndex(
            name: "IX_Communities_Slug",
            table: "Communities",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Discussions_CommunityId_CreatedAt",
            table: "Discussions",
            columns: ["CommunityId", "CreatedAt"]);

        migrationBuilder.AddForeignKey(
            name: "FK_Discussions_Communities_CommunityId",
            table: "Discussions",
            column: "CommunityId",
            principalTable: "Communities",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Discussions_Communities_CommunityId",
            table: "Discussions");

        migrationBuilder.DropIndex(
            name: "IX_Discussions_CommunityId_CreatedAt",
            table: "Discussions");

        migrationBuilder.DropColumn(name: "CommunityId", table: "Discussions");
        migrationBuilder.DropColumn(name: "ImageUrlsJson", table: "Discussions");
        migrationBuilder.DropColumn(name: "VideoUrlsJson", table: "Discussions");
        migrationBuilder.DropColumn(name: "PasswordHash", table: "Users");
        migrationBuilder.DropTable(name: "Communities");
    }
}
