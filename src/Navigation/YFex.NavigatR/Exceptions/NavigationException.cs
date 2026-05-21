namespace YFex.NavigatR.Exceptions;

/// <summary>
/// Base class for all navigation exceptions.
/// Catch this broadly or any derived type specifically.
/// </summary>
public abstract class NavigationException : Exception
{
    protected NavigationException(string message) : base(message) { }
    protected NavigationException(string message, Exception inner) : base(message, inner) { }
}
