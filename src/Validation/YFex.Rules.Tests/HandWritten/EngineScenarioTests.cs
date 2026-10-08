using System.Collections.Immutable;
using System.Globalization;
using YFex.Collections;
using YFex.Localization;

namespace YFex.Rules.Tests.HandWritten;

/// <summary>Doc 12 sale scenario + 04 §13 engine properties, on the hand-written (generator-shaped) rule set.</summary>
public class EngineScenarioTests
{
    private static HSale S => HSaleBuilder.Default;
    private static Percentual Pct(decimal v) => Percentual.FromPercentage(v);

    [Fact]
    public void Default_sale_applies_the_line_promo_and_is_allowed()
    {
        var d = HandSaleRules.Evaluate(S);

        d.Verdict.Kind.Should().Be(VerdictKind.Allowed);
        d.Totals.Initial.As<Money>().Should().Be(new Money(1200));
        d.Totals.Final.As<Money>().Should().Be(new Money(1190));
        d.Adjustments.Count.Should().Be(1);
        d.Adjustments[0].Rule.Code.Should().Be("promo.l3p2");
        d.Adjustments[0].LineIndex.Should().Be(0);
        d.Adjustments[0].Amount.As<Money>().Should().Be(new Money(10));
        d.Totals.Lines[0].Final.As<Money>().Should().Be(new Money(20));
    }

    [Fact]
    public void Gold_and_birthday_stack_and_are_allocated_to_lines_exactly()
    {
        var d = HandSaleRules.Evaluate(S with { Tier = CustomerTier.Gold, IsBirthdayMonth = true });

        var loyalty = d.Adjustments.AsSpan().ToArray().Where(a => a.Stage.Code == "subtotal").ToArray();
        loyalty.Select(a => a.Rule.Code).Should().Equal("loyalty.gold", "loyalty.bday");   // priority 100 first
        loyalty[0].Amount.As<Money>().Should().Be(new Money(59.50m));                               // 5% of 1190 (stack base)
        loyalty[1].Amount.As<Money>().Should().Be(new Money(35.70m));                               // 3% of 1190 (stack base, not compounded)
        d.Totals.Final.As<Money>().Should().Be(new Money(1094.80m));

        foreach (var a in loyalty)
            a.Allocations.ToArray().Sum(x => x.Amount.As<Money>().Amount).Should().Be(a.Amount.As<Money>().Amount);
        d.Totals.Lines.ToArray().Sum(l => l.Final.As<Money>().Amount).Should().Be(d.Totals.Final.As<Money>().Amount);
        loyalty[0].Allocations.ToArray().Select(x => x.Amount.As<Money>()).Should().Equal(new Money(1), new Money(55), new Money(3.5m));
    }

    [Fact]
    public void Stack_cap_limits_the_total_and_traces_the_capped_rule()
    {
        var items = Stage.First("items");
        var group = new RuleGroup(items, Resolve.Stack(cap: Pct(5)));
        var set = new RuleSetDefinitionBuilder<HSale>("cap-test", 1)
            .Group("G", group)
            .Amount<Money>()
            .LineBase(static (in HSale s, int i) => Amounts.ToDecimal(s.Lines[i].UnitPrice * s.Lines[i].Quantity))
            .Rule(RuleDescriptor<HSale>.Create("a", "G", static (in HSale s, in RuleContext c) => true,
                static (in HSale s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, (Percentual.FromPercentage(3))), priority: 2))
            .Rule(RuleDescriptor<HSale>.Create("b", "G", static (in HSale s, in RuleContext c) => true,
                static (in HSale s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, (Percentual.FromPercentage(3))), priority: 1))
            .Build();

        var d = set.Evaluate(S, new() { Trace = TraceLevel.Full });

        d.Adjustments[0].Amount.As<Money>().Should().Be(new Money(36));        // 3% of 1200
        d.Adjustments[1].Amount.As<Money>().Should().Be(new Money(24));        // capped: 5% of 1200 = 60 − 36
        d.Adjustments[1].Note.Kind.Should().Be(ResolutionNoteKind.CappedBy);
        d.Trace!.For("b").Single().Outcome.Kind.Should().Be(TraceOutcomeKind.Capped);
    }

