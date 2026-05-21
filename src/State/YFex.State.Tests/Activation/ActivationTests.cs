using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Activation;

public class ActivationTests
{
    [Fact]
    public void NewObject_IsInactive()
    {
        var vm = new TestStateObject();
        vm.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_SetsIsActiveTrue()
    {
        var vm = new TestStateObject();
        vm.Activate();
        vm.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_AfterActivate_SetsIsActiveFalse()
    {
        var vm = new TestStateObject();
        vm.Activate();
        vm.Deactivate();
        vm.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_Idempotent_OnlyFiresHookOnce()
    {
        var vm = new SpyVm();
        vm.Activate();
        vm.Activate();
        vm.OnActivatedCount.Should().Be(1);
    }

    [Fact]
    public void Deactivate_Idempotent_OnlyFiresHookOnce()
    {
        var vm = new SpyVm();
        vm.Activate();
        vm.Deactivate();
        vm.Deactivate();
        vm.OnDeactivatedCount.Should().Be(1);
    }

    [Fact]
    public void DeactivateBeforeActivate_IsNoOp()
    {
        var vm = new SpyVm();
        vm.Deactivate();
        vm.OnDeactivatedCount.Should().Be(0);
    }

    [Fact]
    public void Activate_CallsCascadingThenHook_InOrder()
    {
        var vm = new SpyVm();
        vm.Activate();
        vm.OrderLog.Should().Equal("OnActivateCascading", "OnActivated");
    }

    [Fact]
    public void Deactivate_CallsCascadingThenHook_InOrder()
    {
        var vm = new SpyVm();
        vm.Activate();
        vm.OrderLog.Clear();
        vm.Deactivate();
        vm.OrderLog.Should().Equal("OnDeactivateCascading", "OnDeactivated");
    }

    private sealed class SpyVm : StateObject
    {
        public int OnActivatedCount;
        public int OnDeactivatedCount;
        public System.Collections.Generic.List<string> OrderLog { get; } = new();

        protected override void OnActivateCascading() { OrderLog.Add(nameof(OnActivateCascading)); }
        protected override void OnDeactivateCascading() { OrderLog.Add(nameof(OnDeactivateCascading)); }
        protected override void OnActivated() { OnActivatedCount++; OrderLog.Add(nameof(OnActivated)); }
        protected override void OnDeactivated() { OnDeactivatedCount++; OrderLog.Add(nameof(OnDeactivated)); }
    }
}
