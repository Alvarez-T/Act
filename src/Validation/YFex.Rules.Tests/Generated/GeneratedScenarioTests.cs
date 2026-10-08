using System.Collections.Immutable;
using System.Globalization;
using YFex.Collections;
using YFex.Localization;
using YFex.Rules.Tests.HandWritten;

namespace YFex.Rules.Tests.Generated;

/// <summary>Doc-12 acceptance scenarios on GENERATED code (situations, rule set, tables, explain bodies).</summary>
public class GeneratedScenarioTests
{
    private static SaleSituation S => SaleBuilder.Default;
    private static Percentual Pct(decimal v) => Percentual.FromPercentage(v);

    [Fact]
    public void Generated_schema_reads_and_ids()
    {
        SaleSituation.Schema.Name.Should().Be("sale");
        SaleSituation.Schema.Count.Should().Be(12);
        SaleSituation.Fields.Installments.Id.Should().Be(SaleSituation.FieldIds.Installments);
        SaleSituation.Schema[SaleSituation.FieldIds.Total].Role.Should().Be(FieldRole.Derived);
        SaleSituation.Schema[SaleSituation.FieldIds.Tenant].Tier.Should().Be(1);
        SaleSituation.Schema[SaleSituation.FieldIds.Store].Tier.Should().Be(2);
        SaleSituation.Schema[SaleSituation.FieldIds.Payment].EnumOptions.Length.Should().Be(4);
        SaleSituation.Schema.LineSchema!.Name.Should().Be("sale.line");

        SaleSituation.Read(S, SaleSituation.FieldIds.Total).Should().Be(RawValue.From(new Money(1200)));
        SaleSituation.Read(S, SaleSituation.FieldIds.Tenant).Should().Be(RawValue.From("t1"));
        SaleSituation.TryGetFieldId("Payment", out var id).Should().BeTrue();
        id.Should().Be(SaleSituation.FieldIds.Payment);
        SaleSituation.ReadLine(S, 1, SaleLineSituation.FieldIds.Product).Should().Be(RawValue.From("tv"));
        SaleSituation.TierPath(S).Should().Be(RuleTierPath.Of(RawValue.From("t1"), RawValue.From("s1")));
        SaleSituation.EffectiveDate(S).Should().Be(new DateOnly(2026, 9, 29));
    }

    [Fact]
    public void Generated_situation_round_trips_hashes_and_diffs()
    {
        var s = S with { Tier = CustomerTier.Gold, ManualDiscount = Pct(2.5m) };
        using var buffer = new ArrayPoolBufferWriter<byte>();
        SaleSituation.WriteTo(s, buffer);
        var back = SaleSituation.ReadFrom(buffer.WrittenSpan);

        back.Lines.AsSpan().SequenceEqual(s.Lines.AsSpan()).Should().BeTrue();
        (back with { Lines = s.Lines }).Should().Be(s);
        SaleSituation.Hash(back).Should().Be(SaleSituation.Hash(s));
        SaleSituation.Hash(s with { Installments = 2 }).Should().NotBe(SaleSituation.Hash(s));
        SaleSituation.DiffMask(s, s with { Payment = PaymentMethod.Pix }).Should().Be(1UL << SaleSituation.FieldIds.Payment);
    }

