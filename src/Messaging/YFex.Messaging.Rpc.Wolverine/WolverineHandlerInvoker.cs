using Wolverine;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Wolverine;

/// <summary>
/// <see cref="IHandlerInvoker"/> that routes every message through Wolverine's
/// <see cref="IMessageBus.InvokeAsync{T}"/>. Registered by
/// <see cref="WolverineRpcExtensions.UseYFexMessagingRpcServerWithWolverine"/>.
/// </summary>
internal sealed class WolverineHandlerInvoker : IHandlerInvoker
{
    private readonly IMessageBus _bus;

    public WolverineHandlerInvoker(IMessageBus bus) => _bus = bus;

    public Task<T> InvokeAsync<T>(object message, CancellationToken ct) =>
        _bus.InvokeAsync<T>(message, ct);

    public Task InvokeAsync(object message, CancellationToken ct) =>
        _bus.InvokeAsync(message, ct);
}
