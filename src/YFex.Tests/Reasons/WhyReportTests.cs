using YFex.Reasons;

namespace YFex.Tests.Reasons;

/// <summary>The neutral reason model (⚑WH1, validation 14 ⚑RX17, RX27): worst kind, JSON round trip, empty members left out.</summary>
public class WhyReportTests
{
    private static WhyReport Installments() => new("Parcelas",
    [
        new WhyItem("Parcelas", WhyKind.Limit)
        {
            Detail = "até 10x",
            Field = "Installments",
            Key = WhyKey.Create("rule", "installments.first-line"),
            Path = [new(WhyKey.Create("ruleset", "sale"), "Venda"), new(WhyKey.Create("group", "sale/CardLimits"), "Parcelamento")],
        },
        new WhyItem("Cartão 12x", WhyKind.NotApplied)
        {
            Key = WhyKey.Create("rule", "installments.card.12x"),
            Conditions =
            [
                new("requer Payment = CreditCard", WhyCheck.Passed),
                new("requer Total ≥ R$ 1.000,00", WhyCheck.Failed, "atual R$ 940,00", "faltam R$ 60,00"),
                new("requer Tier = Gold", WhyCheck.WouldPass),
            ],
            Links = [new("também limitado por Primeira linha", WhyKey.Create("rule", "installments.first-line"))],
        },
    ])
    { Field = "Installments", Value = "1" };

    [Fact]
    public void Worst_is_the_most_severe_kind()
    {
        Installments().Worst.Should().Be(WhyKind.Limit);
        (Installments() with { Items = [new("x", WhyKind.Applied), new("y", WhyKind.Denied), new("z", WhyKind.Warning)] })
            .Worst.Should().Be(WhyKind.Denied);
        new WhyReport("empty", []).Worst.Should().Be(WhyKind.Info);
    }

    [Fact]
    public void Json_round_trips_with_camel_case_names_enum_names_and_no_empty_members()
    {
        var report = Installments();
        string json = WhyJson.Serialize(report);

        json.Should().Contain("\"title\":\"Parcelas\"").And.Contain("\"kind\":\"Limit\"").And.Contain("\"state\":\"Failed\"")
            .And.Contain("\"key\":\"rule:installments.first-line\"").And.Contain("\"missing\":\"faltam R$ 60,00\"");
        json.Should().NotContain("worst").And.NotContain("\"initial\"").And.NotContain("\"amount\"");

        var back = WhyJson.Deserialize(json);
        WhyJson.Serialize(back).Should().Be(json);
        back.Items[1].Conditions[1].State.Should().Be(WhyCheck.Failed);
        back.Items[0].Path[1].Key.Should().Be(WhyKey.Create("group", "sale/CardLimits"));
        back.Items[1].Links[0].Target.Should().Be(back.Items[0].Key);
    }

    [Fact]
    public void Missing_collections_read_back_as_empty()
    {
        var back = WhyJson.Deserialize("""{"title":"t","items":[{"title":"a","kind":"Info"}]}""");
        back.Items.Should().ContainSingle();
        back.Items[0].Path.IsDefault.Should().BeFalse();
        back.Items[0].Conditions.IsEmpty.Should().BeTrue();
        back.Items[0].Key.IsEmpty.Should().BeTrue();
    }
}
