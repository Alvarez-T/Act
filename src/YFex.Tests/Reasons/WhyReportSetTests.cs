using YFex.Reasons;

namespace YFex.Tests.Reasons;

/// <summary>Reports sent together with a rejection (validation 14 ⚑RX30): lookup by path, path mapping, empty default.</summary>
public class WhyReportSetTests
{
    private static WhyReport Field(string field) => new(field, [new WhyItem(field, WhyKind.Limit) { Field = field, Detail = "até 5%" }])
    { Field = field };

    [Fact]
    public void Default_is_empty()
    {
        WhyReportSet set = default;
        set.IsEmpty.Should().BeTrue();
        set.Count.Should().Be(0);
        set.Reports.Should().BeEmpty();
        set.TryGet(FieldPath.Of("Total"), out _).Should().BeFalse();
        WhyReportSet.From(System.Collections.Immutable.ImmutableArray<WhyReport>.Empty).Should().Be(WhyReportSet.Empty);
    }

    [Fact]
    public void TryGet_finds_a_field_a_line_field_and_the_whole_decision()
    {
        var whole = new WhyReport("Venda", [new WhyItem("Venda", WhyKind.Denied)]);
        var set = WhyReportSet.From(Field("ManualDiscount"), Field("Lines[1].Quantity"), whole);

        set.TryGet(FieldPath.Of("ManualDiscount"), out var a).Should().BeTrue();
        a!.Field.Should().Be("ManualDiscount");
        set.TryGet(FieldPath.Of("Lines[].Quantity", 1), out var b).Should().BeTrue();
        b!.Field.Should().Be("Lines[1].Quantity");
        set.TryGet(FieldPath.Of("Lines[].Quantity", 0), out _).Should().BeFalse();
        set.TryGet(FieldPath.Empty, out var c).Should().BeTrue();
        c.Should().BeSameAs(whole);
    }

    [Fact]
    public void MapFields_rewrites_report_and_item_paths_and_keeps_the_rest()
    {
        var set = WhyReportSet.From(Field("Discount"), Field("Lines[2].Qty"), new WhyReport("Venda", []));
        var mapped = set.MapFields(0, static (_, p) => p.Template switch
        {
            "Discount" => FieldPath.Of("ManualDiscount"),
            "Lines[].Qty" => FieldPath.Of("Items[].Quantity", p.Indices),
            _ => p,
        });

        mapped.Reports.Select(r => r.Field).Should().Equal("ManualDiscount", "Items[2].Quantity", null);
        mapped.Reports[0].Items[0].Field.Should().Be("ManualDiscount");
        mapped.Reports[1].Items[0].Field.Should().Be("Items[2].Quantity");
        set.Reports[0].Field.Should().Be("Discount");   // the original is untouched
    }
}
