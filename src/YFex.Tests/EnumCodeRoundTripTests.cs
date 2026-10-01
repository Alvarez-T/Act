using YFex.Extensions;

namespace YFex.Tests;

public class EnumCodeRoundTripTests
{
    private enum Mixed
    {
        [EnumCode("C")] Card,
        Cash,            // no [EnumCode] → GetCode falls back to the name
        [EnumCode("Cash2")] Other,
    }

    [Fact]
    public void Every_GetCode_reads_back_with_FromCode()
    {
        foreach (var v in Enum.GetValues<Mixed>())
            EnumExtensions.FromCode<Mixed>(v.GetCode()).Should().Be(v);
    }

    [Fact]
    public void Codes_win_over_member_names()
    {
        EnumExtensions.FromCode<Mixed>("C").Should().Be(Mixed.Card);
        EnumExtensions.TryFromCode<Mixed>("Card").Should().BeNull();         // coded member: only its code is accepted
        EnumExtensions.FromCode<Mixed>("cash", StringComparison.OrdinalIgnoreCase).Should().Be(Mixed.Cash);
    }
}
