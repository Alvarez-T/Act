using System.Linq;
using YFex.State.Validation;

namespace YFex.State.Tests.Validation;

public class ValidationBagTests
{
    [Fact]
    public void Empty_HasErrorsFalse_ErrorCountZero()
    {
        var bag = new ValidationBag();
        bag.HasErrors.Should().BeFalse();
        bag.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void Set_StoresMessage_FiresEventAndUpdatesCount()
    {
        var bag = new ValidationBag();
        string? lastChanged = null;
        bag.ValidationChanged += name => lastChanged = name;

        bag.Set("Email", "Required");

        bag.HasErrors.Should().BeTrue();
        bag.ErrorCount.Should().Be(1);
        bag.GetError("Email").Should().Be("Required");
        lastChanged.Should().Be("Email");
    }

    [Fact]
    public void Set_OverwritesExistingMessage_DoesNotDoubleCount()
    {
        var bag = new ValidationBag();
        bag.Set("X", "first");
        bag.Set("X", "second");

        bag.ErrorCount.Should().Be(1);
        bag.GetError("X").Should().Be("second");
    }

    [Theory]
    [InlineData(ValidationSeverity.Info)]
    [InlineData(ValidationSeverity.Warning)]
    public void Set_NonErrorSeverity_DoesNotIncrementErrorCount(ValidationSeverity severity)
    {
        var bag = new ValidationBag();

        bag.Set("X", "msg", severity);

        bag.HasErrors.Should().BeFalse();
        bag.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void Set_ErrorThenWarning_DecrementsErrorCount()
    {
        var bag = new ValidationBag();
        bag.Set("X", "err", ValidationSeverity.Error);
        bag.ErrorCount.Should().Be(1);

        bag.Set("X", "warn", ValidationSeverity.Warning);

        bag.ErrorCount.Should().Be(0);
        bag.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesEntry_DecrementsCount_FiresEvent()
    {
        var bag = new ValidationBag();
        bag.Set("X", "err");
        string? fired = null;
        bag.ValidationChanged += name => fired = name;

        bag.Clear("X");

        bag.HasErrors.Should().BeFalse();
        bag.ErrorCount.Should().Be(0);
        bag.GetError("X").Should().BeNull();
        fired.Should().Be("X");
    }

    [Fact]
    public void Clear_PropertyWithoutEntry_IsNoOp_NoEvent()
    {
        var bag = new ValidationBag();
        bool fired = false;
        bag.ValidationChanged += _ => fired = true;

        bag.Clear("nope");

        fired.Should().BeFalse();
    }

    [Fact]
    public void ClearAll_ResetsState_FiresWithEmptyString()
    {
        var bag = new ValidationBag();
        bag.Set("A", "1");
        bag.Set("B", "2");
        string? fired = null;
        bag.ValidationChanged += name => fired = name;

        bag.ClearAll();

        bag.HasErrors.Should().BeFalse();
        bag.ErrorCount.Should().Be(0);
        fired.Should().Be(string.Empty);
    }

    [Fact]
    public void GetErrors_NullPropertyName_ReturnsAllMessages()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");
        bag.Set("B", "msgB");

        var all = bag.GetErrors(null).ToList();

        all.Should().BeEquivalentTo(new[] { "msgA", "msgB" });
    }

    [Fact]
    public void GetErrors_EmptyPropertyName_ReturnsAllMessages()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");

        var all = bag.GetErrors("").ToList();

        all.Should().Equal("msgA");
    }

    [Fact]
    public void GetErrors_SpecificProperty_ReturnsOnlyThatPropertysMessage()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");
        bag.Set("B", "msgB");

        bag.GetErrors("A").Should().Equal("msgA");
    }

    [Fact]
    public void All_EnumeratesEveryStoredResult()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");
        bag.Set("B", "msgB", ValidationSeverity.Warning);

        bag.All.Should().HaveCount(2);
    }

    [Fact]
    public void GetErrors_NullAndEmptyString_BothReturnAllMessages()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");
        bag.Set("B", "msgB");

        var fromNull = bag.GetErrors(null).ToList();
        var fromEmpty = bag.GetErrors("").ToList();

        fromNull.Should().BeEquivalentTo(fromEmpty,
            "INDEI contract: null and empty string both mean entity-level errors");
        fromNull.Should().BeEquivalentTo(new[] { "msgA", "msgB" });
    }

    [Fact]
    public void GetErrors_UnknownProperty_ReturnsEmpty()
    {
        var bag = new ValidationBag();
        bag.Set("A", "msgA");

        bag.GetErrors("not-a-real-property").Should().BeEmpty();
    }

    [Fact]
    public void GetErrors_PropertyWithNonErrorSeverity_StillReturnsMessage()
    {
        // GetErrors filters on IsSuccess (Message != null), not on Severity.
        var bag = new ValidationBag();
        bag.Set("X", "warn", ValidationSeverity.Warning);

        bag.GetErrors("X").Should().Contain("warn");
    }
}
