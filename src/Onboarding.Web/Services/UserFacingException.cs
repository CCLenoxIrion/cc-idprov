namespace Onboarding.Web.Services;

/// <summary>Error whose message is shown to the user as is (German). Never contains secrets.</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException()
    {
    }

    public UserFacingException(string message)
        : base(message)
    {
    }

    public UserFacingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public const string ConcurrencyMessage =
        "Der Datensatz wurde zwischenzeitlich geändert (z. B. vom Worker oder einem anderen Admin). Bitte neu laden und erneut versuchen.";
}
