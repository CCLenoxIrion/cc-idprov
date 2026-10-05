using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Steps.Fakes;
using Onboarding.Steps.Security;

namespace Onboarding.Steps;

public static class IntegrationRegistration
{
    /// <summary>
    /// Registers directory, license and password-policy integrations. Only <c>Fake</c> exists
    /// until phase 4 (CLAUDE.md).
    /// </summary>
    public static IServiceCollection AddOnboardingIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Integrations");
        var mode = section["Mode"] ?? "Fake";
        if (!string.Equals(mode, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Integrations:Mode '{mode}' is not available before phase 4. Use 'Fake'.");
        }

        var dataFile = section["FakeDataFile"] ?? throw new InvalidOperationException("Integrations:FakeDataFile is not configured.");
        services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new FakeDirectory(FakeDirectoryData.Load(Path.Combine(env.ContentRootPath, dataFile)));
        });
        services.AddSingleton<IDirectoryLookup>(sp => sp.GetRequiredService<FakeDirectory>());
        services.AddSingleton<IDirectoryBrowser>(sp => sp.GetRequiredService<FakeDirectory>());
        services.AddSingleton<IPasswordPolicyProvider>(sp => sp.GetRequiredService<FakeDirectory>());
        services.AddSingleton<ILicenseOverview>(sp => sp.GetRequiredService<FakeDirectory>());
        return services;
    }

    /// <summary>
    /// Registers the initial-password encryption (DECISIONS P1). <c>SecretProtection:Mode</c> =
    /// <c>Certificate</c> (thumbprint from the global configuration) or <c>DevelopmentPem</c>
    /// (development only).
    /// </summary>
    public static IServiceCollection AddSecretProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment,
        Func<IServiceProvider, string> thumbprint)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("SecretProtection");
        var mode = section["Mode"] ?? "Certificate";
        if (string.Equals(mode, "DevelopmentPem", StringComparison.OrdinalIgnoreCase))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("SecretProtection:Mode 'DevelopmentPem' is only allowed in Development.");
            }

            var path = section["DevelopmentKeyPath"] ?? throw new InvalidOperationException("SecretProtection:DevelopmentKeyPath is not configured.");
            services.AddSingleton<IRsaKeySource>(new DevelopmentPemKeySource(path));
        }
        else
        {
            services.AddScoped<IRsaKeySource>(sp => new CertificateStoreKeySource(() => thumbprint(sp)));
        }

        services.AddScoped<HybridSecretProtector>();
        services.AddScoped<ISecretEncryptor>(sp => sp.GetRequiredService<HybridSecretProtector>());
        return services;
    }
}
