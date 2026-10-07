using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bHomeAclLogonServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HomeAdditionalAces",
                table: "Departments",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            // Seed values only where the key is missing: the administered global configuration is
            // not overwritten (the whole seed row is not rewritten). Version++ for optimistic concurrency.
            migrationBuilder.Sql(
                """
                UPDATE GlobalConfig
                SET Settings = json_set(Settings, '$.LogonScript.Server', 'DC03'), Version = Version + 1
                WHERE json_extract(Settings, '$.LogonScript.Server') IS NULL;
                """);
            migrationBuilder.Sql(
                """
                UPDATE GlobalConfig
                SET Settings = json_set(Settings, '$.Home.UserRight', 'Modify'), Version = Version + 1
                WHERE json_extract(Settings, '$.Home.UserRight') IS NULL;
                """);
            migrationBuilder.Sql(
                """
                UPDATE GlobalConfig
                SET Settings = json_set(Settings, '$.Home.AdditionalAces',
                        json('[{"Principal":"SYSTEM","Right":"FullControl"},{"Principal":"BUILTIN\\Administrators","Right":"FullControl"}]')),
                    Version = Version + 1
                WHERE json_extract(Settings, '$.Home.AdditionalAces') IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeAdditionalAces",
                table: "Departments");

            // The added configuration keys stay in the global configuration; older code ignores them.
        }
    }
}
