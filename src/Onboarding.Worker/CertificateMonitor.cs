using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Operations;
using Onboarding.Data;
using Onboarding.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Security;

namespace Onboarding.Worker;

/// <summary>
/// Checks the expiry of the worker's certificates at start and then daily and stores the result
/// for the web banner (SPEC §9, DECISIONS X15). The certificates are only read, never exported.
/// </summary>
public sealed class CertificateMonitor(
    IDbContextFactory<OnboardingDbContext> dbFactory,
    ICertificateInfoSource certificates,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<CertificateMonitor> logger) : BackgroundService
{
    private static readonly string[] WorkerCertificates = [CertificateNames.PasswordCertificate, CertificateNames.CloudApp];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Worker:CertificateCheckInterval", TimeSpan.FromDays(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.CertificateCheckFailed(ex);
            }

            await Task.Delay(interval, timeProvider, stoppingToken);
        }
    }

    /// <summary>Upserts the status of every configured worker certificate; drops unconfigured ones.</summary>
    public async Task CheckOnceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(configuration["SecretProtection:Mode"], "DevelopmentPem", StringComparison.OrdinalIgnoreCase))
        {
            targets[CertificateNames.PasswordCertificate] = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings.PasswordCertThumbprint;
        }

        if (IntegrationRegistration.ReadMode(configuration, StepGroups.Cloud) != IntegrationMode.Fake)
        {
            targets[CertificateNames.CloudApp] = configuration["Integrations:Cloud:CertificateThumbprint"] ?? "";
        }

        var now = timeProvider.GetUtcNow();
        var existing = await db.CertificateStatuses.Where(c => WorkerCertificates.Contains(c.Name)).ToListAsync(ct);
        db.CertificateStatuses.RemoveRange(existing.Where(e => !targets.ContainsKey(e.Name)));
        foreach (var (name, thumbprint) in targets)
        {
            var info = certificates.Find(thumbprint);
            var row = existing.FirstOrDefault(e => e.Name == name);
            if (row is null)
            {
                row = new CertificateStatus { Name = name };
                db.CertificateStatuses.Add(row);
            }

            row.Thumbprint = thumbprint;
            row.Found = info is not null;
            row.NotAfter = info?.NotAfter;
            row.CheckedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
