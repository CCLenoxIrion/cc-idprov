using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Workflow;
using Onboarding.Data;
using Onboarding.Steps;
using Onboarding.Web.Auth;
using Onboarding.Web.Components;
using Onboarding.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.AddOnboardingAuthentication();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IActorAccessor>(sp => sp.GetRequiredService<CurrentUser>());

var connectionString = builder.Configuration.GetConnectionString("Onboarding")
                       ?? throw new InvalidOperationException("ConnectionStrings:Onboarding is not configured.");
builder.Services.AddDbContextFactory<OnboardingDbContext>(
    (sp, options) => options.UseOnboardingSqlite(
        connectionString, sp.GetRequiredService<IActorAccessor>(), sp.GetRequiredService<TimeProvider>()),
    ServiceLifetime.Scoped);

builder.Services.AddOnboardingIntegrations(builder.Configuration);
builder.Services.AddSecretProtection(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    sp =>
    {
        using var db = sp.GetRequiredService<IDbContextFactory<OnboardingDbContext>>().CreateDbContext();
        return db.GlobalConfig.AsNoTracking().Single().Settings.PasswordCertThumbprint;
    });

builder.Services.AddScoped(sp => new RequestWorkflow(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<ConfigService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var options = new DbContextOptionsBuilder<OnboardingDbContext>();
    options.UseOnboardingSqlite(connectionString, new FixedActorAccessor(Onboarding.Core.Domain.Actor.SystemName), TimeProvider.System);
    await using var db = new OnboardingDbContext(options.Options);
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapOnboardingAuthentication();
app.MapGet("/audit/export.csv", async (Guid? requestId, RequestService requests, HttpContext context) =>
{
    var entries = await requests.GetAuditAsync(requestId, context.RequestAborted);
    var name = requestId is null ? "audit.csv" : $"audit-{requestId}.csv";
    return Results.File(AuditCsv.Write(entries), "text/csv; charset=utf-8", name);
}).RequireAuthorization(Policies.ITAdmin);

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Entry point; public for integration tests.</summary>
public partial class Program;
