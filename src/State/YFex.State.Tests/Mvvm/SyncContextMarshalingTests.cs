using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Mvvm;

public class SyncContextMarshalingTests
{
    [Fact]
    public void OnSameContext_PropertyChanged_DispatchedDirectly_NoPost()
    {
        var ctx = new RecordingSyncContext();
        using var scope = new SyncContextScope(ctx);

        var vm = new MvvmPersonVm();
        ctx.Reset();

        vm.FirstName = "Alice";

        ctx.PostCount.Should().Be(0, "same-context dispatch is direct invoke");
    }

    [Fact]
    public void OnDifferentContext_PropertyChanged_PostedOncePerMutation()
    {
        var captured = new RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(captured);
        var vm = new MvvmPersonVm();
        captured.Reset();

        // Simulate "background thread" — same thread (owner check passes), different current context.
        SynchronizationContext.SetSynchronizationContext(null);

        vm.FirstName = "x";
        vm.LastName = "y";

        // Filter to PropertyChanged state shape — ChangingPosts also fire (one per mutation).
        int changedPosts = 0;
        foreach (var p in captured.Posts)
            if (p.State is (object _, System.ComponentModel.PropertyChangedEventArgs)) changedPosts++;
        changedPosts.Should().Be(2);

        SynchronizationContext.SetSynchronizationContext(null);
    }

    [Fact]
    public void OffThreadBatch_PropertyChanged_CoalescedToOnePost()
    {
        var captured = new RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(captured);
        var vm = new MvvmPersonVm();
        captured.Reset();

        SynchronizationContext.SetSynchronizationContext(null);

        using (vm.BeginUpdate())
        {
            vm.FirstName = "x";
            vm.LastName = "y";
            vm.Age = 7;
        }

        // Exactly one PropertyChangedEventArgs[] post (the batch); per-mutation Changing posts also exist.
        int batchPosts = 0;
        foreach (var p in captured.Posts)
            if (p.State is (object _, System.ComponentModel.PropertyChangedEventArgs[])) batchPosts++;
        batchPosts.Should().Be(1, "flush emits exactly one batched Post for PropertyChanged");

        SynchronizationContext.SetSynchronizationContext(null);
    }

    [Fact]
    public void OffThreadBatch_PropertyChanging_NotCoalesced_OnePerMutation()
    {
        var captured = new RecordingSyncContext();
        SynchronizationContext.SetSynchronizationContext(captured);
        var vm = new MvvmPersonVm();
        captured.Reset();

        SynchronizationContext.SetSynchronizationContext(null);

        using (vm.BeginUpdate())
        {
            vm.FirstName = "x";
            vm.LastName = "y";
            vm.Age = 7;
        }

        // Three Changing posts — one per mutation. They use a different state shape than the batched
        // PropertyChanged callback (PropertyChangingEventArgs vs PropertyChangedEventArgs[]).
        int changingPosts = 0;
        foreach (var p in captured.Posts)
        {
            if (p.State is (object _, PropertyChangingEventArgs)) changingPosts++;
        }
        changingPosts.Should().Be(3);

        SynchronizationContext.SetSynchronizationContext(null);
    }

    [Fact]
    public void NoCapturedContext_DispatchAlwaysDirect()
    {
        using var scope = new SyncContextScope(null);
        var vm = new MvvmPersonVm();

        int fired = 0;
        vm.PropertyChanged += (_, _) => fired++;

        vm.FirstName = "x";

        fired.Should().Be(1);
    }
}
