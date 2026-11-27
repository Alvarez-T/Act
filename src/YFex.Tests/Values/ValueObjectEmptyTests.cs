namespace YFex.Tests.Values;

/// <summary>Forms ⚑FM73: core value objects say what "empty" is through <see cref="IValueObject"/>.</summary>
public class ValueObjectEmptyTests
{
    [Fact]
    public void Default_email_is_empty_and_prints_nothing()
    {
        IsEmpty(default(EmailAddress)).Should().BeTrue();
        default(EmailAddress).ToString().Should().BeEmpty();
        IsEmpty(EmailAddress.Parse("ana@example.com", null)).Should().BeFalse();
    }

    [Fact]
    public void Default_phone_is_empty_and_prints_nothing()
    {
        IsEmpty(default(PhoneNumber)).Should().BeTrue();
        default(PhoneNumber).ToString().Should().BeEmpty();
        PhoneNumber.TryParse("(11) 98765-4321", PhoneRegion.Brazil, out PhoneNumber phone).Should().BeTrue();
        IsEmpty(phone).Should().BeFalse();
    }

    // Constrained call: what generated presence checks do (no boxing).
    private static bool IsEmpty<T>(T value) where T : IValueObject => value.IsEmpty;
}
