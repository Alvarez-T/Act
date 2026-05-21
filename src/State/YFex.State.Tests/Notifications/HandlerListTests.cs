using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Notifications;

public class HandlerListTests
{
    [Fact]
    public void OneHandler_ReceivesEvents()
    {
        var vm = new TestStateObject();
        var h = new CountingHandler();
        vm.Subscribe(h);

        vm.Alpha = 1;

        h.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void TwoHandlers_BothReceiveEvents()
    {
        var vm = new TestStateObject();
        var a = new CountingHandler();
        var b = new CountingHandler();
        vm.Subscribe(a);
        vm.Subscribe(b);

        vm.Alpha = 1;

        a.OnChangedCount.Should().Be(1);
        b.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void ThreeHandlers_OverflowsToArrayAndAllReceive()
    {
        var vm = new TestStateObject();
        var handlers = new[] { new CountingHandler(), new CountingHandler(), new CountingHandler() };
        foreach (var h in handlers) vm.Subscribe(h);

        vm.Alpha = 1;

        foreach (var h in handlers) h.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void TenHandlers_AllReceiveInOrder()
    {
        var vm = new TestStateObject();
        var handlers = new CountingHandler[10];
        for (int i = 0; i < 10; i++)
        {
            handlers[i] = new CountingHandler();
            vm.Subscribe(handlers[i]);
        }

        vm.Alpha = 1;

        foreach (var h in handlers)
            h.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void RemoveFromInlineSlot_ShiftsRemaining()
    {
        var vm = new TestStateObject();
        var a = new CountingHandler();
        var b = new CountingHandler();
        vm.Subscribe(a);
        vm.Subscribe(b);

        vm.Unsubscribe(a);
        vm.Alpha = 1;

        a.OnChangedCount.Should().Be(0);
        b.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void RemoveFromOverflow_LeavesOthersIntact()
    {
        var vm = new TestStateObject();
        var handlers = new CountingHandler[5];
        for (int i = 0; i < 5; i++) { handlers[i] = new CountingHandler(); vm.Subscribe(handlers[i]); }

        vm.Unsubscribe(handlers[3]);
        vm.Alpha = 1;

        handlers[3].OnChangedCount.Should().Be(0);
        handlers[0].OnChangedCount.Should().Be(1);
        handlers[1].OnChangedCount.Should().Be(1);
        handlers[2].OnChangedCount.Should().Be(1);
        handlers[4].OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void DefaultOnChanging_OnHandlerThatDoesNotOverride_NoOps()
    {
        var vm = new TestStateObject();
        var h = new MinimalHandler();
        vm.Subscribe(h);

        vm.Alpha = 1;

        h.OnChangedCount.Should().Be(1);
        h.OnChangingCount.Should().Be(0);
    }

    [Fact]
    public void DefaultBatchFlushBrackets_OnHandlerThatDoesNotOverride_NoOps()
    {
        var vm = new TestStateObject();
        var h = new MinimalHandler();
        vm.Subscribe(h);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            vm.Beta = "x";
        }

        h.OnChangedCount.Should().Be(2);
        h.OnFlushStartingCount.Should().Be(0);
        h.OnFlushCompletedCount.Should().Be(0);
    }

    [Fact]
    public void BatchFlushBrackets_FireOncePerOutermostScope()
    {
        var vm = new TestStateObject();
        var h = new CountingHandler();
        vm.Subscribe(h);

        using (vm.BeginUpdate())
        {
            using (vm.BeginUpdate())
            {
                vm.Alpha = 1;
                vm.Beta = "x";
            }
        }

        h.OnFlushStartingCount.Should().Be(1);
        h.OnFlushCompletedCount.Should().Be(1);
    }

    private sealed class MinimalHandler : IChangedHandler
    {
        public int OnChangedCount;
#pragma warning disable CS0649 // intentionally never assigned — relying on default-implemented interface methods
        public int OnChangingCount;
        public int OnFlushStartingCount;
        public int OnFlushCompletedCount;
#pragma warning restore CS0649
        public void OnChanged(object source, in ChangedNotification notification) => OnChangedCount++;
        // Override the count fields only via the OnChanged path; the others use defaults from the interface.
    }
}