    [Fact]
    public void Exclusive_promo_keeps_the_best_per_line_and_supersedes_the_other()
    {
        var d = HandSaleRules.Evaluate(S with { BusinessDate = new DateOnly(2026, 11, 27) }, new() { Trace = TraceLevel.Full });

        var items = d.Adjustments.AsSpan().ToArray().Where(a => a.Stage.Code == "items").ToArray();
        items.Select(a => (a.Rule.Code, a.LineIndex, a.Amount.As<Money>())).Should().Equal(
            ("promo.l3p2", 0, new Money(10)),            // 10.00 beats Black Friday's 3.00 on the coca line
            ("promo.blackfriday", 1, new Money(110)),
            ("promo.blackfriday", 2, new Money(7)));
        var superseded = d.Trace!.For("promo.blackfriday").Single(e => e.LineIndex == 0);
        superseded.Outcome.TryGetValue(out Superseded s).Should().BeTrue();
        s.By.Code.Should().Be("promo.l3p2");
    }

    [Fact]
    public void Pix_limits_installments_to_one_and_gives_its_discount()
    {
        var d = HandSaleRules.Evaluate(S with { Payment = PaymentMethod.Pix, Installments = 3 });

        d.Verdict.IsDenied.Should().BeTrue();
        d.Violations.Contains("rules.limit.max").Should().BeTrue();
        d.Violations[0].Path.ToString().Should().Be("Installments");
        d.Max(HSale.Fields.Installments).Should().Be(1);
        d.Adjustments.AsSpan().ToArray().Should().Contain(a => a.Rule.Code == "payment.pix" && a.Amount.As<Money>() == new Money(23.80m));
    }

    [Fact]
    public void Boleto_is_sealed_to_one_installment()
    {
        var d = HandSaleRules.Evaluate(S with { Payment = PaymentMethod.Boleto, Installments = 2 });
        d.Verdict.IsDenied.Should().BeTrue();
        d.ConstraintsFor(HSale.Fields.Installments).Max.Should().Be(RawValue.From(1));
    }

    [Fact]
    public void Seller_above_limit_needs_approval_manager_does_not()
    {
        var seller = HandSaleRules.Evaluate(S with { ManualDiscount = Pct(8) });
        seller.Verdict.IsNeedsApproval.Should().BeTrue();
        seller.Approvals.Requires(RulesTestPermissions.DiscountOverride).Should().BeTrue();
        seller.Violations[0].Severity.Should().Be(YFex.Validation.Severity.Warning);
        seller.Adjustments.AsSpan().ToArray().Should().Contain(a => a.Rule.Code == "sale.manual" && a.Amount.As<Money>() == new Money(95.20m));

        HandSaleRules.Evaluate(S with { ManualDiscount = Pct(8), Actor = ActorLevel.Manager }).Verdict.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Credit_card_table_row_and_min_installment_value()
    {
        var ok = HandSaleRules.Evaluate(S with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold, Installments = 12 });
        ok.Verdict.IsAllowed.Should().BeTrue();
        ok.Max(HSale.Fields.Installments).Should().Be(18);                                // Gold row
        ok.DerivedValue(HandSaleRules.InstallmentValue).Should().Be(RawValue.From(new Money(94.21m)));

        var tooMany = HandSaleRules.Evaluate(S with { Payment = PaymentMethod.CreditCard, Tier = CustomerTier.Gold, Installments = 18 });
        tooMany.Verdict.IsDenied.Should().BeTrue();                                          // 1130.50 / 18 = 62.81 < 80
        tooMany.Violations[0].Code.Value.Should().Be("rules.limit.min");
        tooMany.Violations[0].Path.ToString().Should().Be("InstallmentValue");
    }

