using Onboarding.Core.Configuration;

namespace Onboarding.Core.Naming;

/// <summary>Non-blocking hints for the request form (warnings, never errors).</summary>
public static class InputWarnings
{
    /// <summary>Compares the extension with the department's <see cref="DepartmentTeamsConfig.PhoneExpected"/> (DECISIONS T1).</summary>
    public static string? Phone(DepartmentConfig department, string? extension)
    {
        ArgumentNullException.ThrowIfNull(department);
        var hasExtension = !string.IsNullOrWhiteSpace(extension);
        return (department.Teams.PhoneExpected, hasExtension) switch
        {
            (true, false) => $"Abteilung {department.Name} hat normalerweise Telefonie, aber es ist keine Durchwahl angegeben.",
            (false, true) => $"Abteilung {department.Name} hat normalerweise keine Telefonie.",
            _ => null,
        };
    }
}
