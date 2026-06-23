using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Kinshout.Api.Data;

#nullable disable

namespace Kinshout.Api.Migrations
{
    [DbContext(typeof(KinshoutDbContext))]
    [Migration("20260624120000_AddUserDisplayPreference")]
    /// <inheritdoc />
    public partial class AddUserDisplayPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayPreference",
                table: "Users",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "clair");

            migrationBuilder.Sql(
                "UPDATE Users SET DisplayPreference = N'clair' WHERE DisplayPreference IS NULL OR DisplayPreference NOT IN (N'clair', N'sombre')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayPreference",
                table: "Users");
        }
    }
}
