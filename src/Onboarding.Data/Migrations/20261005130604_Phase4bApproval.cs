using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectSid",
                table: "Requests",
                type: "TEXT",
                maxLength: 184,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelfApprovalReason",
                table: "Requests",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelfApproved",
                table: "Requests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            // No UpdateData of the GlobalConfig seed: it would overwrite the administered
            // configuration. A missing ApprovalPolicy deserializes to SelfApprovalWithReason.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectoryObjectSid",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "SelfApprovalReason",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "SelfApproved",
                table: "Requests");
            // No UpdateData of the GlobalConfig seed: it would overwrite the administered
            // configuration. A missing ApprovalPolicy deserializes to SelfApprovalWithReason.
        }
    }
}
