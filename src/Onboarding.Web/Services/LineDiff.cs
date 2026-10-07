namespace Onboarding.Web.Services;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>Line diff (LCS) for small texts such as logon scripts.</summary>
public static class LineDiff
{
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var a = Split(oldText);
        var b = Split(newText);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        result.AddRange(a.Skip(x).Select(l => new DiffLine(DiffKind.Removed, l)));
        result.AddRange(b.Skip(y).Select(l => new DiffLine(DiffKind.Added, l)));
        return result;
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