    [Fact]
    public void Fingerprint_is_deterministic_and_ignores_the_trace()
    {
        var s = S with { Tier = CustomerTier.Gold, Payment = PaymentMethod.CreditCard, Installments = 6 };
        ulong first = HandSaleRules.Evaluate(s).Fingerprint;
        for (int i = 0; i < 1000; i++) HandSaleRules.Evaluate(s).Fingerprint.Should().Be(first);
        HandSaleRules.Evaluate(s, new() { Trace = TraceLevel.Full }).Fingerprint.Should().Be(first);
        HandSaleRules.Evaluate(s with { Installments = 7 }).Fingerprint.Should().NotBe(first);
        HandSaleRules.Evaluate(s).Should().Be(HandSaleRules.Evaluate(s));                   // equality = fingerprint
    }

    [Fact]
    public void Full_trace_explains_why_not()
    {
        var d = HandSaleRules.Evaluate(S, new() { Trace = TraceLevel.Full });

        var pix = d.Trace!.For("payment.pix").Single();
        pix.Outcome.TryGetValue(out NotApplicable na).Should().BeTrue();
        na.Failed.Index.Should().Be(0);

        var catalog = new MessageCatalog(RuleMessages.Instance);
        var e = d.Explain(catalog, CultureInfo.InvariantCulture);
        e.Applied.Should().ContainSingle().Which.Rule.Code.Should().Be("promo.l3p2");
        e.NotApplied.Should().Contain(l => l.Rule.Code == "payment.pix" && l.Detail!.StartsWith("requires"));
        e.Final.As<Money>().Should().Be(new Money(1190));
        e.Limits.Should().Contain(l => l.Title == "Installments");
    }

    [Fact]
    public void Situation_round_trips_and_hash_is_stable()
    {
        var s = S with { Tier = CustomerTier.Gold, ManualDiscount = Pct(2.5m) };
        using var buffer = new ArrayPoolBufferWriter<byte>();
        Situation.WriteTo(in s, buffer);
        var back = Situation.ReadFrom<HSale>(buffer.WrittenSpan);

        back.Lines.AsSpan().SequenceEqual(s.Lines.AsSpan()).Should().BeTrue();
        (back with { Lines = s.Lines }).Should().Be(s);
        Situation.Hash(in back).Should().Be(Situation.Hash(in s));
        HSale.DiffMask(in s, in back).Should().Be(0);
        HSale.DiffMask(in s, s with { Payment = PaymentMethod.Pix }).Should().Be(1UL << 7);
    }

    [Fact]
    public void Allocation_None_keeps_subtotal_adjustments_off_the_lines()
    {
        var subtotal = Stage.First("subtotal");
        var set = new RuleSetDefinitionBuilder<HSale>("no-alloc", 1)
            .Group("G", new RuleGroup(subtotal, Resolve.All))
            .Allocation(Allocation.None)
            .Amount<Money>()
            .LineBase(static (in HSale s, int i) => Amounts.ToDecimal(s.Lines[i].UnitPrice * s.Lines[i].Quantity))
            .Rule(RuleDescriptor<HSale>.Create("x", "G", static (in HSale s, in RuleContext c) => true,
                static (in HSale s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, (new Money(100)))))
            .Build();

        var d = set.Evaluate(S);
        d.Totals.Final.As<Money>().Should().Be(new Money(1100));
        d.Adjustments[0].Allocations.IsEmpty.Should().BeTrue();
        d.Totals.Lines.ToArray().Sum(l => l.AllocatedChange.As<Money>().Amount).Should().Be(0);
    }

    [Fact]
    public void Allocation_remainder_goes_to_the_largest_line()
    {
        var subtotal = Stage.First("subtotal");
        var set = new RuleSetDefinitionBuilder<HSale>("remainder", 1)
            .Group("G", new RuleGroup(subtotal, Resolve.All))
            .Amount<Money>()
            .LineBase(static (in HSale s, int i) => Amounts.ToDecimal(s.Lines[i].UnitPrice * s.Lines[i].Quantity))
            .Rule(RuleDescriptor<HSale>.Create("x", "G", static (in HSale s, in RuleContext c) => true,
                static (in HSale s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, (new Money(10.01m)))))
            .Build();
        var three = S with { Lines = [new("a", "c", 1, new Money(100)), new("b", "c", 1, new Money(50)), new("c", "c", 1, new Money(50))] };

        var alloc = set.Evaluate(three).Adjustments[0].Allocations.ToArray().Select(a => a.Amount.As<Money>()).ToArray();
        alloc.Should().Equal(new Money(5.01m), new Money(2.50m), new Money(2.50m));
    }

