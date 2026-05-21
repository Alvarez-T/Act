using System;
using YFex.State.Mvvm;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Notifications;

public class ExceptionTests
{
    [Fact]
    public void HandlerThrowsInOnChanged_ExceptionPropagates()
    {
        var vm = new TestStateObject();
        vm.Subscribe(new ThrowingHandler());

        var act = () => vm.Alpha = 1;

        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void HandlerThrowsInOnChanged_SubsequentHandlersNotCalled()
    {
        var vm = new TestStateObject();
        var first = new ThrowingHandler();
        var second = new CountingHandler();
        vm.Subscribe(first);
        vm.Subscribe(second);

        try { vm.Alpha = 1; } catch { }

        second.OnChangedCount.Should().Be(0,
            "documented behavior — InlineHandlerList iterates synchronously and exception aborts");
    }

    [Fact]
    public void HandlerThrowsInOnChanging_PropagatesBeforeFieldUpdate()
    {
        var vm = new TestStateObject();
        vm.Subscribe(new ThrowingHandler { OnChangingException = new InvalidOperationException("pre") });

        var act = () => vm.Alpha = 99;

        act.Should().Throw<InvalidOperationException>().WithMessage("pre");
        vm.Alpha.Should().Be(0, "field was not updated because Changing handler threw before assignment");
    }

    [Fact]
    public void GetPropertyChangedArgs_UnknownId_ReturnsUnknownPropertyArgs_DoesNotThrow()
    {
        var vm = new MvvmPersonVm();
        vm.FireUnknownDescriptor();
    }
}

public partial class MvvmPersonVm : MvvmStateObject
{
    // Adds a way to fire a notification with an unknown id without going through codegen
    // so we can exercise the GetPropertyChangedArgs base fallback path.
    public void FireUnknownDescriptor()
    {
        var n = new ChangedNotification { PropertyName = "ghost", PropertyId = 999u };
        ((IChangedHandler)this).OnChanged(this, in n);
    }
}
