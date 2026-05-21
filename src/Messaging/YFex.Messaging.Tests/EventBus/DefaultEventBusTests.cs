using System.Collections.Generic;

namespace YFex.Messaging.Tests.EventBus;

public sealed class DefaultEventBusTests
{
    private static DefaultEventBus CreateBus() => new();

    // ── Basic pub/sub ──────────────────────────────────────────────────────────

    [Fact]
    public void Publish_Sync_DeliveresToSyncSubscriber()
    {
        var bus = CreateBus();
        int received = 0;
        var recipient = new LambdaRecipient<int>(_ => received++);
        bus.Subscribe(recipient);

        bus.Publish(42);

        received.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_DeliveresToAsyncSubscriber()
    {
        var bus = CreateBus();
        int received = 0;
        var recipient = new AsyncLambdaRecipient<int>(_ => { received++; return ValueTask.CompletedTask; });
        bus.SubscribeAsync(recipient);

        await bus.PublishAsync(1);

        received.Should().Be(1);
    }

    [Fact]
    public void Publish_DeliveresToMultipleSubscribers()
    {
        var bus = CreateBus();
        int a = 0, b = 0;
        bus.Subscribe(new LambdaRecipient<string>(_ => a++));
        bus.Subscribe(new LambdaRecipient<string>(_ => b++));

        bus.Publish("hello");

        a.Should().Be(1);
        b.Should().Be(1);
    }

    [Fact]
    public void Publish_DoesNotThrow_WhenNoSubscribers()
    {
        var bus = CreateBus();
        var act = () => bus.Publish(99);
        act.Should().NotThrow();
    }

    // ── Type isolation ─────────────────────────────────────────────────────────

    [Fact]
    public void Publish_DoesNotDeliver_ToSubscribersOfDifferentType()
    {
        var bus = CreateBus();
        int stringCount = 0;
        bus.Subscribe(new LambdaRecipient<string>(_ => stringCount++));

        bus.Publish(123); // int, not string

        stringCount.Should().Be(0);
    }

    // ── Unsubscribe (dispose token) ────────────────────────────────────────────

    [Fact]
    public void Dispose_UnregistersSubscription_NoFurtherDelivery()
    {
        var bus = CreateBus();
        int received = 0;
        var token = bus.Subscribe(new LambdaRecipient<int>(_ => received++));

        bus.Publish(1);
        token.Dispose();
        bus.Publish(2);

        received.Should().Be(1);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var bus = CreateBus();
        var token = bus.Subscribe(new LambdaRecipient<int>(_ => { }));
        var act = () => { token.Dispose(); token.Dispose(); };
        act.Should().NotThrow();
    }

    // ── KeepAlive ──────────────────────────────────────────────────────────────

    [Fact]
    public void KeepAlive_True_SubscriptionSurvivesGarbageCollection()
    {
        var bus = CreateBus();
        var counter = new Counter();

        SubscribeKeepAlive(bus, counter);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        bus.Publish(0);
        counter.Value.Should().Be(2, "strong ref survives GC");
    }

    private static void SubscribeKeepAlive(DefaultEventBus bus, Counter counter)
    {
        var recipient = new LambdaRecipient<int>(_ => counter.Value++);
        bus.Subscribe(recipient, new SubscribeOptions { KeepAlive = true });
        bus.Publish(0); // first call while still in scope
        // recipient goes out of scope here — but KeepAlive = true → strong ref kept
    }

    // ── Weak-ref auto-cleanup ──────────────────────────────────────────────────

    [Fact]
    public void WeakRef_RecipientGarbageCollected_DoesNotReceiveEvents_AndNoError()
    {
        var bus = CreateBus();
        var counter = new Counter();

        SubscribeWeak(bus, counter);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();

        var act = () => bus.Publish(1);
        act.Should().NotThrow("dead weak ref must be silently skipped");
        counter.Value.Should().Be(1, "only the publish inside SubscribeWeak counted");
    }

    private static void SubscribeWeak(DefaultEventBus bus, Counter counter)
    {
        var recipient = new LambdaRecipient<int>(_ => counter.Value++);
        bus.Subscribe(recipient); // weak by default
        bus.Publish(0);           // received while in scope
        // recipient goes out of scope → weak ref becomes collectable
    }

    private sealed class Counter { public int Value; }

    // ── Target / Group filtering ───────────────────────────────────────────────

    [Fact]
    public void Publish_WithTargetId_OnlyDelivers_ToMatchingSubscriber()
    {
        var bus = CreateBus();
        int matchCount = 0, noMatchCount = 0;

        bus.Subscribe(new LambdaRecipient<string>(_ => matchCount++),
            new SubscribeOptions { TargetId = "client-A", KeepAlive = true });
        bus.Subscribe(new LambdaRecipient<string>(_ => noMatchCount++),
            new SubscribeOptions { TargetId = "client-B", KeepAlive = true });

        bus.Publish("hello", new PublishOptions { TargetId = "client-A" });

        matchCount.Should().Be(1);
        noMatchCount.Should().Be(0);
    }

    [Fact]
    public void Publish_WithGroupId_OnlyDelivers_ToMatchingSubscriber()
    {
        var bus = CreateBus();
        int matchCount = 0, noMatchCount = 0;

        bus.Subscribe(new LambdaRecipient<string>(_ => matchCount++),
            new SubscribeOptions { GroupId = "room-1", KeepAlive = true });
        bus.Subscribe(new LambdaRecipient<string>(_ => noMatchCount++),
            new SubscribeOptions { GroupId = "room-2", KeepAlive = true });

        bus.Publish("msg", new PublishOptions { GroupId = "room-1" });

        matchCount.Should().Be(1);
        noMatchCount.Should().Be(0);
    }

    [Fact]
    public void Publish_Broadcast_DeliveresToAll_IncludingTargetedSubscribers()
    {
        // Broadcast (no publish filter) reaches EVERY subscriber, including
        // those that registered with a TargetId filter. The subscriber TargetId
        // only restricts which TARGETED publishes it receives — not broadcasts.
        var bus = CreateBus();
        int broadcastCount = 0, targetCount = 0;

        bus.Subscribe(new LambdaRecipient<int>(_ => broadcastCount++),
            new SubscribeOptions { KeepAlive = true });
        bus.Subscribe(new LambdaRecipient<int>(_ => targetCount++),
            new SubscribeOptions { TargetId = "x", KeepAlive = true });

        bus.Publish(0); // broadcast (no options)

        broadcastCount.Should().Be(1, "broadcast subscriber always receives");
        targetCount.Should().Be(1, "broadcast also reaches subscribers with a TargetId filter");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class LambdaRecipient<T>(Action<T> action) : IEventRecipient<T>
    {
        public void Receive(in T @event) => action(@event);
    }

    private sealed class AsyncLambdaRecipient<T>(Func<T, ValueTask> action) : IAsyncEventRecipient<T>
    {
        public ValueTask ReceiveAsync(T @event, CancellationToken ct = default) => action(@event);
    }
}
