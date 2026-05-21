using System.ComponentModel;
using YFex.State.Internal;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.FeatureSwitchesTests;

[Collection("FeatureSwitches")]  // serialise to avoid concurrent AppContext mutations
public class PropertyChangingSwitchTests
{
    private const string SwitchName = "YFex.State.EnableINotifyPropertyChangingSupport";

    [Fact]
    public void Default_IsEnabled()
    {
        FeatureSwitches.ResetForTesting();
        FeatureSwitches.EnableINotifyPropertyChangingSupport.Should().BeTrue();
    }

    [Fact]
    public void Disabled_NotifyChanging_IsNoOp()
    {
        using var _ = new FeatureSwitchScope(SwitchName, false);

        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Alpha = 1;

        recorder.Events.Should().HaveCount(1);
        recorder.ChangingEvents.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_PropertyChanged_StillFires()
    {
        using var _ = new FeatureSwitchScope(SwitchName, false);

        var vm = new MvvmPersonVm();
        bool changedFired = false;
        bool changingFired = false;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, _) => changedFired = true;
        ((INotifyPropertyChanging)vm).PropertyChanging += (_, _) => changingFired = true;

        vm.FirstName = "x";

        changedFired.Should().BeTrue();
        changingFired.Should().BeFalse();
    }

    [Fact]
    public void Switched_BackOn_ResetForTestingReevaluates()
    {
        using (new FeatureSwitchScope(SwitchName, false))
        {
            FeatureSwitches.EnableINotifyPropertyChangingSupport.Should().BeFalse();
        }

        // After Dispose, the scope reset cache and restored switch.
        FeatureSwitches.EnableINotifyPropertyChangingSupport.Should().BeTrue();
    }
}

[CollectionDefinition("FeatureSwitches", DisableParallelization = true)]
public class FeatureSwitchCollection { }
