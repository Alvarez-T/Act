using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Rpc;

// AOT/trim proof for the refactored, reflection-free outbox replay + dispatch path.
//
// This project sets <IsAotCompatible>true</IsAotCompatible> + <TreatWarningsAsErrors>true</...>,
// so if the outbox replay/dispatch path (OutboxCommandRegistry -> IDispatcher.CommandAsync<T,TResult>)
// used Type.GetType, MakeGenericMethod, or reflection-based JSON, the build would FAIL with an
// IL2xxx / IL3xxx error. It builds clean because:
//   * command materialization uses a source-generated JsonTypeInfo<T> (no reflection JSON), and
//   * dispatch uses the strongly-typed IDispatcher.CommandAsync<T,TResult> overload (no MakeGenericMethod).

// ── An aggregate command (mirrors the framework's nested-Commands convention) ──
public sealed partial class OrdersAggregate
{
    public static partial class Commands
    {
        public sealed record PlaceOrder(int Id, string Sku) : ICommand<OrderPlaced>, IQueueable;
    }
}

public sealed record OrderPlaced(int Id, string Sku);

// ── An event (server-push path) ──
public sealed record OrderShipped(int Id) : IEvent;

// ── A command handler resolved through the source-generated LocalHandlerInvokerRegistry ──
internal sealed class PlaceOrderHandler : ICommandHandler<OrdersAggregate.Commands.PlaceOrder, OrderPlaced>
{
    public Task<OrderPlaced> HandleAsync(OrdersAggregate.Commands.PlaceOrder command, CancellationToken ct)
        => Task.FromResult(new OrderPlaced(command.Id, command.Sku));
}

// ── A remote-event-bus stub that records what RpcEventBus forwarded ──
internal sealed class RecordingRemoteEventBus : IRemoteEventBus
{
    public RpcEventEnvelope? LastEnvelope { get; private set; }

    public Task PublishAsync(RpcEventEnvelope envelope, CancellationToken ct = default)
    {
        LastEnvelope = envelope;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RpcEventEnvelope> SubscribeToServerEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

// ── Source-generated JSON context (AOT-safe; authored, so STJ's generator fills it) ──
[JsonSerializable(typeof(OrdersAggregate.Commands.PlaceOrder))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

// ── A minimal dispatcher that records what it received (stands in for LocalDispatcher) ──
internal sealed class RecordingDispatcher : IDispatcher
{
    public object? LastCommand { get; private set; }

    public ValueTask<QueueableResult<TResult>> CommandAsync<TCommand, TResult>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand<TResult>
    {
        LastCommand = cmd;
        // Fabricate a success result of the right type for the sample.
        var placed = (TResult)(object)new OrderPlaced(((OrdersAggregate.Commands.PlaceOrder)(object)cmd!).Id,
                                                       ((OrdersAggregate.Commands.PlaceOrder)(object)cmd!).Sku);
        return new ValueTask<QueueableResult<TResult>>(QueueableResult<TResult>.Ok(placed));
    }

    public ValueTask<QueueableResult> CommandAsync<TCommand>(TCommand cmd, CancellationToken ct = default)
        where TCommand : ICommand
    {
        LastCommand = cmd;
        return new ValueTask<QueueableResult>(QueueableResult.Ok());
    }

    public ValueTask<CacheableQueryResult<TResult>> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult> => throw new NotSupportedException();

    public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : IEvent => ValueTask.CompletedTask;
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var original = new OrdersAggregate.Commands.PlaceOrder(101, "WIDGET-1");

        // "Enqueue": serialize the command exactly as the outbox stores it (JSON bytes + type name),
        // but via source-generated JsonTypeInfo so this end is AOT-safe too.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(original, SampleJsonContext.Default.PlaceOrder);
        string typeName = typeof(OrdersAggregate.Commands.PlaceOrder).AssemblyQualifiedName!;

        // Build the replay map the AOT-safe way (this is what the generated AddYFexOutboxCommands does,
        // except using source-generated JsonTypeInfo instead of reflection JSON).
        var registry = new OutboxCommandRegistryBuilder()
            .Add<OrdersAggregate.Commands.PlaceOrder, OrderPlaced>(SampleJsonContext.Default.PlaceOrder)
            .Build();

        // "Replay": look up by stored type name -> materialize -> typed dispatch. No reflection.
        if (!registry.TryGetHandler(typeName, out var handler))
        {
            Console.Error.WriteLine("FAIL: no replay handler registered.");
            return 1;
        }

        var dispatcher = new RecordingDispatcher();
        // The recording dispatcher returns success, so the conflict-resolution path (which would use
        // this provider to resolve IConflictResolver<T> / the registry) is never exercised here.
        var replayServices = new ServiceCollection().BuildServiceProvider();
        await handler(payload, dispatcher, replayServices, CancellationToken.None);

        if (dispatcher.LastCommand is not OrdersAggregate.Commands.PlaceOrder replayed || replayed != original)
        {
            Console.Error.WriteLine($"FAIL: round-trip mismatch. Got: {dispatcher.LastCommand}");
            return 1;
        }
        Console.WriteLine($"OK: replayed {replayed} via reflection-free dispatch.");

        // ── Handler-invoke path (LocalHandlerInvoker + source-generated registry, no MakeGenericType) ──
        var services = new ServiceCollection();
        services.AddTransient<ICommandHandler<OrdersAggregate.Commands.PlaceOrder, OrderPlaced>, PlaceOrderHandler>();
        var sp = services.BuildServiceProvider();

        var handlerRegistry = new LocalHandlerInvokerRegistryBuilder()
            .AddCommand<OrdersAggregate.Commands.PlaceOrder, OrderPlaced>()
            .Build();
        var invoker = new LocalHandlerInvoker(sp, handlerRegistry);
        var invoked = await invoker.InvokeAsync<OrderPlaced>(original, CancellationToken.None);
        if (invoked != new OrderPlaced(original.Id, original.Sku))
        {
            Console.Error.WriteLine($"FAIL: handler-invoke mismatch. Got: {invoked}");
            return 1;
        }
        Console.WriteLine($"OK: invoked handler for {original.Sku} via reflection-free resolution.");

        // ── Event path (RpcEventBus pre-registration + publish, no Type.GetType/MakeGenericMethod) ──
        // RegisterEventType<T> is the AOT-safe replacement for the removed reflective server-push
        // dispatch fallback (generated at scale by YFexRpcEventRegistrations.RegisterYFexRpcEventTypes).
        // A local publish (no TargetId) avoids MemoryPack formatter setup, which is orthogonal here.
        var remote = new RecordingRemoteEventBus();
        await using var eventBus = new RpcEventBus(new DefaultEventBus(), remote);
        eventBus.RegisterEventType<OrderShipped>();
        await eventBus.PublishAsync(new OrderShipped(original.Id));
        Console.WriteLine($"OK: registered + published {nameof(OrderShipped)} (AOT-safe, no reflection).");

        return 0;
    }
}
