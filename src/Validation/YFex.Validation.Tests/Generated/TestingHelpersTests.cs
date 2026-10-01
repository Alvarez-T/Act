using System.Globalization;
using YFex.Localization;
using YFex.Validation.BuiltIn;
using YFex.Validation.Testing;

namespace YFex.Validation.Tests.Generated;

public class TestingHelpersTests
{
    [Fact]
    public void Helpers_PassAndFailAsExpected()
    {
        ValidationAssert.Valid(CustomerDto.Valid);

        var bad = ValidationAssert.Invalid(CustomerDto.Valid with { Name = "", Age = 200 });
        bad.ShouldHave("Name", "required").ShouldHave("Age", "range", Severity.Error)
           .ShouldNotHaveAt("Email").ShouldHaveOnly("Name:required", "Age:range")
           .ShouldHaveMessages(new MessageCatalog(BuiltInMessages.Instance), CultureInfo.GetCultureInfo("pt-BR"));

        var act = () => bad.ShouldHave("Email", "required");
        act.Should().Throw<ValidationAssertionException>().WithMessage("*Name*required*");

        var missing = () => ValidationAssert.Invalid(new Point3(1, 1, "x"))
            .ShouldHaveMessages(new MessageCatalog(), CultureInfo.InvariantCulture);
        missing.Should().Throw<ValidationAssertionException>();

        ValidationAssert.Describe(bad).Should().Contain("Age  range (0, 150)");
    }
}
