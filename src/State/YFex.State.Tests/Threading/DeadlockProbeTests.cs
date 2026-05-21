using System.Threading.Tasks;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Threading;

public class DeadlockProbeTests
{
    [Fact(Timeout = 5000)]
    public Task ReentrantChange_InsideOnChanged_DoesNotDeadlock() => Task.Run(() =>
    {
        var vm = new TestStateObject();
        int observed = 0;
        var h = new InlineHandler(_ =>
        {
            if (observed++ == 0) vm.Beta = "from-handler";
        });
        vm.Subscribe(h);

        vm.Alpha = 1;

        observed.Should().BeGreaterOrEqualTo(2);
    });

    [Fact(Timeout = 5000)]
    public Task DeeplyNestedBatchScopes_DoNotStackOverflow() => Task.Run(() =>
    {
        var vm = new TestStateObject();
        DeepNest(vm, 32);
    });

    private static void DeepNest(TestStateObject vm, int depth)
    {
        if (depth == 0) { vm.Alpha = 99; return; }
        using (vm.BeginUpdate())
        {
            DeepNest(vm, depth - 1);
        }
    }

    [Fact(Timeout = 5000)]
    public Task SubscribeInsideOnChanged_DoesNotDeadlock() => Task.Run(() =>
    {
        var vm = new TestStateObject();
        var h = new InlineHandler(_ =>
        {
            vm.Subscribe(new InlineHandler(_ => { }));
        });
        vm.Subscribe(h);

        var act = () => vm.Alpha = 1;
        act.Should().NotThrow();
    });

    private sealed class InlineHandler : IChangedHandler
    {
        private readonly System.Action<ChangedNotification> _action;
        public InlineHandler(System.Action<ChangedNotification> action) => _action = action;
        public void OnChanged(object source, in ChangedNotification notification) => _action(notification);
    }
}
