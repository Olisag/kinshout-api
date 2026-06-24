using System;
using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    [DbContext(typeof(KinshoutDbContext))]
    [Migration("20260624130000_AddAdvertLikeCount")]
    /// <inheritdoc />
    public partial class AddAdvertLikeCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LikeCount",
                table: "Adverts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE a
                SET LikeCount = counts.Total
                FROM Adverts a
                INNER JOIN (
                    SELECT AdvertId, COUNT(*) AS Total
                    FROM SavedAdverts
                    GROUP BY AdvertId
                ) counts ON counts.AdvertId = a.Id
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LikeCount",
                table: "Adverts");
        }
    }
}
