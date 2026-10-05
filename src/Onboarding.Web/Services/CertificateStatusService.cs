using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Operations;
using Onboarding.Core.Time;
using Onboarding.Data;
using Onboarding.Steps.Security;

namespace Onboarding.Web.Services;

/// <summary>
/// Certificate warnings for ITAdmins (SPEC §9, DECISIONS X15): worker certificates from the
/// status table, the web's own Graph read certificate checked directly.
/// </summary>
public sealed class CertificateStatusService(
    IDbContextFactory<OnboardingDbContext> dbFactory,
    ICertificateInfoSource certificates,
    IConfiguration configuration,
    TimeProvider timeProvider)
{
    /// <summary>A worker check older than this is reported (the worker checks daily).</summary>
    public static readonly TimeSpan MaxCheckAge = TimeSpan.FromHours(48);

    public async Task<IReadOnlyList<CertificateWarning>> GetWarningsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var statuses = await db.CertificateStatuses.AsNoTracking().ToListAsync(ct);
        var now = timeProvider.GetUtcNow();
        if (string.Equals(configuration["Integrations:Read:Graph"], "Real", StringComparison.OrdinalIgnoreCase))
        {
            var thumbprint = configuration["Integrations:Read:GraphAuth:CertificateThumbprint"] ?? "";
            var info = certificates.Find(thumbprint);
            statuses.Add(new CertificateStatus
            {
                Name = CertificateNames.GraphRead,
                Thumbprint = thumbprint,
                Found = info is not null,
                NotAfter = info?.NotAfter,
                CheckedAt = now,
            });
        }

        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var warningDays = configuration.GetValue("Web:CertificateWarningDays", 30);
        return CertificateWarnings.Evaluate(statuses, now, warningDays, MaxCheckAge, BusinessCalendar.Resolve(global.TimeZone));
    }
}
