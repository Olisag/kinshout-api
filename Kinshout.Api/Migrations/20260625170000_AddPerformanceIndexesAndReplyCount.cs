using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    [DbContext(typeof(KinshoutDbContext))]
    [Migration("20260625170000_AddPerformanceIndexesAndReplyCount")]
    /// <inheritdoc />
    public partial class AddPerformanceIndexesAndReplyCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplyCount",
                table: "Discussions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE d
                SET ReplyCount = counts.Total
                FROM Discussions d
                INNER JOIN (
                    SELECT DiscussionId, COUNT(*) AS Total
                    FROM DiscussionReplies
                    GROUP BY DiscussionId
                ) counts ON counts.DiscussionId = d.Id
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Adverts_IsPublished_CreatedAt",
                table: "Adverts",
                columns: new[] { "IsPublished", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Adverts_IsPublished_ViewCount_CreatedAt",
                table: "Adverts",
                columns: new[] { "IsPublished", "ViewCount", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Adverts_UserId_IsPublished_CreatedAt",
                table: "Adverts",
                columns: new[] { "UserId", "IsPublished", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Adverts_CategoryId_IsPublished_CreatedAt",
                table: "Adverts",
                columns: new[] { "CategoryId", "IsPublished", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Discussions_CreatedAt",
                table: "Discussions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Discussions_ReplyCount_CreatedAt",
                table: "Discussions",
                columns: new[] { "ReplyCount", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Discussions_UserId_UpdatedAt",
                table: "Discussions",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscussionReplies_DiscussionId_CreatedAt",
                table: "DiscussionReplies",
                columns: new[] { "DiscussionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscussionReplies_UserId_DiscussionId",
                table: "DiscussionReplies",
                columns: new[] { "UserId", "DiscussionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscussionReplies_UserId_DiscussionId",
                table: "DiscussionReplies");

            migrationBuilder.DropIndex(
                name: "IX_DiscussionReplies_DiscussionId_CreatedAt",
                table: "DiscussionReplies");

            migrationBuilder.DropIndex(
                name: "IX_Discussions_UserId_UpdatedAt",
                table: "Discussions");

            migrationBuilder.DropIndex(
                name: "IX_Discussions_ReplyCount_CreatedAt",
                table: "Discussions");

            migrationBuilder.DropIndex(
                name: "IX_Discussions_CreatedAt",
                table: "Discussions");

            migrationBuilder.DropIndex(
                name: "IX_Adverts_CategoryId_IsPublished_CreatedAt",
                table: "Adverts");

            migrationBuilder.DropIndex(
                name: "IX_Adverts_UserId_IsPublished_CreatedAt",
                table: "Adverts");

            migrationBuilder.DropIndex(
                name: "IX_Adverts_IsPublished_ViewCount_CreatedAt",
                table: "Adverts");

            migrationBuilder.DropIndex(
                name: "IX_Adverts_IsPublished_CreatedAt",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "ReplyCount",
                table: "Discussions");
        }
    }
}
