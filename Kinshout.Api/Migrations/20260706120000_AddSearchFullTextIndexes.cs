using Kinshout.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinshout.Api.Migrations
{
    [DbContext(typeof(KinshoutDbContext))]
    [Migration("20260706120000_AddSearchFullTextIndexes")]
    /// <inheritdoc />
    public partial class AddSearchFullTextIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'KinshoutSearchCatalog')
                    CREATE FULLTEXT CATALOG KinshoutSearchCatalog AS DEFAULT
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Adverts')
                )
                CREATE FULLTEXT INDEX ON Adverts(Title, Description, Location, TagsJson)
                KEY INDEX PK_Adverts
                ON KinshoutSearchCatalog
                WITH CHANGE_TRACKING AUTO
                """);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Discussions')
                )
                CREATE FULLTEXT INDEX ON Discussions(Title, Body)
                KEY INDEX PK_Discussions
                ON KinshoutSearchCatalog
                WITH CHANGE_TRACKING AUTO
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Discussions')
                )
                    DROP FULLTEXT INDEX ON Discussions
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Adverts')
                )
                    DROP FULLTEXT INDEX ON Adverts
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'KinshoutSearchCatalog')
                    DROP FULLTEXT CATALOG KinshoutSearchCatalog
                """);
        }
    }
}
