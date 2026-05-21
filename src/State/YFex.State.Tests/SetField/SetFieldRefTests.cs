using System;
using System.Collections.Generic;
using YFex.State.Testing;
using YFex.State.Tests.Helpers;

namespace YFex.State.Tests.SetField;

public class SetFieldRefTests
{
    [Fact]
    public void SameValue_ReturnsFalse_AndDoesNotFireEvents()
    {
        var vm = new TestStateObject();
        vm.Alpha = 5;
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Alpha = 5;

        recorder.Events.Should().BeEmpty();
        recorder.ChangingEvents.Should().BeEmpty();
    }

    [Fact]
    public void DifferentValue_FiresChangingThenChanged()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Alpha = 1;

        recorder.AssertChangingThenChanged("Alpha");
    }

    [Fact]
    public void NullToValue_FiresEvents()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Beta = "hello";

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void ValueToNull_FiresEvents()
    {
        var vm = new TestStateObject { Beta = "hello" };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Beta = null;

        recorder.Events.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(-1, 0)]
    public void Int_DifferentValues_Fire(int from, int to)
    {
        var vm = new TestStateObject { Alpha = from };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Alpha = to;

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void Double_NaNToNaN_DoesNotFire()
    {
        var vm = new TestStateObject { Gamma = double.NaN };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Gamma = double.NaN;

        // EqualityComparer<double>.Default treats NaN == NaN
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void Double_NaNToValue_Fires()
    {
        var vm = new TestStateObject { Gamma = double.NaN };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.Gamma = 1.0;

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void CustomComparer_TreatsValuesEqual_Suppresses()
    {
        var vm = new TestStateObject();
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.SetAlphaWithComparer(0, AlwaysEqualComparer.Instance);

        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void CustomComparer_TreatsValuesUnequal_AlwaysFires()
    {
        var vm = new TestStateObject { Alpha = 5 };
        using var recorder = new StateRecorder<TestStateObject>(vm);

        vm.SetAlphaWithComparer(5, AlwaysUnequalComparer.Instance);

        recorder.Events.Should().HaveCount(1);
    }

    [Fact]
    public void NullComparer_Throws()
    {
        var vm = new TestStateObject();

        var act = () => vm.SetAlphaWithComparer(1, null!);

        act.Should().Throw<NullReferenceException>();
    }

    private sealed class AlwaysEqualComparer : IEqualityComparer<int>
    {
        public static readonly AlwaysEqualComparer Instance = new();
        public bool Equals(int x, int y) => true;
        public int GetHashCode(int obj) => 0;
    }

    private sealed class AlwaysUnequalComparer : IEqualityComparer<int>
    {
        public static readonly AlwaysUnequalComparer Instance = new();
        public bool Equals(int x, int y) => false;
        public int GetHashCode(int obj) => obj;
    }
}
