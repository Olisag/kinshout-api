using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPopularSearches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchQueryStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NormalizedQuery = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayQuery = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SearchCount = table.Column<int>(type: "int", nullable: false),
                    LastSearchedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchQueryStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchQueryStats_NormalizedQuery",
                table: "SearchQueryStats",
                column: "NormalizedQuery",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchQueryStats");
        }
    }
}
