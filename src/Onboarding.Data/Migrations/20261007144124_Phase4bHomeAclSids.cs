using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bHomeAclSids : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replace the previous default ACEs (localized names fail on German servers) by well-known
            // SIDs plus CC-Management-Lead, but only while the list is still exactly the old default:
            // an administered list is never overwritten. Version++ for optimistic concurrency.
            migrationBuilder.Sql(
                """
                UPDATE GlobalConfig
                SET Settings = json_set(Settings, '$.Home.AdditionalAces', json('[{"Principal":"S-1-5-18","Right":"FullControl"},{"Principal":"S-1-5-32-544","Right":"FullControl"},{"Principal":"CC\\CC-Management-Lead","Right":"FullControl"}]')), Version = Version + 1
                WHERE json(json_extract(Settings, '$.Home.AdditionalAces')) = json('[{"Principal":"SYSTEM","Right":"FullControl"},{"Principal":"BUILTIN\\Administrators","Right":"FullControl"}]');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE GlobalConfig
                SET Settings = json_set(Settings, '$.Home.AdditionalAces', json('[{"Principal":"SYSTEM","Right":"FullControl"},{"Principal":"BUILTIN\\Administrators","Right":"FullControl"}]')), Version = Version + 1
                WHERE json(json_extract(Settings, '$.Home.AdditionalAces')) = json('[{"Principal":"S-1-5-18","Right":"FullControl"},{"Principal":"S-1-5-32-544","Right":"FullControl"},{"Principal":"CC\\CC-Management-Lead","Right":"FullControl"}]');
                """);
        }
    }
}
