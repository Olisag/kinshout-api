using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropUserUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_Users_Username'
                      AND object_id = OBJECT_ID('Users')
                )
                    DROP INDEX IX_Users_Username ON Users;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM sys.columns
                    WHERE name = 'Username'
                      AND object_id = OBJECT_ID('Users')
                )
                    ALTER TABLE Users DROP COLUMN Username;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Users",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true,
                filter: "[Username] IS NOT NULL");
        }
    }
}
