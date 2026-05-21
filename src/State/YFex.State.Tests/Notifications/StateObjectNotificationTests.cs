using YFex.State.Notification;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Notifications;

public class StateObjectNotificationTests
{
    [Fact]
    public void Subscribe_AddsHandler_ThatReceivesEvents()
    {
        var vm = new TestStateObject();
        var handler = new CountingHandler();

        vm.Subscribe(handler);
        vm.Alpha = 1;

        handler.OnChangedCount.Should().Be(1);
        handler.OnChangingCount.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_RemovesHandler_FromFurtherEvents()
    {
        var vm = new TestStateObject();
        var handler = new CountingHandler();

        vm.Subscribe(handler);
        vm.Alpha = 1;
        vm.Unsubscribe(handler);
        vm.Alpha = 2;

        handler.OnChangedCount.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_HandlerThatWasNeverSubscribed_IsNoOp()
    {
        var vm = new TestStateObject();
        var handler = new CountingHandler();

        var act = () => vm.Unsubscribe(handler);

        act.Should().NotThrow();
    }

    [Fact]
    public void NotifyChanging_FiresImmediately_AndNeverBatched()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            vm.Beta = "x";
            vm.Gamma = 1.0;

            recorder.ChangingEvents.Should().HaveCount(3);
            recorder.Events.Should().BeEmpty();
        }

        recorder.Events.Should().HaveCount(3);
    }

    [Fact]
    public void NotifyChanged_DeferredInsideBeginUpdate_FlushesOnDispose()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            recorder.Events.Should().BeEmpty();
        }

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void NotifyChanged_OutsideBatch_FiresSynchronously()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Alpha = 1;

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void FireNotification_BypassesBatchDeferral()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.FireDirect(in TestStateObject.AlphaDescriptor);
            recorder.Events.Should().HaveCount(1);
        }
    }

    [Fact]
    public void Reentrancy_HandlerMutatesPropertyDuringOnChanged_DoesNotDeadlock()
    {
        var vm = new TestStateObject();
        var spy = new ReentrantHandler(vm);
        vm.Subscribe(spy);

        vm.Alpha = 5;

        spy.MutationsObserved.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SubscribeInsideOnChanged_DoesNotInvalidateEnumeration()
    {
        var vm = new TestStateObject();
        var subscribingHandler = new SubscribingHandler(vm);
        vm.Subscribe(subscribingHandler);

        var act = () => vm.Alpha = 1;

        act.Should().NotThrow();
    }

    private sealed class ReentrantHandler : IChangedHandler
    {
        private readonly TestStateObject _vm;
        public int MutationsObserved;
        private int _depth;

        public ReentrantHandler(TestStateObject vm) => _vm = vm;

        public void OnChanged(object source, in ChangedNotification notification)
        {
            MutationsObserved++;
            if (_depth++ < 1)
                _vm.Beta = "from-handler";
            _depth--;
        }
    }

    private sealed class SubscribingHandler : IChangedHandler
    {
        private readonly TestStateObject _vm;
        private bool _added;

        public SubscribingHandler(TestStateObject vm) => _vm = vm;

        public void OnChanged(object source, in ChangedNotification notification)
        {
            if (_added) return;
            _added = true;
            _vm.Subscribe(new CountingHandler());
        }
    }
}
