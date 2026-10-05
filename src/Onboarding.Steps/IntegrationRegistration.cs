using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Onboarding.Core.Configuration;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Core.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Fakes;
using Onboarding.Steps.Fakes.World;
using Onboarding.Steps.Graph;
using Onboarding.Steps.Ldap;
using Onboarding.Steps.Scripts;
using Onboarding.Steps.Security;

namespace Onboarding.Steps;

public static class IntegrationRegistration
{
    /// <summary>
    /// Registers the read-only integrations of the web (DECISIONS X6/X16):
    /// <c>Integrations:Read:Directory</c> = Fake | Real (AD over LDAP: collisions, manager
    /// search, OUs, password policy) and <c>Integrations:Read:Graph</c> = Fake | Real (licenses,
    /// cloud-only Entra objects in the collision check, own read-only app registration).
    /// <paramref name="globalConfig"/> supplies domain and request-id attribute.
    /// </summary>
    public static IServiceCollection AddOnboardingIntegrations(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, CancellationToken, Task<GlobalConfig>> globalConfig)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(globalConfig);
        if (configuration["Integrations:Mode"] is { } legacy)
        {
            throw new InvalidOperationException(
                $"Integrations:Mode '{legacy}' is no longer supported. Use Integrations:Read:Directory and Integrations:Read:Graph.");
        }

        if (configuration["Integrations:Read:Licenses"] is { } licenses)
        {
            throw new InvalidOperationException(
                $"Integrations:Read:Licenses '{licenses}' is no longer supported. Use Integrations:Read:Graph (licenses and Entra lookups).");
        }

        var directory = ReadReadMode(configuration, "Directory");
        var graph = ReadReadMode(configuration, "Graph");
        GraphReadOptions? graphOptions = null;
        if (graph == IntegrationMode.Real)
        {
            graphOptions = configuration.GetSection("Integrations:Read:GraphAuth").Get<GraphReadOptions>() ?? new GraphReadOptions();
            graphOptions.Validate();
        }

        var dataFile = configuration["Integrations:FakeDataFile"];
        if ((directory == IntegrationMode.Fake || graph == IntegrationMode.Fake) && string.IsNullOrWhiteSpace(dataFile))
        {
            throw new InvalidOperationException("Integrations:FakeDataFile is not configured.");
        }

