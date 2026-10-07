using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;

namespace Onboarding.Data;

/// <summary>Identity values reserved by requests that are not closed (DECISIONS K1).</summary>
public sealed class OpenRequestLookup(OnboardingDbContext db) : IOpenRequestLookup
{
    public async Task<IReadOnlyList<OpenRequestIdentity>> GetOpenRequestIdentitiesAsync(
        Guid excludeRequestId,
        CancellationToken cancellationToken)
    {
        // Derived is a JSON column; the number of open requests is small, so filter in memory.
        var rows = await db.Requests
            .IgnoreAutoIncludes()
            .AsNoTracking()
            .Where(r => r.Id != excludeRequestId &&
                        r.Status != RequestStatus.Completed &&
                        r.Status != RequestStatus.Cancelled)
            .Select(r => new { r.Id, r.Derived })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Where(r => r.Derived is not null)
            .Select(r => new OpenRequestIdentity(
                r.Id,
                r.Derived!.SamAccountName,
                r.Derived.Mail,
                r.Derived.UserPrincipalName,
                r.Derived.ProxyAddresses,
                r.Derived.PhoneE164))
            .ToList();
    }
}
