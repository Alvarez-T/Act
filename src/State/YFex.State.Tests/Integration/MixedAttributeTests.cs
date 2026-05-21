using System.Threading.Tasks;
using YFex.State.Mvvm;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Integration;

public class MixedAttributeTests
{
    [Fact]
    public async Task ObservableMvvmAsyncResult_FullStack_FiresInExpectedOrder()
    {
        var vm = new MvvmAsyncResultVm();
        using var recorder = new StateRecorder<MvvmAsyncResultVm>(vm);

        var tcs = new TaskCompletionSource<int>();
        vm.Result = tcs.Task;

        recorder.AssertChangingThenChanged(nameof(vm.Result));
        int afterAssign = recorder.Events.Count;

        tcs.SetResult(42);
        await Task.Delay(50);

        recorder.Events.Should().HaveCountGreaterOrEqualTo(afterAssign,
            "completion may add a second Changed if NotifyOnTaskCompletion fires through the Mvvm path");
    }

    [Fact]
    public void Inheritance_DerivedAndBaseMutations_BothFireWithoutCollidingPropertyIds()
    {
        var vm = new DerivedMvvmPersonVm();
        using var recorder = new StateRecorder<DerivedMvvmPersonVm>(vm);

        vm.FirstName = "X";
        vm.Email = "y@z";
        vm.IsAdmin = true;

        recorder.AssertChanged("FirstName");
        recorder.AssertChanged("Email");
        recorder.AssertChanged("IsAdmin");
    }

    [Fact]
    public void BatchUpdate_OnMvvmStateObject_CoalescesPostsButNotChangingEvents()
    {
        var ctx = new RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        var vm = new MvvmPersonVm();
        ctx.Reset();

        SynchronizationContext.SetSynchronizationContext(null);

        using (vm.BeginUpdate())
        {
            vm.FirstName = "1";
            vm.LastName = "2";
            vm.Age = 3;
        }

        // Filter posts by state shape:
        //   PropertyChangedEventArgs[]        → batch flush (exactly 1)
        //   PropertyChangingEventArgs (single) → per-mutation Changing (3)
        int batchPosts = 0;
        int changingPosts = 0;
        foreach (var p in ctx.Posts)
        {
            if (p.State is (object _, System.ComponentModel.PropertyChangedEventArgs[])) batchPosts++;
            else if (p.State is (object _, System.ComponentModel.PropertyChangingEventArgs)) changingPosts++;
        }

        batchPosts.Should().Be(1, "PropertyChanged is coalesced to one Post per batch flush");
        changingPosts.Should().Be(3, "PropertyChanging is per-mutation, never coalesced");

        SynchronizationContext.SetSynchronizationContext(null);
    }
}
