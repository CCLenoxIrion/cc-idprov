using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Onboarding.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DirectoryObjectGuid",
                table: "Requests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Responsible",
                table: "ChecklistTemplates",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "IT"); // DECISIONS C1: existing rows are IT

            migrationBuilder.AddColumn<string>(
                name: "Responsible",
                table: "ChecklistItems",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "IT"); // DECISIONS C1: existing rows are IT

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000101"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000102"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000103"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000104"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000105"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000106"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "ChecklistTemplates",
                keyColumn: "Id",
                keyValue: new Guid("8d1e2b3c-4f5a-4b6c-8d7e-000000000107"),
                column: "Responsible",
                value: "IT");

            migrationBuilder.UpdateData(
                table: "GlobalConfig",
                keyColumn: "Id",
                keyValue: 1,
                column: "Settings",
                value: "{\"DomainFqdn\":\"CC.local\",\"DomainNetBios\":\"\",\"UpnSuffix\":\"cleancontrolling.de\",\"MailPattern\":\"{firstInitial}.{lastName}@cleancontrolling.de\",\"ProxyAddressTemplates\":[\"SMTP:{mailLocal}@cleancontrolling.de\",\"smtp:{mailLocal}@cleancontrolling.com\"],\"DoctorTitle\":{\"Attribute\":\"extensionAttribute1\",\"Value\":\"Dr.\"},\"PhonePrefixE164\":\"\\u002B497465929678\",\"PhoneDisplayFormat\":\"\\u002B49 7465 929678-{DW}\",\"Home\":{\"Server\":\"DC01\",\"LocalRoot\":\"F:\\\\Home\",\"ShareNamePattern\":\"{sam}$\",\"UncPattern\":\"\\\\\\\\dc01\\\\{sam}$\"},\"LogonScript\":{\"Path\":\"\\\\\\\\dc03\\\\NETLOGON\",\"FileNamePattern\":\"{sam}.bat\"},\"EntraConnectServer\":\"CC01\",\"UsageLocation\":\"DE\",\"LicenseMode\":\"Direct\",\"OneWinNativeOutlookEnabled\":false,\"Teams\":{\"VoiceRoutingPolicy\":\"INTStandard\",\"VoicemailPolicy\":\"CleanControlling Personal Voicemail\",\"VoicemailPromptLanguage\":\"de-DE\",\"PhoneNumberType\":\"DirectRouting\"},\"DisabledUsersOU\":\"\",\"TimeZone\":\"Europe/Berlin\",\"EnableLeadTime\":\"00:00:00\",\"RequestIdAttribute\":\"extensionAttribute15\",\"PasswordCertThumbprint\":\"\",\"Execution\":{\"PollInterval\":\"00:01:00\",\"BackoffSchedule\":[\"00:01:00\",\"00:02:00\",\"00:05:00\",\"00:10:00\",\"00:15:00\"],\"StepTimeout\":\"1.00:00:00\"}}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectoryObjectGuid",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "Responsible",
                table: "ChecklistTemplates");

            migrationBuilder.DropColumn(
                name: "Responsible",
                table: "ChecklistItems");

            migrationBuilder.UpdateData(
                table: "GlobalConfig",
                keyColumn: "Id",
                keyValue: 1,
                column: "Settings",
                value: "{\"DomainFqdn\":\"CC.local\",\"DomainNetBios\":\"\",\"UpnSuffix\":\"cleancontrolling.de\",\"MailPattern\":\"{firstInitial}.{lastName}@cleancontrolling.de\",\"ProxyAddressTemplates\":[\"SMTP:{mailLocal}@cleancontrolling.de\",\"smtp:{mailLocal}@cleancontrolling.com\"],\"DoctorTitle\":{\"Attribute\":\"extensionAttribute1\",\"Value\":\"Dr.\"},\"PhonePrefixE164\":\"\\u002B497465929678\",\"PhoneDisplayFormat\":\"\\u002B49 7465 929678-{DW}\",\"Home\":{\"Server\":\"DC01\",\"LocalRoot\":\"F:\\\\Home\",\"ShareNamePattern\":\"{sam}$\",\"UncPattern\":\"\\\\\\\\dc01\\\\{sam}$\"},\"LogonScript\":{\"Path\":\"\\\\\\\\dc03\\\\NETLOGON\",\"FileNamePattern\":\"{sam}.bat\"},\"EntraConnectServer\":\"CC01\",\"UsageLocation\":\"DE\",\"LicenseMode\":\"Direct\",\"OneWinNativeOutlookEnabled\":false,\"Teams\":{\"VoiceRoutingPolicy\":\"INTStandard\",\"VoicemailPolicy\":\"CleanControlling Personal Voicemail\",\"VoicemailPromptLanguage\":\"de-DE\",\"PhoneNumberType\":\"DirectRouting\"},\"DisabledUsersOU\":\"\",\"TimeZone\":\"Europe/Berlin\",\"EnableLeadTime\":\"00:00:00\",\"PasswordCertThumbprint\":\"\",\"Execution\":{\"PollInterval\":\"00:01:00\",\"BackoffSchedule\":[\"00:01:00\",\"00:02:00\",\"00:05:00\",\"00:10:00\",\"00:15:00\"],\"StepTimeout\":\"1.00:00:00\"}}");
        }
    }
}
