using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Steps.Execution;

/// <summary>How the steps of a group are executed (DECISIONS X3/X4).</summary>
public enum IntegrationMode
{
    /// <summary>Simulated fake world, no real system.</summary>
    Fake,

    /// <summary>Real scripts with <c>dryRun = true</c>: check state, report planned actions, change nothing.</summary>
    DryRun,

    /// <summary>Real scripts that change systems.</summary>
    Real,
}

/// <summary>Step groups that can be switched independently (phase 4a: OnPrem, 4b: Cloud).</summary>
public static class StepGroups
{
    public const string OnPrem = "OnPrem";
    public const string Cloud = "Cloud";

    public static IReadOnlySet<string> OnPremKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AdCreateUser, AdGroups, HomeFolder, HomeShare, LogonScript, SyncDelta, AdEnable, SyncDeltaAfterEnable,
    };

    public static string GroupOf(string stepKey) => OnPremKeys.Contains(stepKey) ? OnPrem : Cloud;
}
