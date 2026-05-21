using System;
using System.Threading.Tasks;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.TaskNotifier;

public class TaskNotifierCodegenTests
{
    [Fact]
    public async Task NotifyOnTaskCompletion_OnTaskOfT_ReFiresOnCompletion()
    {
        var vm = new AsyncResultVm();
        using var recorder = new StateRecorder<AsyncResultVm>(vm);

        var tcs = new TaskCompletionSource<int>();
        vm.Result = tcs.Task;

        recorder.AssertChanged(nameof(AsyncResultVm.Result));
        int afterAssign = recorder.Events.Count;

        tcs.SetResult(7);
        await Task.Delay(50);

        recorder.Events.Should().HaveCount(afterAssign + 1);
    }

    [Fact]
    public async Task NotifyOnTaskCompletion_OnPlainTask_ReFiresOnCompletion()
    {
        var vm = new AsyncTaskVm();
        using var recorder = new StateRecorder<AsyncTaskVm>(vm);

        var tcs = new TaskCompletionSource();
        vm.Operation = tcs.Task;

        recorder.AssertChanged(nameof(AsyncTaskVm.Operation));

        tcs.SetResult();
        await Task.Delay(50);

        recorder.AssertChangeCount(nameof(AsyncTaskVm.Operation), 2);
    }

    [Fact]
    public void NotifyOnTaskCompletion_NullAssignment_DoesNotFire()
    {
        var vm = new AsyncResultVm();
        using var recorder = new StateRecorder<AsyncResultVm>(vm);

        vm.Result = null;

        recorder.Events.Should().BeEmpty();
    }
}
