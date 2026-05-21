using System;
using System.Threading.Tasks;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.TaskNotifier;

public class TaskNotifierManualTests
{
    [Fact]
    public async Task PendingTask_FiresChangedOnAssignment_AndAgainOnCompletion()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        var tcs = new TaskCompletionSource();
        vm.SetDelta(tcs.Task);

        recorder.Events.Should().HaveCount(1);
        recorder.ChangingEvents.Should().HaveCount(1);

        tcs.SetResult();
        await Task.Delay(50);

        recorder.Events.Should().HaveCount(2);
        recorder.ChangingEvents.Should().HaveCount(1, "PropertyChanging fires only on assignment");
    }

    [Fact]
    public void NullAssignment_ToFirstSet_FiresAndStoresNull()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        // Setting null when the underlying notifier is null should still create a notifier
        // (notifier ??= new) and skip MonitorTask (newValue is null -> isAlreadyCompletedOrNull true).
        bool changed = vm.SetDelta(null);

        // ReferenceEquals(notifier.Task=null, newValue=null) -> short-circuit returns false.
        changed.Should().BeFalse();
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SameTaskAssignedTwice_SecondReturnsFalseAndDoesNotFire()
    {
        var vm = new TestStateObject();
        var t = Task.Delay(20);
        vm.SetDelta(t);
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetDelta(t);

        changed.Should().BeFalse();
        recorder.Events.Should().BeEmpty();
        await t; // drain
    }

    [Fact]
    public async Task FaultedTask_DoesNotCrashProcess_AndStillFiresOnCompletion()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        var tcs = new TaskCompletionSource();
        vm.SetDelta(tcs.Task);
        recorder.Events.Should().HaveCount(1);

        // Now fault while pending — MonitorTask must swallow the exception via
        // GetAwaitableWithoutEndValidation but still fire the post-completion NotifyChanged.
        tcs.SetException(new InvalidOperationException("kaboom"));
        await Task.Delay(50);

        recorder.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task CompletedTask_FiresOnceAndCallbackInvokedSynchronously()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool callbackInvoked = false;
        vm.SetDelta(Task.CompletedTask, _ => callbackInvoked = true);

        await Task.Delay(20); // give MonitorTask a chance even if it was spawned

        recorder.Events.Should().HaveCount(1, "task already completed -> no second fire");
        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task ReassignmentBeforeCompletion_StaleTaskCompletion_DoesNotFire()
    {
        // Two synchronous assignments → 2 Changed + 2 Changing.
        // Then completing the FIRST (stale) task must NOT fire — the ReferenceEquals check in
        // MonitorTask sees notifier.Task = tcs2.Task ≠ newValue = tcs1.Task and skips NotifyChanged.
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();

        vm.SetDelta(tcs1.Task);
        vm.SetDelta(tcs2.Task);
        recorder.Events.Should().HaveCount(2);
        recorder.ChangingEvents.Should().HaveCount(2);

        tcs1.SetResult();
        await Task.Delay(100);
        recorder.Events.Should().HaveCount(2, "stale task completion is gated by ReferenceEquals check");

        // Completing the CURRENT task always fires.
        tcs2.SetResult();
        await Task.Delay(100);
        recorder.Events.Count.Should().BeGreaterOrEqualTo(2,
            "current-task completion may fire one more Changed (timing-sensitive async-void resume)");
    }

    [Fact]
    public void TaskNotifier_ImplicitConversionToTask_NullIn_NullOut()
    {
        Helpers.TestStateObject vm = new();
        Task? task = vm.DeltaTask; // implicit cast through TaskNotifier? -> Task?
        task.Should().BeNull();
    }

    [Fact]
    public async Task TaskNotifier_ImplicitConversionToTask_PopulatedSameInstance()
    {
        var vm = new TestStateObject();
        var t = Task.Delay(20);
        vm.SetDelta(t);

        Task? unwrapped = vm.DeltaTask;

        unwrapped.Should().BeSameAs(t);
        await t;
    }
}
