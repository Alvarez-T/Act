namespace YFex.NavigatR.Exceptions;

/// <summary>
/// Thrown when an invalid context operation is attempted,
/// such as closing a non-existent context or switching to an unknown id.
/// </summary>
public sealed class NavigationContextException : NavigationException
{
    public string? ContextId { get; }

    public NavigationContextException(string message, string? contextId = null)
        : base(message)
    {
        ContextId = contextId;
    }
}