        services.AddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new FakeDirectory(FakeDirectoryData.Load(Path.Combine(env.ContentRootPath, dataFile!)));
        });

        if (graphOptions is not null)
        {
            services.AddSingleton(graphOptions);
            services.AddSingleton<IGraphTokenSource, MsalGraphTokenSource>();
            services.AddSingleton(sp => new GraphReadClient(
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
                sp.GetRequiredService<IGraphTokenSource>(),
                graphOptions));
            services.AddSingleton<ILicenseOverview, GraphLicenseOverview>();
            services.AddSingleton<EntraDirectoryLookup>();
        }
        else
        {
            services.AddSingleton<ILicenseOverview>(sp => sp.GetRequiredService<FakeDirectory>());
        }

        if (directory == IntegrationMode.Fake)
        {
            services.AddSingleton<IDirectoryBrowser>(sp => sp.GetRequiredService<FakeDirectory>());
            services.AddSingleton<IPasswordPolicyProvider>(sp => sp.GetRequiredService<FakeDirectory>());
            services.AddScoped<IDirectoryLookup>(sp => WithEntra(sp, sp.GetRequiredService<FakeDirectory>(), graphOptions is not null));
            return services;
        }

        var ldapOptions = configuration.GetSection("Integrations:Read:Ldap").Get<LdapOptions>() ?? new LdapOptions();
        services.AddSingleton(ldapOptions);
        services.AddSingleton<ILdapSearcher, LdapConnectionSearcher>();
        services.AddScoped(sp => new LdapDirectory(sp.GetRequiredService<ILdapSearcher>(), async ct =>
        {
            var global = await globalConfig(sp, ct).ConfigureAwait(false);
            return new LdapDirectorySettings(
                string.IsNullOrWhiteSpace(ldapOptions.Server) ? global.DomainFqdn : ldapOptions.Server,
                LdapFilter.DomainToBaseDn(global.DomainFqdn),
                global.RequestIdAttribute);
        }));
        services.AddScoped<IDirectoryLookup>(sp => WithEntra(sp, sp.GetRequiredService<LdapDirectory>(), graphOptions is not null));
        services.AddScoped<IDirectoryBrowser>(sp => sp.GetRequiredService<LdapDirectory>());
        services.AddScoped<IPasswordPolicyProvider>(sp => sp.GetRequiredService<LdapDirectory>());
        return services;
    }

    private static IDirectoryLookup WithEntra(IServiceProvider sp, IDirectoryLookup directory, bool entra) =>
        entra ? new CompositeDirectoryLookup(directory, sp.GetRequiredService<EntraDirectoryLookup>()) : directory;

    /// <summary>Read adapters know only Fake and Real; there is nothing to dry-run.</summary>
    public static IntegrationMode ReadReadMode(IConfiguration configuration, string adapter)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"Integrations:Read:{adapter}"] ?? nameof(IntegrationMode.Fake);
        return Enum.TryParse<IntegrationMode>(value, ignoreCase: true, out var mode) && mode is IntegrationMode.Fake or IntegrationMode.Real
            ? mode
            : throw new InvalidOperationException($"Integrations:Read:{adapter} '{value}' is invalid (Fake, Real).");
    }

    /// <summary>
    /// Registers the step executors for the worker. The mode is switched per step group
    /// (<c>Integrations:Steps:OnPrem</c> / <c>:Cloud</c> = Fake | DryRun | Real). Invalid
    /// combinations and settings stop the start with a configuration error.
    /// </summary>
    public static IServiceCollection AddStepExecutors(this IServiceCollection services, IConfiguration configuration, string contentRoot = "")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var onPrem = ReadMode(configuration, StepGroups.OnPrem);
        var cloud = ReadMode(configuration, StepGroups.Cloud);
        if (cloud != IntegrationMode.Fake && onPrem == IntegrationMode.Fake)
        {
            // The fake AD account never reaches Entra; real cloud steps would wait forever.
            throw new InvalidOperationException(
                $"Integrations:Steps:Cloud '{cloud}' requires Integrations:Steps:OnPrem DryRun or Real (a fake AD account never reaches Entra).");
        }

        if (configuration["Integrations:Scripts:DcConfigurationName"] is not null)
        {
            // DC01 is a file server, logon scripts moved to their own endpoint on a DC (DECISIONS X5).
            throw new InvalidOperationException(
                "Integrations:Scripts:DcConfigurationName is no longer supported. Use HomeConfigurationName and LogonConfigurationName.");
        }

        var worldOptions = configuration.GetSection("FakeWorld").Get<FakeWorldOptions>() ?? new FakeWorldOptions();
        worldOptions.DetachedFromOnPrem = onPrem != IntegrationMode.Fake;
        var scriptOptions = configuration.GetSection("Integrations:Scripts").Get<ScriptOptions>() ?? new ScriptOptions();
        CloudOptions? cloudOptions = null;
        if (onPrem != IntegrationMode.Fake)
        {
            if (string.IsNullOrWhiteSpace(scriptOptions.ScriptsDirectory))
            {
                throw new InvalidOperationException("Integrations:Scripts:ScriptsDirectory is not configured.");
            }

            scriptOptions.ScriptsDirectory = Path.GetFullPath(Path.Combine(contentRoot, scriptOptions.ScriptsDirectory));
            scriptOptions.Validate(StepKeys.All);
            var executionTimeout = configuration.GetValue("Worker:ExecutionTimeout", TimeSpan.FromMinutes(5));
            if (executionTimeout <= scriptOptions.MaxTimeout)
            {
                throw new InvalidOperationException(
                    $"Worker:ExecutionTimeout ({executionTimeout}) must be greater than the longest script timeout ({scriptOptions.MaxTimeout}).");
            }
        }

        if (cloud != IntegrationMode.Fake)
        {
            cloudOptions = configuration.GetSection("Integrations:Cloud").Get<CloudOptions>() ?? new CloudOptions();
            cloudOptions.Validate();
        }

        services.AddSingleton(new FakeWorld(worldOptions));
        services.AddSingleton(scriptOptions);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp =>
        {
            var fakes = FakeStepExecutors.Create(sp.GetRequiredService<FakeWorld>());
            var executors = fakes.Select(fake =>
            {
                var mode = StepGroups.GroupOf(fake.StepKey) == StepGroups.OnPrem ? onPrem : cloud;
                return mode == IntegrationMode.Fake
                    ? fake
                    : new ScriptStepExecutor(fake.StepKey, mode, scriptOptions, sp.GetRequiredService<IProcessRunner>(),
                        sp.GetRequiredService<ILogger<ScriptStepExecutor>>(), cloudOptions);
            });
            return new StepExecutorRegistry(executors);
        });
        return services;
    }

    public static IntegrationMode ReadMode(IConfiguration configuration, string group)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"Integrations:Steps:{group}"] ?? nameof(IntegrationMode.Fake);
        return Enum.TryParse<IntegrationMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException($"Integrations:Steps:{group} '{value}' is invalid (Fake, DryRun, Real).");
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
        Func<IServiceProvider, string> thumbprint,
        string contentRoot = "",
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
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
            services.AddSingleton<IRsaKeySource>(new DevelopmentPemKeySource(Path.Combine(contentRoot, path)));
        }
        else
        {
            services.Add(new ServiceDescriptor(typeof(IRsaKeySource), sp => new CertificateStoreKeySource(() => thumbprint(sp)), lifetime));
        }

        services.Add(new ServiceDescriptor(typeof(HybridSecretProtector), typeof(HybridSecretProtector), lifetime));
        services.Add(new ServiceDescriptor(typeof(ISecretEncryptor), sp => sp.GetRequiredService<HybridSecretProtector>(), lifetime));
        services.Add(new ServiceDescriptor(typeof(ISecretDecryptor), sp => sp.GetRequiredService<HybridSecretProtector>(), lifetime));
        return services;
    }
}
