using Microsoft.AspNetCore.Components;
using Onboarding.Web.Services;

namespace Onboarding.Web.Components;

/// <summary>Common error/info handling for pages. Only user-facing messages are shown.</summary>
public abstract class OnboardingComponentBase : ComponentBase
{
    protected string? Error { get; set; }
    protected string? Info { get; set; }
    protected bool Busy { get; private set; }

    protected async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        Error = null;
        Info = null;
        Busy = true;
        try
        {
            await action();
            Info = success;
            return true;
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
