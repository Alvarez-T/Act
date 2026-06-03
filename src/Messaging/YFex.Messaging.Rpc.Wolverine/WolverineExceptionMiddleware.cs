using YFex.Cqrs;

namespace YFex.Messaging.Rpc.Wolverine;

/// <summary>
/// Optional Wolverine middleware that converts unhandled handler exceptions into
/// <see cref="ResultConversionException"/> so that the Fusion server-side contract impl
/// can return a typed <see cref="Result.Fail"/> to the client instead of propagating
/// the exception through the wire.
///
/// Opt-in via <c>opts.ConvertExceptionsToResults = true</c> in
/// <see cref="WolverineRpcExtensions.UseYFexMessagingRpcServerWithWolverine"/>.
/// Place this as the OUTERMOST middleware in the Wolverine pipeline so it wraps
/// every inner step including retry/error policies.
/// </summary>
public sealed class ResultConversionMiddleware
{
    // Wolverine discovers middleware by the (Func<Task> next) InvokeAsync pattern.
    public async Task InvokeAsync(Func<Task> next)
    {
        try
        {
            await next().ConfigureAwait(false);
        }
        catch (ResultConversionException)
        {
            // Already wrapped — re-throw so the contract impl layer can handle it.
            throw;
        }
        catch (Exception ex)
        {
            throw new ResultConversionException(
                new Error(ErrorType.Fail, ex.Message), ex);
        }
    }
}

/// <summary>
/// Wraps a handler exception with its typed <see cref="Error"/> representation so
/// the Fusion server contract impl can convert it to a wire-safe failure response.
/// </summary>
public sealed class ResultConversionException : Exception
{
    public Error TypedError { get; }

    public ResultConversionException(Error error, Exception inner)
        : base(error.Message, inner) => TypedError = error;
}
