using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForceRequested",
                table: "RequestSteps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "WaitingSince",
                table: "RequestSteps",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForceRequested",
                table: "RequestSteps");

            migrationBuilder.DropColumn(
                name: "WaitingSince",
                table: "RequestSteps");
        }
    }
}
