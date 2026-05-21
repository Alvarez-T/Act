namespace YFex.State.Tests.Activation;

public class ActivationCascadeTests
{
    [Fact]
    public void ParentActivate_CascadesToChildStateObjectProperty()
    {
        var parent = new CascadeParentVm
        {
            Child = new CascadeChildVm()
        };

        parent.Activate();

        parent.IsActive.Should().BeTrue();
        parent.Child!.IsActive.Should().BeTrue("the generator emits Child?.Activate() in OnActivateCascading");
    }

    [Fact]
    public void ParentDeactivate_CascadesToChildStateObjectProperty()
    {
        var parent = new CascadeParentVm
        {
            Child = new CascadeChildVm()
        };
        parent.Activate();

        parent.Deactivate();

        parent.IsActive.Should().BeFalse();
        parent.Child!.IsActive.Should().BeFalse("the generator emits Child?.Deactivate() in OnDeactivateCascading");
    }

    [Fact]
    public void ChildSetToNullWhileActive_DoesNotThrow()
    {
        var parent = new CascadeParentVm { Child = new CascadeChildVm() };
        parent.Activate();

        var act = () => parent.Child = null;

        act.Should().NotThrow();
    }

    [Fact]
    public void IgnoreActivation_OnChildProperty_DoesNotCascade()
    {
        var parent = new IgnoringParentVm { Child = new CascadeChildVm() };

        parent.Activate();

        parent.IsActive.Should().BeTrue();
        parent.Child!.IsActive.Should().BeFalse("[IgnoreActivation] excludes the property from OnActivateCascading");
    }

    [Fact]
    public void ReassignmentWhileActive_DeactivatesOld_ActivatesNew()
    {
        var parent = new CascadeParentVm();
        var child1 = new CascadeChildVm();
        var child2 = new CascadeChildVm();
        parent.Child = child1;
        parent.Activate();
        child1.IsActive.Should().BeTrue();

        parent.Child = child2;

        child1.IsActive.Should().BeFalse("the generator emits __old?.Deactivate() in the setter when IsActive");
        child2.IsActive.Should().BeTrue("the generator emits value?.Activate() in the setter when IsActive");
    }

    [Fact]
    public void ReassignmentWhileInactive_DoesNotActivateNew()
    {
        var parent = new CascadeParentVm();
        var child1 = new CascadeChildVm();
        var child2 = new CascadeChildVm();
        parent.Child = child1;

        // parent NOT activated
        parent.Child = child2;

        child1.IsActive.Should().BeFalse();
        child2.IsActive.Should().BeFalse("setter only activates new value when parent IsActive");
    }

    [Fact]
    public void ThreeLevel_GrandchildHierarchy_CascadesAllTheWayDown()
    {
        var grandparent = new CascadeGrandparentVm
        {
            Middle = new CascadeMiddleVm
            {
                Leaf = new CascadeChildVm()
            }
        };

        grandparent.Activate();

        grandparent.IsActive.Should().BeTrue();
        grandparent.Middle!.IsActive.Should().BeTrue();
        grandparent.Middle.Leaf!.IsActive.Should().BeTrue("cascading is recursive — base.OnActivateCascading() chains down");

        grandparent.Deactivate();

        grandparent.Middle.Leaf.IsActive.Should().BeFalse();
        grandparent.Middle.IsActive.Should().BeFalse();
        grandparent.IsActive.Should().BeFalse();
    }
}

// Partial classes declared at namespace scope so the generator's overrides target the correct
// type. Nested partials inside a test class break OnActivateCascading override emission.
public partial class CascadeChildVm : StateObject
{
    [Observable] public partial string Name { get; set; }
}

public partial class CascadeParentVm : StateObject
{
    [Observable] public partial CascadeChildVm? Child { get; set; }
}

public partial class IgnoringParentVm : StateObject
{
    [Observable, IgnoreActivation] public partial CascadeChildVm? Child { get; set; }
}

public partial class CascadeMiddleVm : StateObject
{
    [Observable] public partial CascadeChildVm? Leaf { get; set; }
}

public partial class CascadeGrandparentVm : StateObject
{
    [Observable] public partial CascadeMiddleVm? Middle { get; set; }
}
