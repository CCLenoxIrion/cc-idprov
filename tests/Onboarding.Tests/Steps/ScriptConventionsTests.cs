using System.Text.RegularExpressions;

namespace Onboarding.Tests.Steps;

/// <summary>
/// Static checks of the PowerShell files (no pwsh needed). Only checks without false positives:
/// BOM, Join-Path with more than two path parts, a few .NET-Core/PS7-only identifiers and
/// <c>#Requires -Version 7</c> in what runs under Windows PowerShell 5.1. Operators such as
/// <c>&amp;&amp;</c>, <c>||</c>, <c>??</c> or the ternary are not reliably detectable by regex
/// (patterns, here-strings) – the 5.1 Pester run covers them (DEPLOYMENT.md §9).
/// </summary>
public sealed partial class ScriptConventionsTests
{
    private static readonly string[] PowerShellExtensions = [".ps1", ".psm1", ".psd1", ".psrc", ".template"];

    private static string ScriptsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Onboarding.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "scripts");
        }
    }

    public static TheoryData<string> AllPowerShellFiles() => Files(Directory.EnumerateFiles(ScriptsDirectory, "*", SearchOption.AllDirectories));

    /// <summary>Files that run under Windows PowerShell 5.1: the JEA endpoints and their tests.</summary>
    public static TheoryData<string> WindowsPowerShellFiles() => Files(
        Directory.EnumerateFiles(Path.Combine(ScriptsDirectory, "jea"), "*", SearchOption.AllDirectories)
            .Append(Path.Combine(ScriptsDirectory, "tests", "JeaEndpoint.Tests.ps1"))
            .Append(Path.Combine(ScriptsDirectory, "tests", "Stubs.ps1")));

    [Theory]
    [MemberData(nameof(AllPowerShellFiles))]
    public void Starts_with_utf8_bom(string relativePath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ScriptsDirectory, relativePath));
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], $"{relativePath}: UTF-8 ohne BOM – Windows PowerShell 5.1 liest das als ANSI.");
    }

    [Theory]
    [MemberData(nameof(WindowsPowerShellFiles))]
    public void Is_compatible_with_windows_powershell_5_1(string relativePath)
    {
        var violations = Violations(File.ReadAllLines(Path.Combine(ScriptsDirectory, relativePath)));
        Assert.True(violations.Count == 0, $"{relativePath}:\n{string.Join('\n', violations)}");
    }

    [Theory]
    [InlineData("Import-Module (Join-Path $PSScriptRoot '..' 'jea' 'X.psd1')", true)]
    [InlineData("$p = Join-Path $root $child $more", true)]
    [InlineData("$h = [Convert]::ToHexString($bytes)", true)]
    [InlineData("$d = [System.Security.Cryptography.SHA256]::HashData($bytes)", true)]
    [InlineData("$j | ConvertFrom-Json -AsHashtable", true)]
    [InlineData("#Requires -Version 7.2", true)]
    [InlineData("$p = Join-Path $PSScriptRoot 'OnboardingEndpoint.psd1'", false)]
    [InlineData("$p = Join-Path (Join-Path $PSScriptRoot '..') 'jea'", false)]
    [InlineData("$p = Join-Path $root 'x' -Resolve", false)]
    [InlineData("if ($a -match '&&|\\|\\|') { $x = $b ?? 'c' }", false)]
    [InlineData("# Join-Path a b c im Kommentar", false)]
    [InlineData("$sha.ComputeHash($bytes)", false)]
    public void Detector_flags_only_clear_violations(string line, bool expected) =>
        Assert.Equal(expected, Violations([line]).Count > 0);

    private static List<string> Violations(IEnumerable<string> lines)
    {
        var violations = new List<string>();
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (RequiresPs7().IsMatch(line))
            {
                violations.Add($"{number}: #Requires -Version 7");
                continue;
            }

            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (JoinPathWithThreeParts().IsMatch(line))
            {
                violations.Add($"{number}: Join-Path mit mehr als zwei Pfadteilen (gibt es erst ab PowerShell 6)");
            }

            if (Ps7OnlyIdentifier().Match(line) is { Success: true } match)
            {
                violations.Add($"{number}: {match.Value} gibt es unter Windows PowerShell 5.1 nicht");
            }
        }

        return violations;
    }

    private static TheoryData<string> Files(IEnumerable<string> paths)
    {
        var data = new TheoryData<string>();
        foreach (var path in paths.Where(p => PowerShellExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(ScriptsDirectory, path));
        }

        return data;
    }

    /// <summary>Join-Path followed by three plain arguments (variables or quoted literals).</summary>
    [GeneratedRegex("""Join-Path\s+(?:-Path\s+)?(?:\$[\w:]+|'[^']*'|"[^"]*")\s+(?:\$[\w:]+|'[^']*'|"[^"]*")\s+(?:\$[\w:]+|'[^']*'|"[^"]*")""")]
    private static partial Regex JoinPathWithThreeParts();

    [GeneratedRegex(@"(-AsHashtable\b|\bToHexString\b|\bHashData\b|\bGetRelativePath\b)")]
    private static partial Regex Ps7OnlyIdentifier();

    [GeneratedRegex(@"^\s*#Requires\s+-Version\s+[6-9]", RegexOptions.IgnoreCase)]
    private static partial Regex RequiresPs7();
}
