using System;
using System.Threading;
using System.Threading.Tasks;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Threading;

public class OwnerThreadTests
{
#if DEBUG
    [Fact]
    public void DebugBuild_CrossThreadMutation_ThrowsInvalidOperationException()
    {
        // Use a dedicated thread for the mutation — Task.Run can race onto the test's
        // own thread-pool thread under load and bypass the owner-thread check.
        var vm = new TestStateObject();
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { vm.Alpha = 1; }
            catch (Exception e) { captured = e; }
        });
        t.Start();
        t.Join();

        captured.Should().BeOfType<InvalidOperationException>();
        captured!.Message.Should().Contain("thread");
    }

    [Fact]
    public void DebugBuild_CrossThreadActivate_ThrowsInvalidOperationException()
    {
        var vm = new TestStateObject();
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { vm.Activate(); }
            catch (Exception e) { captured = e; }
        });
        t.Start();
        t.Join();

        captured.Should().BeOfType<InvalidOperationException>();
        captured!.Message.Should().Contain("thread");
    }
#else
    [Fact]
    public void ReleaseBuild_CrossThreadMutation_DoesNotThrow_OwnerCheckIsNoOp()
    {
        var vm = new TestStateObject();
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { vm.Alpha = 1; }
            catch (Exception e) { captured = e; }
        });
        t.Start();
        t.Join();

        captured.Should().BeNull();
    }
#endif

    [Fact]
    public void SameThreadMutation_DoesNotThrow()
    {
        var vm = new TestStateObject();
        var act = () => vm.Alpha = 1;
        act.Should().NotThrow();
    }
}
