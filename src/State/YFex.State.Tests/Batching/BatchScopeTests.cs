using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Batching;

public class BatchScopeTests
{
    [Fact]
    public void SinglePropertyChange_FiresOnceOnDispose()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate()) { vm.Alpha = 1; }

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void ThreePropertyChanges_FireThreeTimesOnDispose()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            vm.Beta = "x";
            vm.Gamma = 2.5;
        }

        recorder.Events.Should().HaveCount(3);
    }

    [Fact]
    public void SamePropertyChangedThreeTimes_BitmapCoalescesToOneNotification()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            vm.Alpha = 2;
            vm.Alpha = 3;
        }

        // Note: the hand-written TestStateObject uses NotifyChanged which sets a bit per id.
        // Three sets to the same id collapse to one bit -> one notification on flush.
        recorder.AssertChangeCount("Alpha", 1);
    }

    [Fact]
    public void NestedScopes_OnlyOutermostFlushes()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            using (vm.BeginUpdate())
            {
                vm.Alpha = 1;
                recorder.Events.Should().BeEmpty();
            }
            recorder.Events.Should().BeEmpty();
        }

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void PreChange_StillFiresImmediately_InsideBatch()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            recorder.ChangingEvents.Should().HaveCount(1);
        }
    }

    [Fact]
    public void BatchFlushBrackets_FireOnceAroundFlush()
    {
        var vm = new TestStateObject();
        var h = new CountingHandler();
        vm.Subscribe(h);

        using (vm.BeginUpdate())
        {
            vm.Alpha = 1;
            vm.Beta = "x";
            h.OnFlushStartingCount.Should().Be(0);
        }

        h.OnFlushStartingCount.Should().Be(1);
        h.OnFlushCompletedCount.Should().Be(1);
    }

    [Fact]
    public void EmptyBatch_StillFiresFlushBrackets()
    {
        var vm = new TestStateObject();
        var h = new CountingHandler();
        vm.Subscribe(h);

        using (vm.BeginUpdate()) { }

        h.OnFlushStartingCount.Should().Be(1);
        h.OnFlushCompletedCount.Should().Be(1);
        h.OnChangedCount.Should().Be(0);
    }

    [Fact]
    public void ConsecutiveBatches_NoLeakedPendingState()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        using (vm.BeginUpdate()) { vm.Alpha = 1; }
        using (vm.BeginUpdate()) { vm.Beta = "y"; }

        recorder.Events.Should().HaveCount(2);
        recorder.AssertChanged("Alpha");
        recorder.AssertChanged("Beta");
    }
}
