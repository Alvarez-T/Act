using YFex.Validation;

namespace YFex.Rules.Tests.HandWritten;

/// <summary>04 §14: situation → message path renames (rule violations land on the field the caller sent).</summary>
public class SituationPathMapTests
{
    private static readonly SituationPathMap Map = SituationPathMap.Empty
        .Path("Total", "Amount")
        .Path("Lines[]", "Items[]")
        .Path("Lines[].Qty", "Items[].Quantity");

    [Theory]
    [InlineData("Total", "Amount")]
    [InlineData("Total.Currency", "Amount.Currency")]         // below a renamed member
    [InlineData("TotalTax", "TotalTax")]                      // prefix of a NAME, not of a segment → unchanged
    [InlineData("Lines[].Price", "Items[].Price")]
    [InlineData("Lines[].Qty", "Items[].Quantity")]           // most specific rename wins
    [InlineData("Customer", "Customer")]
    public void Templates_are_renamed_by_segment(string from, string to)
        => Map.Map(FieldPath.Of(from)).Template.Should().Be(to);

    [Fact]
    public void Indices_are_kept()
    {
        var mapped = Map.Map(FieldPath.Of("Lines[].Qty", 3));
        mapped.ToString().Should().Be("Items[3].Quantity");
    }

    [Fact]
    public void Violations_are_mapped_and_an_unchanged_list_is_returned_as_is()
    {
        var list = ViolationList.From([new Violation(FieldPath.Of("Total"), "x"), new Violation(FieldPath.Of("Customer"), "y")]);
        var mapped = Map.Map(list);
        mapped.AsSpan()[0].Path.ToString().Should().Be("Amount");
        mapped.AsSpan()[1].Path.ToString().Should().Be("Customer");

        var untouched = ViolationList.From([new Violation(FieldPath.Of("Customer"), "y")]);
        Map.Map(untouched).AsSpan().ToArray().Should().Equal(untouched.AsSpan().ToArray());
    }

    [Fact]
    public void Both_sides_need_the_same_number_of_index_holes_and_a_path_maps_once()
    {
        var holes = () => SituationPathMap.Empty.Path("Lines[]", "Items");
        holes.Should().Throw<ArgumentException>().WithMessage("*same number of '[]'*");
        var twice = () => SituationPathMap.Empty.Path("Total", "A").Path("Total", "B");
        twice.Should().Throw<ArgumentException>().WithMessage("*mapped twice*");
    }
}
