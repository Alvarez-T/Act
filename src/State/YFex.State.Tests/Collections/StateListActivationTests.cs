using YFex.State.Collections;
using YFex.State.Notification;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.Collections;

public class StateListActivationTests
{
    /// <summary>
    /// Implements both <see cref="INotifyChanged"/> and <see cref="IActivatable"/> because
    /// StateList's AttachListener short-circuits on the INotifyChanged check; Activate cascading
    /// for added-while-active items only runs when both interfaces are present.
    /// </summary>
    private sealed class ActivatableItem : IActivatable, INotifyChanged
    {
        public int ActivateCount;
        public int DeactivateCount;
        public bool IsActive { get; private set; }
        public void Activate() { IsActive = true; ActivateCount++; }
        public void Deactivate() { IsActive = false; DeactivateCount++; }
        public void Subscribe(IChangedHandler handler) { }
        public void Unsubscribe(IChangedHandler handler) { }
    }

    [Fact]
    public void Activate_CascadesToActivatableItems()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        var b = new ActivatableItem();
        list.Add(a); list.Add(b);

        list.Activate();

        a.IsActive.Should().BeTrue();
        b.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_CascadesToActivatableItems()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        list.Add(a);
        list.Activate();

        list.Deactivate();

        a.IsActive.Should().BeFalse();
        a.DeactivateCount.Should().Be(1);
    }

    [Fact]
    public void AddWhileActive_ActivatesNewItem()
    {
        using var list = new StateList<ActivatableItem>();
        list.Activate();
        var item = new ActivatableItem();

        list.Add(item);

        item.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Activate_Idempotent()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        list.Add(a);

        list.Activate();
        list.Activate();

        a.ActivateCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_DeactivatesAllItems_WhenActive()
    {
        var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        list.Add(a);
        list.Activate();

        list.Dispose();

        a.IsActive.Should().BeFalse();
    }

    [Fact]
    public void NonActivatableType_NoOpsActivation()
    {
        using var list = new StateList<int>();
        list.Add(1);

        var act = () => list.Activate();
        act.Should().NotThrow();
        list.IsActive.Should().BeTrue();
    }

    [Fact]
    public void RemoveWhileActive_DeactivatesItem()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        list.Add(a);
        list.Activate();
        a.IsActive.Should().BeTrue();

        list.Remove(a);

        a.IsActive.Should().BeFalse();
        a.DeactivateCount.Should().Be(1);
    }

    [Fact]
    public void RemoveAtWhileActive_DeactivatesItem()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        var b = new ActivatableItem();
        list.Add(a); list.Add(b);
        list.Activate();

        list.RemoveAt(0);

        a.IsActive.Should().BeFalse();
        b.IsActive.Should().BeTrue("only the removed item was deactivated");
    }

    [Fact]
    public void ClearWhileActive_DeactivatesAllItems()
    {
        using var list = new StateList<ActivatableItem>();
        var a = new ActivatableItem();
        var b = new ActivatableItem();
        list.Add(a); list.Add(b);
        list.Activate();

        list.Clear();

        a.IsActive.Should().BeFalse();
        b.IsActive.Should().BeFalse();
    }
}
