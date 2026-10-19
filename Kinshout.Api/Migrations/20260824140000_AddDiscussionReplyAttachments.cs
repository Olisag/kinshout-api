using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations;

/// <inheritdoc />
public partial class AddDiscussionReplyAttachments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ImageUrl",
            table: "DiscussionReplies",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VideoUrl",
            table: "DiscussionReplies",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Latitude",
            table: "DiscussionReplies",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Longitude",
            table: "DiscussionReplies",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PlaceName",
            table: "DiscussionReplies",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Address",
            table: "DiscussionReplies",
            type: "nvarchar(300)",
            maxLength: 300,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImageUrl", table: "DiscussionReplies");
        migrationBuilder.DropColumn(name: "VideoUrl", table: "DiscussionReplies");
        migrationBuilder.DropColumn(name: "Latitude", table: "DiscussionReplies");
        migrationBuilder.DropColumn(name: "Longitude", table: "DiscussionReplies");
        migrationBuilder.DropColumn(name: "PlaceName", table: "DiscussionReplies");
        migrationBuilder.DropColumn(name: "Address", table: "DiscussionReplies");
    }
}
