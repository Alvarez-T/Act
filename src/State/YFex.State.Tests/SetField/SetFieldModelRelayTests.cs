using System.Collections.Generic;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.SetField;

public class SetFieldModelRelayTests
{
    private sealed class Box { public int Value; }

    [Fact]
    public void ModelRelay_StaticSetter_UpdatesModelAndFiresEvents()
    {
        var vm = new TestStateObject();
        var box = new Box { Value = 0 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetAlphaViaModelRelay(box, 42, static (b, v) => b.Value = v);

        changed.Should().BeTrue();
        box.Value.Should().Be(42);
        recorder.AssertChangingThenChanged("Alpha");
    }

    [Fact]
    public void ModelRelay_SameValue_DoesNotInvokeSetter()
    {
        var vm = new TestStateObject { Alpha = 7 };
        var box = new Box { Value = 0 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetAlphaViaModelRelay(box, 7, static (b, v) => b.Value = v);

        changed.Should().BeFalse();
        box.Value.Should().Be(0);
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void ModelRelay_WithCustomComparer_SuppressesEqualValues()
    {
        var vm = new TestStateObject { Alpha = 1 };
        var box = new Box { Value = 0 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        bool changed = vm.SetAlphaViaModelRelay(
            box, 1, AlwaysEqual.Instance, static (b, v) => b.Value = v);

        changed.Should().BeFalse();
        recorder.Events.Should().BeEmpty();
        box.Value.Should().Be(0);
    }

    [Fact]
    public void ModelRelay_CustomComparerAlwaysUnequal_FiresEachCall()
    {
        var vm = new TestStateObject { Alpha = 1 };
        var box = new Box { Value = 0 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.SetAlphaViaModelRelay(box, 1, AlwaysUnequal.Instance, static (b, v) => b.Value = v);

        recorder.Events.Should().HaveCount(1);
        box.Value.Should().Be(1);
    }

    private sealed class AlwaysEqual : IEqualityComparer<int>
    {
        public static readonly AlwaysEqual Instance = new();
        public bool Equals(int x, int y) => true;
        public int GetHashCode(int obj) => 0;
    }

    private sealed class AlwaysUnequal : IEqualityComparer<int>
    {
        public static readonly AlwaysUnequal Instance = new();
        public bool Equals(int x, int y) => false;
        public int GetHashCode(int obj) => obj;
    }
}
