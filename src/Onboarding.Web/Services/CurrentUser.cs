using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Onboarding.Core.Domain;
using Onboarding.Data;

namespace Onboarding.Web.Services;

/// <summary>
/// The signed-in user of the current circuit. Services resolve the actor via
/// <see cref="GetAsync"/> before saving; the DbContext interceptor then reads
/// <see cref="CurrentActor"/> for the configuration history.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authenticationStateProvider) : IActorAccessor
{
    private Actor? _actor;

    public string CurrentActor =>
        _actor?.Name ?? throw new InvalidOperationException("Current user has not been resolved; call GetAsync first.");

    public async Task<Actor> GetAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        _actor = FromPrincipal(state.User);
        return _actor;
    }

    public static Actor FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(principal.Identity.Name))
        {
            throw new UnauthorizedAccessException("Nicht angemeldet.");
        }

        var roles = Enum.GetValues<Role>().Where(r => principal.IsInRole(r.ToString())).ToHashSet();
        return new Actor(principal.Identity.Name, roles);
    }

    /// <summary>Display name for the UI (Entra: <c>name</c> claim).</summary>
    public static string DisplayName(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst("name")?.Value ?? principal.Identity?.Name ?? "";
    }
}