    // ── Layers, versions, guardrails (04 §9–10) ────────────────────────────────

    private static readonly RawValue T1 = RawValue.From("t1");
    private static readonly RawValue S1 = RawValue.From("s1");

    [Fact]
    public void As_of_selects_the_table_version_in_force()
    {
        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        host.Publish(new TableVersion<InstallmentTable.Row>(
            new TableVersionInfo("sale.installments", 42, RuleTierPath.Of(T1), new DateOnly(2026, 10, 1)),
            [.. InstallmentTable.DefaultRows.ToArray().Select(r => r.MaxInstallments == 12 ? r with { MaxInstallments = 10 } : r)]));
        var card = S with { Payment = PaymentMethod.CreditCard, Installments = 1 };

        host.Evaluate(card).Max(HSale.Fields.Installments).Should().Be(12);                              // 29/09 → code rows
        var later = host.Evaluate(card with { BusinessDate = new DateOnly(2026, 10, 2) });
        later.Max(HSale.Fields.Installments).Should().Be(10);                                             // v42 in force
        later.RulesetVersion.DataVersion.Should().Be(42);
    }

    [Fact]
    public void Guard_clamps_parameter_overrides()
    {
        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        var gold = S with { Tier = CustomerTier.Gold };
        host.Publish(new ParameterSnapshot("loyalty.gold", RuleTierPath.Of(T1), 1, DateOnly.MinValue,
            ImmutableDictionary<string, object?>.Empty.Add("rate", Pct(10))));
        host.Evaluate(gold).Adjustments.AsSpan()[1].Amount.As<Money>().Should().Be(new Money(119));                 // 10% of 1190

        host.Publish(new ParameterSnapshot("loyalty.gold", RuleTierPath.Of(T1), 2, DateOnly.MinValue,
            ImmutableDictionary<string, object?>.Empty.Add("rate", Pct(25))));                            // above guard 15%
        var clamped = host.Evaluate(gold, new() { Trace = TraceLevel.Full });
        clamped.Adjustments.AsSpan()[1].Amount.As<Money>().Should().Be(new Money(178.50m));                         // clamped to 15%
        clamped.Trace!.For("loyalty.gold.rate").Should().ContainSingle(e => e.Outcome.Kind == TraceOutcomeKind.Overridden);
        clamped.Adjustments.AsSpan()[1].Rule.DataVersion.Should().Be(2);
    }

    [Fact]
    public void Store_table_version_beats_the_tenant_one()
    {
        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        InstallmentTable.Row[] With(int max) => [.. InstallmentTable.DefaultRows.ToArray().Select(r => r.MaxInstallments == 12 ? r with { MaxInstallments = max } : r)];
        host.Publish(new TableVersion<InstallmentTable.Row>(new("sale.installments", 1, RuleTierPath.Of(T1), DateOnly.MinValue), [.. With(10)]));
        host.Publish(new TableVersion<InstallmentTable.Row>(new("sale.installments", 2, RuleTierPath.Of(T1, S1), DateOnly.MinValue), [.. With(8)]));
        var card = S with { Payment = PaymentMethod.CreditCard };

        host.Evaluate(card).Max(HSale.Fields.Installments).Should().Be(8);
        host.Evaluate(card with { Store = new StoreId("s2") }).Max(HSale.Fields.Installments).Should().Be(10);
        host.Evaluate(card with { Tenant = new TenantId("t2") }).Max(HSale.Fields.Installments).Should().Be(12);
    }

    [Fact]
    public void Parameters_are_ignored_at_layers_their_edit_scope_forbids()
    {
        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        host.Publish(new ParameterSnapshot("loyalty.gold", RuleTierPath.Of(T1, S1), 1, DateOnly.MinValue,
            ImmutableDictionary<string, object?>.Empty.Add("rate", Pct(1))));                             // rate is Tenant-only
        host.Evaluate(S with { Tier = CustomerTier.Gold }).Adjustments.AsSpan()[1].Amount.As<Money>().Should().Be(new Money(59.50m));
        host.Resolve(RuleTierPath.Of(T1, S1), S.BusinessDate).Notes.ToArray()
            .Should().Contain(n => n.Kind == CompileNoteKind.NotEditableAtTier);
    }

