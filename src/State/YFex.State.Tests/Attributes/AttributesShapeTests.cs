using System;
using System.Linq;
using System.Reflection;

namespace YFex.State.Tests.Attributes;

/// <summary>
/// Verifies that every attribute type declared in YFex.State exposes the expected shape:
/// constructor signatures, properties, AttributeUsage targets, and AllowMultiple flags.
/// Catches accidental API breakage even if the source generator is not yet wired for a given
/// attribute (the runtime contract still ships in YFex.State.dll).
/// </summary>
public class AttributesShapeTests
{
    [Theory]
    [InlineData(typeof(ObservableAttribute))]
    [InlineData(typeof(ComputedAttribute))]
    [InlineData(typeof(EqualityComparerAttribute))]
    [InlineData(typeof(NotifyOnTaskCompletionAttribute))]
    [InlineData(typeof(IgnoreActivationAttribute))]
    [InlineData(typeof(StateCommandAttribute))]
    [InlineData(typeof(TrackableAttribute))]
    [InlineData(typeof(SnapshotAttribute))]
    [InlineData(typeof(PersistAttribute))]
    [InlineData(typeof(ReactsToAttribute))]
    [InlineData(typeof(DebounceAttribute))]
    [InlineData(typeof(ThrottleAttribute))]
    [InlineData(typeof(PollAttribute))]
    [InlineData(typeof(BusyAttribute))]
    [InlineData(typeof(RequiresAllAttribute))]
    [InlineData(typeof(ErrorBucketAttribute))]
    [InlineData(typeof(QueueAttribute))]
    [InlineData(typeof(CooldownAttribute))]
    [InlineData(typeof(LogChangesAttribute))]
    [InlineData(typeof(ObserveItemsAttribute))]
    [InlineData(typeof(PropagateAttribute))]
    [InlineData(typeof(EpochAttribute))]
    [InlineData(typeof(ValidateWithAttribute))]
    [InlineData(typeof(ValidateAsyncAttribute))]
    [InlineData(typeof(LoadOnInitAttribute))]
    [InlineData(typeof(ResetToAttribute))]
    [InlineData(typeof(GateAttribute))]
    public void Attribute_DerivesFromAttribute_AndIsSealed(Type type)
    {
        type.IsSealed.Should().BeTrue();
        type.IsSubclassOf(typeof(Attribute)).Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(ObservableAttribute), AttributeTargets.Property | AttributeTargets.Field, false)]
    [InlineData(typeof(ComputedAttribute), AttributeTargets.Property, false)]
    [InlineData(typeof(EqualityComparerAttribute), AttributeTargets.Property | AttributeTargets.Field, false)]
    [InlineData(typeof(NotifyOnTaskCompletionAttribute), AttributeTargets.Property | AttributeTargets.Field, false)]
    [InlineData(typeof(IgnoreActivationAttribute), AttributeTargets.Property | AttributeTargets.Field, false)]
    [InlineData(typeof(StateCommandAttribute), AttributeTargets.Method, false)]
    [InlineData(typeof(ReactsToAttribute), AttributeTargets.Method, true)]
    [InlineData(typeof(ValidateWithAttribute), AttributeTargets.Property | AttributeTargets.Field, true)]
    [InlineData(typeof(ValidateAsyncAttribute), AttributeTargets.Property | AttributeTargets.Field, true)]
    public void Attribute_HasExpectedAttributeUsage(Type type, AttributeTargets targets, bool allowMultiple)
    {
        var usage = type.GetCustomAttribute<AttributeUsageAttribute>();

        usage.Should().NotBeNull();
        usage!.ValidOn.Should().Be(targets);
        usage.AllowMultiple.Should().Be(allowMultiple);
    }

    [Fact]
    public void EqualityComparer_Constructor_TakesType()
    {
        var attr = new EqualityComparerAttribute(typeof(StringComparer));
        attr.ComparerType.Should().Be(typeof(StringComparer));
    }

    [Fact]
    public void Computed_DependsOn_AcceptsArrayOfStrings()
    {
        var attr = new ComputedAttribute { DependsOn = new[] { "A", "B" } };
        attr.DependsOn.Should().Equal("A", "B");
    }

    [Fact]
    public void StateCommand_IncludeCancelCommand_AndCancelCommandName_AreSettable()
    {
        var attr = new StateCommandAttribute { IncludeCancelCommand = true, CancelCommandName = "Stop" };
        attr.IncludeCancelCommand.Should().BeTrue();
        attr.CancelCommandName.Should().Be("Stop");
    }

    [Fact]
    public void ReactsTo_PropertyNamesIsParams()
    {
        var attr = new ReactsToAttribute("A", "B");
        attr.PropertyNames.Should().Equal("A", "B");
    }

    [Fact]
    public void ReactsTo_OptionalFlags_DefaultFalse()
    {
        var attr = new ReactsToAttribute("X");
        attr.RunOnMainThread.Should().BeFalse();
        attr.CancelPrevious.Should().BeFalse();
    }

    [Fact]
    public void Debounce_StoresMilliseconds()
    {
        new DebounceAttribute(150).Milliseconds.Should().Be(150);
    }

    [Fact]
    public void Throttle_StoresMilliseconds()
    {
        new ThrottleAttribute(200).Milliseconds.Should().Be(200);
    }

    [Fact]
    public void Poll_StoresIntervalAndDefaultsActiveOnlyFalse()
    {
        var attr = new PollAttribute(500);
        attr.IntervalMs.Should().Be(500);
        attr.ActiveOnly.Should().BeFalse();
    }

    [Fact]
    public void Poll_ActiveOnly_Settable()
    {
        var attr = new PollAttribute(100) { ActiveOnly = true };
        attr.ActiveOnly.Should().BeTrue();
    }

    [Fact]
    public void RequiresAll_StoresPropertyNames()
    {
        var attr = new RequiresAllAttribute("A", "B", "C");
        attr.PropertyNames.Should().Equal("A", "B", "C");
    }

    [Fact]
    public void Queue_DefaultCapacityIs64()
    {
        new QueueAttribute().Capacity.Should().Be(64);
    }

    [Fact]
    public void Cooldown_StoresMilliseconds()
    {
        new CooldownAttribute(250).Milliseconds.Should().Be(250);
    }

    [Fact]
    public void ObserveItems_DefaultsBoth_False()
    {
        var attr = new ObserveItemsAttribute();
        attr.Weak.Should().BeFalse();
        attr.ActiveOnly.Should().BeFalse();
    }

    [Fact]
    public void ValidateWith_StoresValidatorType()
    {
        var attr = new ValidateWithAttribute(typeof(string));
        attr.ValidatorType.Should().Be(typeof(string));
    }

    [Fact]
    public void ValidateAsync_StoresValidatorType()
    {
        var attr = new ValidateAsyncAttribute(typeof(string));
        attr.ValidatorType.Should().Be(typeof(string));
    }

    [Fact]
    public void ResetTo_AcceptsObjectDefault()
    {
        new ResetToAttribute(42).DefaultValue.Should().Be(42);
        new ResetToAttribute().DefaultValue.Should().BeNull();
    }

    [Fact]
    public void Gate_StoresPropertyName()
    {
        new GateAttribute("IsValid").PropertyName.Should().Be("IsValid");
    }

    [Fact]
    public void Snapshot_DefaultGroupNull()
    {
        new SnapshotAttribute().Group.Should().BeNull();
    }

    [Fact]
    public void Persist_DefaultKeyNull()
    {
        new PersistAttribute().Key.Should().BeNull();
    }

    [Fact]
    public void Busy_DefaultPropertyNameNull()
    {
        new BusyAttribute().PropertyName.Should().BeNull();
    }

    [Fact]
    public void ErrorBucket_DefaultPropertyNameNull()
    {
        new ErrorBucketAttribute().PropertyName.Should().BeNull();
    }
}
