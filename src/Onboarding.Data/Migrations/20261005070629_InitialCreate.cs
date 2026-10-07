using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Areas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OuCanonical = table.Column<string>(type: "TEXT", nullable: false),
                    OuDistinguishedName = table.Column<string>(type: "TEXT", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Areas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StepKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScopeRefId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Mandatory = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChangeType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OldJson = table.Column<string>(type: "TEXT", nullable: true),
                    NewJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    AdGroups = table.Column<string>(type: "TEXT", nullable: false),
                    Licenses = table.Column<string>(type: "TEXT", nullable: false),
                    SharedMailboxes = table.Column<string>(type: "TEXT", nullable: false),
                    LogonScript = table.Column<string>(type: "TEXT", nullable: false),
                    Teams = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IdentityOverride = table.Column<string>(type: "TEXT", nullable: true),
                    Derived = table.Column<string>(type: "TEXT", nullable: true),
                    NeedsInputReason = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    EncryptedInitialPassword = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Input_AreaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Input_DepartmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Input_EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Input_Extension = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Input_FirstName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Input_HasDoctorTitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    Input_LastName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Input_Mail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Input_ManagerObjectGuid = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceTemplateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Mandatory = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CompletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistItems_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StepKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestSteps_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Areas",
                columns: new[] { "Id", "Active", "Company", "Name", "OuCanonical", "OuDistinguishedName", "SortOrder", "Version" },
                values: new object[,]
                {
                    { new Guid("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0001"), true, "CleanControlling GmbH", "TecSa", "CC.local/CleanControlling/Technical/Users", "OU=Users,OU=Technical,OU=CleanControlling,DC=CC,DC=local", 1, 0L },
                    { new Guid("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0002"), true, "CleanControlling Medical GmbH & Co. KG", "Bio", "CC.local/CleanControlling/Medical/Biology/Users", "OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=CC,DC=local", 2, 0L },
                    { new Guid("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0003"), true, "CleanControlling Medical GmbH & Co. KG", "Chemie", "CC.local/CleanControlling/Medical/Chemical/Users", "OU=Users,OU=Chemical,OU=Medical,OU=CleanControlling,DC=CC,DC=local", 3, 0L }
                });

            migrationBuilder.InsertData(
                table: "ChecklistTemplates",
                columns: new[] { "Id", "Active", "Description", "Mandatory", "Scope", "ScopeRefId", "SortOrder", "Title", "Type", "Version" },
                values: new object[,]
                {
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000101"), true, "", true, "Global", null, 10, "SwissSign-Zertifikat (S/MIME) erstellen", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000102"), true, "", true, "Global", null, 20, "ILIAS-Konto anlegen", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000103"), true, "", true, "Global", null, 30, "Mail an HR", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000104"), true, "", true, "Global", null, 40, "Einladung EDV-Einführung", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000105"), true, "", true, "Global", null, 50, "MFA einrichten (1Password)", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000106"), true, "", true, "Global", null, 60, "In Multifunktionsdrucker eintragen", "Onboarding", 0L },
                    { new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000107"), true, "", true, "Global", null, 70, "Rechnerarbeitsplatz einrichten", "Onboarding", 0L }
                });

            migrationBuilder.InsertData(
                table: "GlobalConfig",
                columns: new[] { "Id", "Settings", "Version" },
                values: new object[] { 1, "{\"DomainFqdn\":\"CC.local\",\"DomainNetBios\":\"\",\"UpnSuffix\":\"cleancontrolling.de\",\"MailPattern\":\"{firstInitial}.{lastName}@cleancontrolling.de\",\"ProxyAddressTemplates\":[\"SMTP:{mailLocal}@cleancontrolling.de\",\"smtp:{mailLocal}@cleancontrolling.com\"],\"DoctorTitle\":{\"Attribute\":\"extensionAttribute1\",\"Value\":\"Dr.\"},\"PhonePrefixE164\":\"\\u002B497465929678\",\"PhoneDisplayFormat\":\"\\u002B49 7465 929678-{DW}\",\"Home\":{\"Server\":\"DC01\",\"LocalRoot\":\"F:\\\\Home\",\"ShareNamePattern\":\"{sam}$\",\"UncPattern\":\"\\\\\\\\dc01\\\\{sam}$\"},\"LogonScript\":{\"Path\":\"\\\\\\\\dc03\\\\NETLOGON\",\"FileNamePattern\":\"{sam}.bat\"},\"EntraConnectServer\":\"CC01\",\"UsageLocation\":\"DE\",\"LicenseMode\":\"Direct\",\"OneWinNativeOutlookEnabled\":false,\"Teams\":{\"VoiceRoutingPolicy\":\"INTStandard\",\"VoicemailPolicy\":\"CleanControlling Personal Voicemail\",\"VoicemailPromptLanguage\":\"de-DE\",\"PhoneNumberType\":\"DirectRouting\"},\"DisabledUsersOU\":\"\",\"TimeZone\":\"Europe/Berlin\",\"EnableLeadTime\":\"00:00:00\",\"PasswordCertThumbprint\":\"\",\"Execution\":{\"PollInterval\":\"00:01:00\",\"BackoffSchedule\":[\"00:01:00\",\"00:02:00\",\"00:05:00\",\"00:10:00\",\"00:15:00\"],\"StepTimeout\":\"1.00:00:00\"}}", 0L });

            migrationBuilder.CreateIndex(
                name: "IX_Areas_Name",
                table: "Areas",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_RequestId",
                table: "AuditLog",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Timestamp",
                table: "AuditLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistItems_RequestId",
                table: "ChecklistItems",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigHistory_EntityType_EntityId",
                table: "ConfigHistory",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Status",
                table: "Requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_RequestId_StepKey",
                table: "RequestSteps",
                columns: new[] { "RequestId", "StepKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_Status_NextAttemptAt",
                table: "RequestSteps",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Areas");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "ChecklistItems");

            migrationBuilder.DropTable(
                name: "ChecklistTemplates");

            migrationBuilder.DropTable(
                name: "ConfigHistory");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "GlobalConfig");

            migrationBuilder.DropTable(
                name: "RequestSteps");

            migrationBuilder.DropTable(
                name: "Requests");
        }
    }
}