    [Fact]
    public void Sealed_rules_ignore_overrides_others_can_be_disabled()
    {
        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        host.Publish(new OverrideSnapshot("sale.installments.boleto", RuleTierPath.Of(T1), 1, DateOnly.MinValue, RuleOverrideAction.Disable));
        host.Publish(new OverrideSnapshot("payment.pix", RuleTierPath.Of(T1), 2, DateOnly.MinValue, RuleOverrideAction.Disable));

        host.Evaluate(S with { Payment = PaymentMethod.Boleto, Installments = 2 }).Verdict.IsDenied.Should().BeTrue();
        var pix = host.Evaluate(S with { Payment = PaymentMethod.Pix }, new() { Trace = TraceLevel.Full });
        pix.Adjustments.AsSpan().ToArray().Should().NotContain(a => a.Rule.Code == "payment.pix");
        pix.Trace!.For("payment.pix").Single().Outcome.TryGetValue(out Overridden o).Should().BeTrue();
        o.ByTier.Should().Be(1);

        host.Publish(new OverrideSnapshot("payment.pix", RuleTierPath.Of(T1, S1), 3, DateOnly.MinValue, RuleOverrideAction.Enable));
        host.Evaluate(S with { Payment = PaymentMethod.Pix }).Adjustments.AsSpan().ToArray().Should().Contain(a => a.Rule.Code == "payment.pix");
    }

    [Fact]
    public void Guardrail_rejected_rows_are_excluded_and_publish_check_reports_cells()
    {
        var rows = InstallmentTable.DefaultRows.ToArray();
        rows[1] = rows[1] with { MaxInstallments = 36 };                                     // CreditCard ≥ 1000: above legal 24
        var version = new TableVersion<InstallmentTable.Row>(
            new TableVersionInfo("sale.installments", 5, RuleTierPath.Of(T1), DateOnly.MinValue), [.. rows]);

        var host = new RuleSetHost<HSale>(HandSaleRules.Set);
        host.Publish(version);
        var d = host.Evaluate(S with { Payment = PaymentMethod.CreditCard });
        d.TryGetMax(HSale.Fields.Installments, out int _).Should().BeFalse();                  // row 1 excluded → no card row matches 1200/None
        host.Resolve(RuleTierPath.Of(T1), S.BusinessDate).Notes.ToArray().Should().Contain(n => n.Kind == CompileNoteKind.RowRejected);

        HandSaleRules.Set.TryGetTable("sale.installments", out var binding).Should().BeTrue();
        using var scope = YFex.Validation.ViolationWriter.Begin(out var w);
        binding.Check(version, ref w).Should().BeFalse();
        w.AsSpan().ToArray().Select(v => v.Path.ToString()).Should().Contain("Rows[1].MaxInstallments");
    }

    [Fact]
    public void Publish_check_flags_unreachable_rows()
    {
        var rows = InstallmentTable.DefaultRows.ToArray().ToList();
        rows.Insert(0, new(Cells.Is(PaymentMethod.CreditCard), Cells.Any, Cells.Any, 2, Money.Zero));   // shadows every card row
        HandSaleRules.Set.TryGetTable("sale.installments", out var binding);
        using var scope = YFex.Validation.ViolationWriter.Begin(out var w);
        binding.Check(new TableVersion<InstallmentTable.Row>(new("sale.installments", 1, RuleTierPath.Code, DateOnly.MinValue), [.. rows]), ref w)
            .Should().BeTrue();                                                               // warnings only
        w.AsSpan().ToArray().Where(v => v.Code.Value == "rules.table.unreachable").Select(v => v.Path.ToString())
            .Should().Equal("Rows[1]", "Rows[2]", "Rows[3]", "Rows[4]");
    }
}
