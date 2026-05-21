using System;
using YFex.State.Notification;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.SetField;

public class SetFieldCallbackTests
{
    [Fact]
    public void Callback_SameValue_NotInvoked_NoEvents()
    {
        var vm = new TestStateObject { Alpha = 5 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetAlphaViaCallback(5);

        changed.Should().BeFalse();
        recorder.Events.Should().BeEmpty();
        vm.Alpha.Should().Be(5);
    }

    [Fact]
    public void Callback_DifferentValue_InvokedOnce_FiresEvents()
    {
        var vm = new TestStateObject { Alpha = 5 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetAlphaViaCallback(10);

        changed.Should().BeTrue();
        vm.Alpha.Should().Be(10);
        recorder.AssertChangingThenChanged("Alpha");
    }

    [Fact]
    public void Callback_Throws_PropagatesAndChangedNotFired()
    {
        var vm = new ThrowingCallbackVm();
        using var recorder = new StateRecorder<ThrowingCallbackVm>(vm);

        var act = () => vm.SetAlphaThrowingCallback(99);

        act.Should().Throw<InvalidOperationException>();
        // Pre-change fired, but post-change did not (setter threw before NotifyChanged).
        recorder.ChangingEvents.Should().HaveCount(1);
        recorder.Events.Should().BeEmpty();
    }

    private sealed class ThrowingCallbackVm : StateObject
    {
        private static readonly ChangedNotification AlphaDescriptor =
            new() { PropertyName = "Alpha", PropertyId = 0u };
        private int _alpha;
        public bool SetAlphaThrowingCallback(int value) =>
            SetField(_alpha, value, _ => throw new InvalidOperationException("setter failed"), in AlphaDescriptor);
    }
}
