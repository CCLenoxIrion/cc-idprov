namespace Onboarding.Core.Configuration;

/// <summary>NTFS right on a home folder (DECISIONS X17).</summary>
public enum HomeRight
{
    Modify,
    FullControl,
}

/// <summary>Additional ACE on every home folder, e.g. <c>CC\CC-Management = Modify</c>.</summary>
public sealed class HomeAce
{
    /// <summary>Account or group exactly as listed in the endpoint allowlist on the file server (or a SID).</summary>
    public string Principal { get; set; } = "";

    public HomeRight Right { get; set; } = HomeRight.Modify;
}

/// <summary>Effective home folder rights of a request: user right plus merged additional ACEs.</summary>
public sealed record HomeAclSettings(HomeRight UserRight, IReadOnlyList<HomeAce> AdditionalAces);

public static class HomeAcl
{
    public const int MaxPrincipalLength = 256;

    /// <summary>
    /// Global ACEs plus the department's; for the same principal (case-insensitive) the
    /// department entry wins. Order: global entries, then department-only entries.
    /// </summary>
    public static HomeAclSettings Resolve(GlobalConfig global, DepartmentConfig department)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(department);
        var byDepartment = department.HomeAdditionalAces
            .GroupBy(a => a.Principal.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<HomeAce>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ace in global.Home.AdditionalAces.Concat(department.HomeAdditionalAces))
        {
            var principal = ace.Principal.Trim();
            if (!seen.Add(principal))
            {
                continue;
            }

            var effective = byDepartment.TryGetValue(principal, out var own) ? own : ace;
            merged.Add(new HomeAce { Principal = principal, Right = effective.Right });
        }

        return new HomeAclSettings(global.Home.UserRight, merged);
    }

    /// <summary>Validation messages for an ACE list (empty or malformed principal, duplicates).</summary>
    public static IEnumerable<string> Validate(IEnumerable<HomeAce> aces, string label)
    {
        ArgumentNullException.ThrowIfNull(aces);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ace in aces)
        {
            var principal = ace.Principal.Trim();
            if (principal.Length == 0)
            {
                yield return $"{label}: Principal fehlt.";
            }
            else if (principal.Length > MaxPrincipalLength || principal.Any(c => c == '=' || char.IsControl(c)))
            {
                yield return $"{label}: Principal '{principal}' ist ungültig (kein '=', keine Steuerzeichen, max. {MaxPrincipalLength} Zeichen).";
            }
            else if (!seen.Add(principal))
            {
                yield return $"{label}: Principal '{principal}' ist doppelt.";
            }

            if (!Enum.IsDefined(ace.Right))
            {
                yield return $"{label}: Recht für '{principal}' ist ungültig.";
            }
        }
    }
}
