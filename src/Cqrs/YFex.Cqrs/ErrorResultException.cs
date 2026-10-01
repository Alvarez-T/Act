namespace YFex.Cqrs;

/// <summary>
/// Carries a typed <see cref="Error"/> through APIs that return a plain value (e.g. <c>IHandlerInvoker.InvokeAsync&lt;T&gt;</c>)
/// to the generated Fusion server contract impl, which returns <see cref="TypedError"/> as the RPC result instead of
/// letting the exception cross the wire. Thrown by the server validation decorator (validation stages 1/5) and, via
/// the derived <c>ResultConversionException</c>, by the Wolverine exception middleware.
/// </summary>
public class ErrorResultException : Exception
{
    public ErrorResultException(Error error, Exception? inner = null)
        : base(error.Message, inner) => TypedError = error;

    public Error TypedError { get; }
}