    [Fact]
    public void Same_outcomes_as_the_hand_written_rule_set()
    {
        (SaleSituation g, HSale h)[] cases =
        [
            (S, HSaleBuilder.Default),
            (S with { Tier = CustomerTier.Gold, IsBirthdayMonth = true }, HSaleBuilder.Default with { Tier = CustomerTier.Gold, IsBirthdayMonth = true }),
            (S with { Payment = PaymentMethod.Pix, Installments = 3 }, HSaleBuilder.Default with { Payment = PaymentMethod.Pix, Installments = 3 }),
            (S with { ManualDiscount = Pct(8) }, HSaleBuilder.Default with { ManualDiscount = Pct(8) }),
            (S with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold, Installments = 18 },
             HSaleBuilder.Default with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold, Installments = 18 }),
        ];
        foreach (var (g, h) in cases)
        {
            var dg = SaleRules.Evaluate(g);
            var dh = HandSaleRules.Evaluate(h);
            dg.Verdict.Kind.Should().Be(dh.Verdict.Kind);
            dg.Totals.Final.Should().Be(dh.Totals.Final);
            dg.Adjustments.AsSpan().ToArray().Select(a => (a.Rule.Code, a.Amount)).Should().Equal(dh.Adjustments.AsSpan().ToArray().Select(a => (a.Rule.Code, a.Amount)));
            dg.Violations.AsSpan().ToArray().Select(v => (v.Code.Value, v.Path.ToString())).Should().Equal(dh.Violations.AsSpan().ToArray().Select(v => (v.Code.Value, v.Path.ToString())));
        }
    }

    [Fact]
    public void Generated_tables_limit_installments_and_require_approval()
    {
        SaleRules.Evaluate(S with { Payment = PaymentMethod.Pix, Installments = 3 }).Verdict.IsDenied.Should().BeTrue();
        var card = SaleRules.Evaluate(S with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold, Installments = 12 });
        card.Verdict.IsAllowed.Should().BeTrue();
        card.Max(SaleSituation.Fields.Installments).Should().Be(18);
        card.TryGetMin(SaleSituation.Fields.Installments, out int _).Should().BeFalse();
        card.DerivedValue(SaleRules.InstallmentValue).Should().Be(RawValue.From(new Money(94.21m)));

        var seller = SaleRules.Evaluate(S with { ManualDiscount = Pct(8) });
        seller.Verdict.IsNeedsApproval.Should().BeTrue();
        seller.Approvals[0].Rule.Code.Should().Be("sale.manual-discount");
        seller.Approvals[0].Rule.IsTableRow.Should().BeTrue();
    }

    [Fact]
    public void Generated_line_table_competes_with_line_promotions()
    {
        var cables = S with { Lines = [SaleBuilder.Coca, SaleBuilder.Tv, SaleBuilder.Cable with { Category = "cables" }] };
        var d = SaleRules.Evaluate(cables, new() { Trace = TraceLevel.Full });

        var line2 = d.Adjustments.AsSpan().ToArray().Single(a => a.LineIndex == 2);
        line2.Rule.Code.Should().Be("sale.category-discount");
        line2.Amount.As<Money>().Should().Be(new Money(35));                                           // 50% of 70
        line2.Rule.RowId.Should().Be(RowMeta.CodeRowId("sale.category-discount", 0));

        // Coca line: the table has no row for "drinks" → "why not" = the Category column.
        var miss = d.Trace!.For("sale.category-discount").Single(e => e.LineIndex == 0);
        miss.Outcome.TryGetValue(out NotApplicable na).Should().BeTrue();
        na.Failed.Index.Should().Be(0);
    }

    [Fact]
    public void Generated_explain_bodies_name_the_failed_condition()
    {
        var d = SaleRules.Evaluate(S with { BusinessDate = new DateOnly(2026, 11, 27), Lines = [SaleBuilder.Tv with { Category = "services" }] },
            new() { Trace = TraceLevel.Full });

        var bf = d.Trace!.For("promo.blackfriday").Single();
        bf.Outcome.TryGetValue(out NotApplicable na).Should().BeTrue();
        na.Failed.Index.Should().Be(1);                                                        // month ok, category = services

        var e = d.Explain(new MessageCatalog(RuleMessages.Instance), CultureInfo.InvariantCulture);
        e.NotApplied.Should().Contain(l => l.Rule.Code == "promo.blackfriday" && l.Detail == "requires Category ≠ services");
        e.NotApplied.Should().Contain(l => l.Rule.Code == "payment.pix" && l.Detail!.StartsWith("requires Payment = "));
    }

    [Fact]
    public void Generated_row_validation_and_guardrail_reject_rows_at_publish()
    {
        var rows = InstallmentTable.DefaultRows.ToArray();
        rows[1] = rows[1] with { MaxInstallments = 60 };                                       // Range(1, 48) AND legal 24
        using var scope = YFex.Validation.ViolationWriter.Begin(out var w);
        InstallmentTable.Check(rows, ref w).Should().BeFalse();
        w.AsSpan().ToArray().Select(v => (v.Code.Value, v.Path.ToString())).Should().Contain(
            [("range", "Rows[1].MaxInstallments"), ("sale.installments.above-legal", "Rows[1].MaxInstallments")]);

        InstallmentTable.Schema.Columns[3].Constraints.Max.Should().Be(RawValue.From(48));   // editor gets the cell limits
    }

    [Fact]
    public void Generated_catalog_lists_rules_and_tables()
    {
        SaleRules.Catalog.TryGet("sale.installments", out var t).Should().BeTrue();
        t.IsTable.Should().BeTrue();
        t.EditableAt.Should().Equal("Tenant", "Store");
        t.PublishPermission.Should().Be(RulesTestPermissions.Ids.InstallmentsPublish);   // catalog metadata keeps the id
        SaleRules.Catalog.TryGet("loyalty.gold", out var g).Should().BeTrue();
        g.EditableAt.Should().Equal("Tenant");                                              // from its parameter
        g.Version.Should().Be(3);
        SaleRules.Catalog.TryGet("sale.installments.boleto", out var b).Should().BeTrue();
        b.IsSealed.Should().BeTrue();
        SaleRules.RuleIds.GoldLoyalty.Should().Be((ushort)g.Id);
    }

    [Fact]
    public void Generated_host_uses_published_versions()
    {
        var host = SaleRules.CreateHost();
        host.Publish(new TableVersion<InstallmentTable.Row>(
            new TableVersionInfo("sale.installments", 43, RuleTierPath.Of(RawValue.From("t1")), new DateOnly(2026, 9, 30)),
            [.. InstallmentTable.DefaultRows.Select(r => r.MaxInstallments == 12 ? r with { MaxInstallments = 10 } : r)]));
        var card = S with { Payment = PaymentMethod.CreditCard };

        SaleRules.Evaluate(host, card).Max(SaleSituation.Fields.Installments).Should().Be(12);                       // dated 29/09
        SaleRules.Evaluate(host, card with { BusinessDate = new DateOnly(2026, 9, 30) }).Max(SaleSituation.Fields.Installments).Should().Be(10);
    }
}
