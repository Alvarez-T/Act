namespace YFex.Messaging.Tests.Generator;

/// <summary>
/// Tests that verify the behaviour of source-generated [Subscribe&lt;T&gt;] wiring:
/// adapter creation, lifecycle gate (activate/deactivate), FilterBy guards, and async handlers.
/// Each test configures its own DefaultEventBus and calls EventBusProvider.Configure to avoid
/// cross-test interference with the static provider.
/// </summary>
public sealed class SubscribeGeneratorTests
{
    // ── Lifecycle gate ─────────────────────────────────────────────────────────

    [Fact]
    public void Handler_NotCalled_WhenVmNotActivated()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new SimpleSubscribeVm();

        bus.Publish(new CounterEvent(1));

        vm.CallCount.Should().Be(0, "subscription registers in OnActivateCascading, not before");
    }

    [Fact]
    public void Handler_Called_AfterActivation()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new SimpleSubscribeVm();
        vm.Activate();

        bus.Publish(new CounterEvent(7));

        vm.CallCount.Should().Be(1);
        vm.LastEvent.Should().Be(new CounterEvent(7));
        vm.Deactivate();
    }

    [Fact]
    public void Handler_NotCalled_AfterDeactivation()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new SimpleSubscribeVm();
        vm.Activate();
        bus.Publish(new CounterEvent(1));

        vm.Deactivate();
        bus.Publish(new CounterEvent(2));

        vm.CallCount.Should().Be(1, "second event arrives after deactivation — must be ignored");
    }

    [Fact]
    public void Activate_Deactivate_Reactivate_ResubscribesCorrectly()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new SimpleSubscribeVm();

        vm.Activate();
        bus.Publish(new CounterEvent(0));
        vm.Deactivate();

        // After deactivate/reactivate the subscription is re-established
        vm.Activate();
        bus.Publish(new CounterEvent(0));
        vm.Deactivate();

        vm.CallCount.Should().Be(2, "subscription re-wired on second activation");
    }

    // ── FilterBy guard ─────────────────────────────────────────────────────────

    [Fact]
    public void FilterBy_HandlerCalled_WhenGuardPasses()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new FilteredSubscribeVm { MatchId = 42 };
        vm.Activate();

        bus.Publish(new FilteredEvent(MatchId: 42, Payload: "ok"));

        vm.CallCount.Should().Be(1);
        vm.Deactivate();
    }

    [Fact]
    public void FilterBy_HandlerNotCalled_WhenGuardFails()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new FilteredSubscribeVm { MatchId = 42 };
        vm.Activate();

        bus.Publish(new FilteredEvent(MatchId: 99, Payload: "wrong id"));

        vm.CallCount.Should().Be(0, "e.MatchId (99) != vm.MatchId (42) → filtered out");
        vm.Deactivate();
    }

    // ── Async handler ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncHandler_Called_AndAwaited()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        var vm = new AsyncSubscribeVm();
        vm.Activate();

        await bus.PublishAsync(new AsyncEvent("hello"));

        vm.CallCount.Should().Be(1);
        var msg = await vm.Received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        msg.Should().Be("hello");
        vm.Deactivate();
    }

    // ── KeepAlive ──────────────────────────────────────────────────────────────

    [Fact]
    public void KeepAlive_SubscriptionSurvivesGc_WhenActivated()
    {
        var bus = new DefaultEventBus();
        EventBusProvider.Configure(bus);
        // Activate a KeepAlive VM and let the local variable go out of scope
        WeakReference<KeepAliveSubscribeVm>? weakRef = null;
        KeepAliveSubscribeVm? vm = null;

        ActivateAndDropRef(bus, ref weakRef);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        bus.Publish(new CounterEvent(0));

        // The VM is kept alive by the bus's strong ref (KeepAlive = true)
        weakRef!.TryGetTarget(out vm).Should().BeTrue("KeepAlive keeps the VM rooted");
        vm!.CallCount.Should().BeGreaterOrEqualTo(1);
        vm.Deactivate();
    }

    private static void ActivateAndDropRef(DefaultEventBus bus, ref WeakReference<KeepAliveSubscribeVm>? weakRef)
    {
        var vm = new KeepAliveSubscribeVm();
        vm.Activate();
        weakRef = new WeakReference<KeepAliveSubscribeVm>(vm);
        // vm goes out of scope here
    }
}
