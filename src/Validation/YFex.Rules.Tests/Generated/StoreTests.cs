using System.Collections.Immutable;
using System.Text;
using YFex.Rules.Tests.HandWritten;

namespace YFex.Rules.Tests.Generated;

/// <summary>4b: RowsJson, parameter/override documents, IRuleStore versions + concurrency, host warm-up.</summary>
public class StoreTests
{
    private static readonly RuleTierPath Tenant = RuleTierPath.Of(RawValue.From("t1"));

    [Fact]
    public void Generated_rows_round_trip_through_json_with_their_metadata()
    {
        SaleRules.Set.TryGetTable("sale.installments", out var table).Should().BeTrue();
        var json = table.SerializeDefaults();
        Encoding.UTF8.GetString(json).Should().Contain("\"Payment\":{\"is\":\"CreditCard\"}").And.Contain("\"Total\":{\"from\":1000");

        var version = (TableVersion<InstallmentTable.Row>)table.Parse(json, new TableVersionInfo("sale.installments", 7, Tenant, DateOnly.MinValue));
        version.Rows.Should().Equal(InstallmentTable.DefaultRows);
        version.Meta[0].RowId.Should().Be(RowMeta.CodeRowId("sale.installments", 0));   // ids survive: history stays comparable

        // An editor-added row without id gets one.
        var edited = Encoding.UTF8.GetBytes("""{"rows":[{"cells":{"Payment":null,"Total":{"below":50},"Tier":{"in":["Gold","Silver"]},"MaxInstallments":2,"MinInstallment":10}}]}""");
        var added = (TableVersion<InstallmentTable.Row>)table.Parse(edited, new("sale.installments", 8, Tenant, DateOnly.MinValue));
        added.Meta[0].RowId.Should().NotBe(Guid.Empty);
        added.Rows[0].Payment.Kind.Should().Be(MatchKind.Any);
        added.Rows[0].Tier.Values.ToArray().Should().Equal(CustomerTier.Gold, CustomerTier.Silver);
        added.Rows[0].Total.Contains(new Money(49.99m)).Should().BeTrue();
    }

    [Fact]
    public async Task Store_versions_are_monotonic_per_rule_set_with_optimistic_concurrency()
    {
        var store = new InMemoryRuleStore();
        var key = new RuleDataKey("sale", RuleDataKind.Table, "sale.installments", Tenant);
        var other = key with { Code = "sale.manual-discount" };

        (await store.AppendAsync(key, 0, DateOnly.MinValue, new byte[] { 1 }, "ana", null, default)).Version.Should().Be(1);
        (await store.AppendAsync(other, 0, DateOnly.MinValue, new byte[] { 2 }, "ana", null, default)).Version.Should().Be(2);
        var stale = () => store.AppendAsync(key, 0, DateOnly.MinValue, new byte[] { 3 }, "bia", null, default).AsTask();
        (await stale.Should().ThrowAsync<RuleStoreConflictException>()).Which.ActualVersion.Should().Be(1);
        (await store.AppendAsync(key, 1, DateOnly.MinValue, new byte[] { 3 }, "bia", "fix", default)).Version.Should().Be(3);

        (await store.GetVersionsAsync(key, default)).Select(v => v.Version).Should().Equal(3, 1);
        (await store.GetAsync(key, 1, default))!.Payload.ToArray().Should().Equal(1);
    }

    [Fact]
    public async Task Host_loads_tables_parameters_and_overrides_from_the_store()
    {
        var store = new InMemoryRuleStore();
        SaleRules.Set.TryGetTable("sale.installments", out var table);
        SaleRules.Set.TryGetRule("loyalty.gold", out var gold);
        var rows = InstallmentTable.DefaultRows.Select(r => r.MaxInstallments == 12 ? r with { MaxInstallments = 10 } : r).ToArray();

        await store.AppendAsync(new("sale", RuleDataKind.Table, "sale.installments", Tenant), 0, DateOnly.MinValue,
            table.Serialize(new TableVersion<InstallmentTable.Row>(new("sale.installments", 0, Tenant, DateOnly.MinValue), [.. rows])), "ana", null, default);
        await store.AppendAsync(new("sale", RuleDataKind.Parameters, "loyalty.gold", Tenant), 0, DateOnly.MinValue,
            RuleDataCodec.WriteParameters(gold.Parameters, new Dictionary<string, object?> { ["rate"] = Percentual.FromPercentage(10) }), "ana", null, default);
        await store.AppendAsync(new("sale", RuleDataKind.Override, "payment.pix", Tenant), 0, DateOnly.MinValue,
            RuleDataCodec.WriteOverride(RuleOverrideAction.Disable), "ana", null, default);

        var host = SaleRules.CreateHost();
        (await host.LoadAsync(store)).Should().BeEmpty();

        var d = SaleRules.Evaluate(host, SaleBuilder.Default with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold });
        d.Max(SaleSituation.Fields.Installments).Should().Be(18);                                  // Gold row unchanged
        SaleRules.Evaluate(host, SaleBuilder.Default with { Payment = PaymentMethod.CreditCard }).Max(SaleSituation.Fields.Installments).Should().Be(10);
        d.Adjustments.AsSpan().ToArray().Single(a => a.Rule.Code == "loyalty.gold").Amount.As<Money>().Should().Be(new Money(119));
        d.RulesetVersion.DataVersion.Should().Be(3);
        SaleRules.Evaluate(host, SaleBuilder.Default with { Payment = PaymentMethod.Pix }).Adjustments.AsSpan().ToArray()
            .Should().NotContain(a => a.Rule.Code == "payment.pix");
    }

    [Fact]
    public void Parameters_without_a_codec_cannot_be_published()
    {
        SaleRules.Set.TryGetRule("promo.l3p2", out var l3p2);
        l3p2.Parameters[0].CanPublish.Should().BeFalse();                                           // ProductSet: custom type, no codec
        SaleRules.Set.TryGetRule("loyalty.gold", out var gold);
        gold.Parameters[0].CanPublish.Should().BeTrue();
        gold.Parameters[0].IsWithinGuard(Percentual.FromPercentage(20)).Should().BeFalse();
    }

    [Fact]
    public void Tier_paths_round_trip_as_text()
    {
        foreach (var path in new[] { RuleTierPath.Code, Tenant, RuleTierPath.Of(RawValue.From("t|1"), RawValue.From(Guid.NewGuid())), RuleTierPath.Of(RawValue.From(42)),
                     RuleTierPath.Of(RawValue.From(new Money(3)), RawValue.From("b"), RawValue.From(7L), RawValue.From("d.e")) })
            RuleTierPaths.Parse(RuleTierPaths.Format(path)).Should().Be(path);
    }
}
