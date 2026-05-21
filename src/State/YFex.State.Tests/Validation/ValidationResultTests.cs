using YFex.State.Validation;

namespace YFex.State.Tests.Validation;

public class ValidationResultTests
{
    [Fact]
    public void Success_IsDefault_AndIsSuccessTrue()
    {
        ValidationResult.Success.Should().Be(default(ValidationResult));
        ValidationResult.Success.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Constructor_PopulatesAllFields()
    {
        var r = new ValidationResult("msg", "Prop", ValidationSeverity.Warning);

        r.Message.Should().Be("msg");
        r.PropertyName.Should().Be("Prop");
        r.Severity.Should().Be(ValidationSeverity.Warning);
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Constructor_DefaultSeverityIsError()
    {
        var r = new ValidationResult("m", "p");
        r.Severity.Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void Equality_SameValuesAreEqual()
    {
        var a = new ValidationResult("m", "p", ValidationSeverity.Info);
        var b = new ValidationResult("m", "p", ValidationSeverity.Info);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentMessage_NotEqual()
    {
        var a = new ValidationResult("m1", "p");
        var b = new ValidationResult("m2", "p");
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equality_DifferentProperty_NotEqual()
    {
        var a = new ValidationResult("m", "p1");
        var b = new ValidationResult("m", "p2");
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equality_DifferentSeverity_NotEqual()
    {
        var a = new ValidationResult("m", "p", ValidationSeverity.Info);
        var b = new ValidationResult("m", "p", ValidationSeverity.Error);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void ToString_Success_ReturnsSuccessLiteral()
    {
        ValidationResult.Success.ToString().Should().Be("Success");
    }

    [Fact]
    public void ToString_Failure_FormatsAllFields()
    {
        var r = new ValidationResult("bad", "Email", ValidationSeverity.Warning);
        r.ToString().Should().Be("Warning: bad [Email]");
    }

    [Fact]
    public void Equals_WithNullObject_IsFalse()
    {
        var r = new ValidationResult("m", "p");
        r.Equals((object?)null).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithDifferentType_IsFalse()
    {
        var r = new ValidationResult("m", "p");
        r.Equals("string").Should().BeFalse();
    }
}
