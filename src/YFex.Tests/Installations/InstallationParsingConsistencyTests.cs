using Xunit;
using YFex.Installations;

namespace YFex.Tests.Installations;

public sealed class InstallationParsingConsistencyTests
{
    [Theory]
    [InlineData(" 00112233-4455-7677-8899-aabbccddeeff")]
    [InlineData("00112233-4455-7677-8899-aabbccddeeff ")]
    [InlineData("00112233445576778899aabbccddeeff")]
    public void Parse_and_TryParse_enforce_the_same_text_contract(string text)
    {
        Assert.False(InstallationId.TryParse(text, null, out _));
        Assert.Throws<FormatException>(() => InstallationId.Parse(text));
        Assert.Throws<FormatException>(() => InstallationId.Parse(text.AsSpan()));
    }

    [Fact]
    public void Parse_null_reports_the_argument_error()
        => Assert.Throws<ArgumentNullException>(() => InstallationId.Parse((string)null!));
}
