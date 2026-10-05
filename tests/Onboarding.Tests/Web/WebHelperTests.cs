using System.Text;
using Bunit;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.LogonScripts;
using Onboarding.Web.Components.Pages.Admin;
using Onboarding.Web.Services;

namespace Onboarding.Tests.Web;

public sealed class WebHelperTests
{
    [Fact]
    public void Audit_csv_escapes_and_neutralizes_formulas()
    {
        var entries = new[]
        {
            AuditEntry.Create(DateTimeOffset.UnixEpoch, "=cmd|' /C calc'!A0", "Test", "Success", details: "a;b \"c\"\nd"),
        };

        var text = Encoding.UTF8.GetString(AuditCsv.Write(entries)).TrimStart('\uFEFF');
        var line = text.Split("\r\n")[1];

        Assert.StartsWith("1970-01-01 00:00:00;'=cmd", line, StringComparison.Ordinal);
        Assert.EndsWith("\"a;b \"\"c\"\"\nd\"", text.TrimEnd('\r', '\n'), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_diff_marks_added_and_removed_lines()
    {
        var diff = LineDiff.Compute("a\r\nb\r\nc\r\n", "a\r\nc\r\nd\r\n");

        Assert.Equal(
            [new DiffLine(DiffKind.Same, "a"), new DiffLine(DiffKind.Removed, "b"), new DiffLine(DiffKind.Same, "c"), new DiffLine(DiffKind.Added, "d")],
            diff);
    }

    [Fact]
    public void Logon_editor_preview_equals_generated_file() // AK 3
    {
        using var ctx = new BunitContext();
        var template = new LogonScriptTemplate
        {
            DisconnectDrives = ["S", "H"],
            ConnectDrives = [new DriveMapping { Letter = "H", Unc = "{homeUnc}" }],
        };

        var cut = ctx.Render<LogonScriptEditor>(p => p
            .Add(c => c.Template, template)
            .Add(c => c.HomeUncPattern, @"\\dc01\{sam}$")
            .Add(c => c.DepartmentName, "Vertrieb"));

        var bytes = LogonScriptGenerator.Generate(template, new LogonScriptValues("mmustermann", @"\\dc01\mmustermann$", "Vertrieb"));
        // The HTML parser normalizes CRLF; byte identity is shown via the SHA-256 of the exact bytes.
        Assert.Equal(LogonScriptGenerator.ToText(bytes).Replace("\r\n", "\n", StringComparison.Ordinal), cut.Find("[data-testid=logon-preview]").TextContent);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), cut.Find("[data-testid=logon-hash]").TextContent);
    }

    [Fact]
    public void Logon_editor_shows_error_for_non_ascii()
    {
        using var ctx = new BunitContext();
        var template = new LogonScriptTemplate { ExtraLines = ["rem Büro"] };

        var cut = ctx.Render<LogonScriptEditor>(p => p
            .Add(c => c.Template, template)
            .Add(c => c.HomeUncPattern, @"\\dc01\{sam}$")
            .Add(c => c.DepartmentName, "Vertrieb"));

        Assert.Contains("Nicht-ASCII", cut.Find(".alert-danger").TextContent, StringComparison.Ordinal);
    }
}
