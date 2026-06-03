namespace YFex.Messaging.Tests.Messaging;

// Top-level so the Messaging generator can emit the [Subscribe<>] wiring correctly.
internal record PingEvent : IEvent;

internal partial class TrackingVm : StateObject
{
    public int PingCount { get; private set; }

    [Subscribe<PingEvent>(KeepAlive = true)]
    private void OnPing(in PingEvent _) => PingCount++;
}

/// <summary>Tests #14–15: Activate/Deactivate cycle (NavigatR Suspend/Resume) and MessagingHost singleton.</summary>
[Trait("Category", "Messaging")]
[Collection("DispatcherTests")]
public sealed class MessagingHostTests : IDisposable
{
    // Reset the global EventBusProvider after each test to prevent cross-test state leak.
    public void Dispose() => EventBusProvider.Configure(new DefaultEventBus());

    // ── Test #14: NavigatR Suspend/Resume cycle ───────────────────────────────

    [Fact]
    public void SuspendResumeCycle_ResubscribesCorrectly()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);

        var vm = new TrackingVm();

        vm.Activate();
        bus.Publish(new PingEvent());
        vm.Deactivate(); // NavigatR Suspend

        vm.Activate();  // NavigatR Resume
        bus.Publish(new PingEvent());
        vm.Deactivate();

        vm.PingCount.Should().Be(2,
            "subscription re-wires on second Activate (NavigatR Resume equivalent)");
    }

    // ── Test #15: MessagingHost singleton ─────────────────────────────────────

    [Fact]
    public async Task MessagingHost_ReceivesEvents_ThenReleasesOnDispose()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);

        var host = new PingCounterHost();
        bus.Publish(new PingEvent());
        bus.Publish(new PingEvent());
        host.PingCount.Should().Be(2, "host receives events while alive");

        await host.DisposeAsync();

        int before = host.PingCount;
        bus.Publish(new PingEvent());
        bus.Publish(new PingEvent());

        host.PingCount.Should().Be(before,
            "disposed MessagingHost must no longer receive events");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class PingCounterHost : MessagingHost
    {
        public int PingCount { get; private set; }

        protected override void OnHostStarting()
        {
            RegisterSubscription(EventBusProvider.Current.On<PingEvent>(_ => PingCount++,
                new SubscribeOptions { KeepAlive = true }));
        }
    }
}
