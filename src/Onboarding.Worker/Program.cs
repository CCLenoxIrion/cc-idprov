using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Domain;
using Onboarding.Data;
using Onboarding.Steps;
using Onboarding.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Onboarding.Worker");

var connectionString = OnboardingDataExtensions.ResolveConnectionString(
    builder.Configuration.GetConnectionString("Onboarding")
        ?? throw new InvalidOperationException("ConnectionStrings:Onboarding is not configured."),
    builder.Environment.ContentRootPath);

// DECISIONS B3: one worker per database.
SingleInstanceLock instanceLock;
try
{
    instanceLock = SingleInstanceLock.Acquire(OnboardingDataExtensions.DatabaseFilePath(connectionString));
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync($"Onboarding.Worker wird nicht gestartet: {ex.Message}");
    return 1;
}

using var _ = instanceLock;

var actor = new FixedActorAccessor("worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<OnboardingDbContext>(
    (sp, options) => options.UseOnboardingSqlite(connectionString, actor, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSecretProtection(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    sp =>
    {
        using var db = sp.GetRequiredService<IDbContextFactory<OnboardingDbContext>>().CreateDbContext();
        return db.GlobalConfig.AsNoTracking().Single().Settings.PasswordCertThumbprint;
    },
    builder.Environment.ContentRootPath,
    ServiceLifetime.Singleton);
builder.Services.AddStepExecutors(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WorkerEngine>(
    sp, sp.GetRequiredService<Onboarding.Core.Security.ISecretDecryptor>()));
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<Onboarding.Steps.Security.ICertificateInfoSource, Onboarding.Steps.Security.StoreCertificateInfoSource>();
builder.Services.AddHostedService<CertificateMonitor>();

var host = builder.Build();

await using (var db = await host.Services.GetRequiredService<IDbContextFactory<OnboardingDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

await host.RunAsync();
return 0;
