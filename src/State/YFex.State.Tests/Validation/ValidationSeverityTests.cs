using YFex.State.Validation;

namespace YFex.State.Tests.Validation;

public class ValidationSeverityTests
{
    [Theory]
    [InlineData(ValidationSeverity.Info, 0)]
    [InlineData(ValidationSeverity.Warning, 1)]
    [InlineData(ValidationSeverity.Error, 2)]
    public void EnumValues_AreStableByteIndices(ValidationSeverity severity, int expected)
    {
        ((byte)severity).Should().Be((byte)expected);
    }

    [Fact]
    public void DefaultValue_IsInfo()
    {
        default(ValidationSeverity).Should().Be(ValidationSeverity.Info);
    }
}
