using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAdvertSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactJson",
                table: "Adverts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "Adverts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DuplicateGroupId",
                table: "Adverts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalPublishedAt",
                table: "Adverts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceExternalId",
                table: "Adverts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceExternalUrl",
                table: "Adverts",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceFirstSeenAt",
                table: "Adverts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceImportedAt",
                table: "Adverts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceLastSeenAt",
                table: "Adverts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceProvider",
                table: "Adverts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceProviderName",
                table: "Adverts",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubcategorySlug",
                table: "Adverts",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Adverts_SourceProvider_SourceExternalId",
                table: "Adverts",
                columns: new[] { "SourceProvider", "SourceExternalId" },
                unique: true,
                filter: "[SourceProvider] IS NOT NULL AND [SourceExternalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Adverts_SourceProvider_SourceExternalId",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "ContactJson",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "DuplicateGroupId",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "ExternalPublishedAt",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceExternalId",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceExternalUrl",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceFirstSeenAt",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceImportedAt",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceLastSeenAt",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceProvider",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SourceProviderName",
                table: "Adverts");

            migrationBuilder.DropColumn(
                name: "SubcategorySlug",
                table: "Adverts");
        }
    }
}
