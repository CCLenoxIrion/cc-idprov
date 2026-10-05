using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Onboarding.Core.Domain;

namespace Onboarding.Web.Auth;

public static class Policies
{
    /// <summary>May create requests (Requester or ITAdmin).</summary>
    public const string Requester = "Requester";

    public const string ITAdmin = "ITAdmin";
}

public sealed class DevUser
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Roles { get; set; } = [];
}

/// <summary>
/// Authentication: Entra ID via Microsoft.Identity.Web (app roles <c>Requester</c>/<c>ITAdmin</c>
/// in the <c>roles</c> claim) or, for local development only, a cookie-based dev login.
/// </summary>
public static class AuthSetup
{
    public const string ModeDev = "Dev";
    public const string ModeEntraId = "EntraId";
    public const string DevLoginPath = "/dev/login";

    public static string Mode(IConfiguration configuration) => configuration["Authentication:Mode"] ?? ModeEntraId;

    public static void AddOnboardingAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var mode = Mode(builder.Configuration);
        if (string.Equals(mode, ModeDev, StringComparison.OrdinalIgnoreCase))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("Authentication:Mode 'Dev' is only allowed in the Development environment.");
            }

            builder.Services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = DevLoginPath;
                    options.AccessDeniedPath = "/access-denied";
                    options.Cookie.Name = "onboarding.dev";
                });
        }
        else if (string.Equals(mode, ModeEntraId, StringComparison.OrdinalIgnoreCase))
        {
            builder.Services
                .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
            builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            });
            builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Authentication:Mode '{mode}'.");
        }

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Requester, p => p.RequireRole(nameof(Role.Requester), nameof(Role.ITAdmin)))
            .AddPolicy(Policies.ITAdmin, p => p.RequireRole(nameof(Role.ITAdmin)));
        builder.Services.AddCascadingAuthenticationState();
    }

    public static void MapOnboardingAuthentication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.Equals(Mode(app.Configuration), ModeDev, StringComparison.OrdinalIgnoreCase))
        {
            MapDevLogin(app);
        }
        else
        {
            app.MapControllers();
        }
    }

    public static string LogoutUrl(IConfiguration configuration) =>
        string.Equals(Mode(configuration), ModeDev, StringComparison.OrdinalIgnoreCase)
            ? "/dev/logout"
            : "/MicrosoftIdentity/Account/SignOut";

    private static void MapDevLogin(WebApplication app)
    {
        var users = app.Configuration.GetSection("DevAuth:Users").Get<List<DevUser>>() ?? [];

        app.MapGet(DevLoginPath, (HttpContext context, IAntiforgery antiforgery, string? returnUrl) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var safeReturn = System.Net.WebUtility.HtmlEncode(LocalUrl(returnUrl));
            var buttons = string.Concat(users.Select(u =>
                $"""
                <button class="btn btn-outline-primary d-block w-100 mb-2" name="user" value="{System.Net.WebUtility.HtmlEncode(u.Name)}">
                  {System.Net.WebUtility.HtmlEncode(u.DisplayName)} <small class="text-muted">({System.Net.WebUtility.HtmlEncode(string.Join(", ", u.Roles))})</small>
                </button>
                """));
            var html = $"""
                <!DOCTYPE html><html lang="de"><head><meta charset="utf-8"><title>Dev-Anmeldung</title>
                <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"></head>
                <body class="p-5"><div style="max-width:420px">
                <h1 class="h4">Entwicklungs-Anmeldung</h1>
                <p class="text-danger small">Nur für lokale Entwicklung. In Produktion ist Entra ID aktiv.</p>
                <form method="post" action="{DevLoginPath}">
                <input type="hidden" name="{tokens.FormFieldName}" value="{tokens.RequestToken}">
                <input type="hidden" name="returnUrl" value="{safeReturn}">
                {buttons}
                </form></div></body></html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost(DevLoginPath, async (HttpContext context, [Microsoft.AspNetCore.Mvc.FromForm] string user, [Microsoft.AspNetCore.Mvc.FromForm] string? returnUrl) =>
        {
            var devUser = users.FirstOrDefault(u => string.Equals(u.Name, user, StringComparison.Ordinal));
            if (devUser is null)
            {
                return Results.BadRequest();
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, devUser.Name), new("name", devUser.DisplayName) };
            claims.AddRange(devUser.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(LocalUrl(returnUrl));
        }).AllowAnonymous();

        app.MapGet("/dev/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(DevLoginPath);
        }).AllowAnonymous();
    }

    private static string LocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
