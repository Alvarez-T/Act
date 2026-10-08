using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>Networking ⚑N4: IServerConnection and the real previous state (D1).</summary>
[Trait("Category", "Rpc")]
public sealed class ServerConnectionTests
{
    [Fact]
    public async Task Replayer_publishes_the_real_previous_state()
    {
        var server = new ManualServerConnection(connected: false);
        var bus = new DefaultEventBus();
        var events = new List<ServerConnectionChangedEvent>();
        using var subscription = bus.Subscribe(new Recipient<ServerConnectionChangedEvent>(events.Add));
        var syncStatus = new SyncStatus();
        using OutboxReplayer replayer = CreateReplayer(server, bus, syncStatus);

        server.StartReconnecting();
        server.GoOnline();
        server.GoOffline();

        events.Should().Equal(
            new ServerConnectionChangedEvent(ServerConnectionState.Disconnected, ServerConnectionState.Reconnecting),
            new ServerConnectionChangedEvent(ServerConnectionState.Reconnecting, ServerConnectionState.Connected),
            new ServerConnectionChangedEvent(ServerConnectionState.Connected, ServerConnectionState.Disconnected));
        syncStatus.IsOffline.Should().BeTrue();
    }

    [Fact]
    public void Always_connected_server_is_connected_and_silent()
    {
        IServerConnection server = AlwaysConnectedServer.Instance;
        server.IsConnected.Should().BeTrue();
        server.State.Should().Be(ServerConnectionState.Connected);
        server.Changed += _ => throw new InvalidOperationException("never raised");
    }

    private static OutboxReplayer CreateReplayer(IServerConnection server, DefaultEventBus bus, SyncStatus syncStatus)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var failureLog = new InMemorySyncFailureLog();
        outbox.SetFailureLog(failureLog);
        var registry = CompiledMessagingRegistry.Build(baseline: []);
        var dispatcher = new LocalDispatcher(new NoHandlers(), registry, server, new InMemoryCache(), outbox, bus, sp);
        return new OutboxReplayer(outbox, failureLog, dispatcher, server, syncStatus, bus, OutboxCommandRegistry.Empty, new InMemoryCache(),
            registry, sp, new YFexMessagingRpcClientOptions(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<OutboxReplayer>());
    }

    private sealed class Recipient<T>(Action<T> action) : IEventRecipient<T>
    {
        public void Receive(in T @event) => action(@event);
    }

    private sealed class NoHandlers : IHandlerInvoker
    {
        public Task<T> InvokeAsync<T>(object message, CancellationToken ct) => throw new NotSupportedException();
        public Task InvokeAsync(object message, CancellationToken ct) => throw new NotSupportedException();
    }
}
