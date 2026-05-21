using System;
using System.Threading.Tasks;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.TaskNotifier;

public class TaskNotifierGenericTests
{
    [Fact]
    public async Task GenericTaskNotifier_PendingTask_FiresChangedTwice()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        var tcs = new TaskCompletionSource<int>();
        vm.SetEpsilon(tcs.Task);

        recorder.Events.Should().HaveCount(1);

        tcs.SetResult(42);
        await Task.Delay(50);

        recorder.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task GenericTaskNotifier_ResultAccessibleAfterCompletion()
    {
        var vm = new TestStateObject();
        var tcs = new TaskCompletionSource<int>();
        vm.SetEpsilon(tcs.Task);

        tcs.SetResult(123);
        await Task.Delay(20);

        var t = vm.EpsilonTask;
        t.Should().NotBeNull();
        (await t!).Should().Be(123);
    }

    [Fact]
    public async Task GenericTaskNotifier_FaultedTask_DoesNotPropagateException()
    {
        var vm = new TestStateObject();
        var faulted = Task.Run(new Func<int>(() => throw new InvalidOperationException("nope")));

        var act = () => vm.SetEpsilon(faulted);
        act.Should().NotThrow();

        try { await faulted; } catch { /* drain */ }
        await Task.Delay(50);
    }

    [Fact]
    public async Task GenericTaskNotifier_Callback_InvokedOnceOnCompletion()
    {
        var vm = new TestStateObject();
        var tcs = new TaskCompletionSource<int>();
        int callbackCount = 0;
        vm.SetEpsilon(tcs.Task, _ => callbackCount++);

        callbackCount.Should().Be(0, "task is pending");

        tcs.SetResult(99);
        await Task.Delay(50);

        callbackCount.Should().Be(1);
    }

    [Fact]
    public void GenericTaskNotifier_NullTaskNotifier_ConvertsToNullTask()
    {
        global::YFex.State.TaskNotifier<int>? notifier = null;
        Task<int>? t = notifier;
        t.Should().BeNull();
    }
}
