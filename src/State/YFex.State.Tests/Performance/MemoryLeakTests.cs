using System;
using System.Runtime.CompilerServices;
using YFex.State.Collections;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Performance;

public class MemoryLeakTests
{
    [Fact]
    public void Subscriber_AfterUnsubscribe_IsCollectable()
    {
        var vm = new TestStateObject();
        var weak = SubscribeAndDrop(vm);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.TryGetTarget(out _)) break;
        }

        weak.TryGetTarget(out _).Should().BeFalse(
            "after Unsubscribe + GC, the handler must be collectable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<CountingHandler> SubscribeAndDrop(TestStateObject vm)
    {
        var h = new CountingHandler();
        vm.Subscribe(h);
        vm.Unsubscribe(h);
        return new WeakReference<CountingHandler>(h);
    }

    [Fact]
    public void StateList_Dispose_ReleasesItemsForCollection()
    {
        WeakReference<object> wr = CreateAndDispose();

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!wr.TryGetTarget(out _)) break;
        }

        wr.TryGetTarget(out _).Should().BeFalse(
            "after StateList.Dispose() reference items should be collectable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CreateAndDispose()
    {
        var list = new StateList<object>();
        var item = new object();
        list.Add(item);
        list.Dispose();
        var wr = new WeakReference<object>(item);
        item = null;
        return wr;
    }
}
