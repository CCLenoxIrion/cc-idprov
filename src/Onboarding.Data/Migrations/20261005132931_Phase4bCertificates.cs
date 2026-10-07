using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificateStatus",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Found = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateStatus", x => x.Name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateStatus");
        }
    }
}
