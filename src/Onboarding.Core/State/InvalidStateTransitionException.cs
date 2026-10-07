namespace Onboarding.Core.State;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException()
    {
    }

    public InvalidStateTransitionException(string message)
        : base(message)
    {
    }

    public InvalidStateTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
