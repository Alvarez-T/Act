using System.Collections.Immutable;
using System.Text;
using static YFex.Rules.Dynamic.Tests.Shop;

namespace YFex.Rules.Dynamic.Tests;

/// <summary>Phase 6 — runtime-authored rules (05 §8): JSON spec → validate → compile once → evaluate like code rules.</summary>
public class DynamicRuleTests
{
    private const string PixBonus = """
        { "code": "tenant.pix-bonus", "group": "Promo", "priority": 10,
          "when": { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] },
          "then": [ { "decrease": { "target": "total", "percent": 3 } } ],
          "messages": { "": { "name": "Pix bonus {0}" }, "pt": { "name": "Bônus Pix {0}" } } }
        """;

    [Fact]
    public void A_published_rule_runs_in_its_group_like_a_code_rule()
    {
        var (host, _) = Host();
        Publish(host, PixBonus);

        var d = host.Evaluate(Sale(PayMethod.Pix));                       // 6×10 + 900 = 960
        d.Totals.Final.As<Money>().Should().Be(new Money(931.20m));          // −3%
        d.Adjustments.AsSpan()[0].Rule.Code.Should().Be("tenant.pix-bonus");
        d.Adjustments.AsSpan()[0].Rule.Tier.Should().Be(1);                  // published at the tenant tier

        host.Evaluate(Sale(PayMethod.Cash)).Totals.Final.As<Money>().Should().Be(new Money(960));
    }

    [Fact]
    public void Trace_and_explanation_use_the_spec_messages_and_the_failing_condition()
    {
        var (host, dynamic) = Host();
        Publish(host, PixBonus);
        var catalog = Catalog(dynamic);

        var applied = host.Evaluate(Sale(PayMethod.Pix)).Explain(catalog, En);
        applied.Applied.Should().ContainSingle().Which.Title.Should().StartWith("Pix bonus");

        var small = host.Evaluate(Sale(PayMethod.Pix, lines: [Beer]), new() { Trace = TraceLevel.Full }).Explain(catalog, En);
        small.NotApplied.Should().Contain(l => l.Rule.Code == "tenant.pix-bonus" && l.Detail!.Contains("Total"));
    }

    [Fact]
    public void Line_rules_run_per_line_and_target_the_line()
    {
        var (host, _) = Host();
        Publish(host, """
            { "code": "tenant.six-pack", "group": "LineDisc",
              "when": { "and": [ { "==": [ { "line": "Category" }, "drinks" ] }, { ">=": [ { "line": "Quantity" }, 6 ] } ] },
              "then": [ { "decrease": { "target": "line", "percent": 10 } } ] }
            """);

        var d = host.Evaluate(Sale());
        d.Adjustments.Count.Should().Be(1);
        d.Adjustments.AsSpan()[0].LineIndex.Should().Be(0);                  // the beer line only
        d.Totals.Final.As<Money>().Should().Be(new Money(954));              // 60 − 6 + 900
    }

    [Fact]
    public void Arithmetic_functions_and_aggregates()
    {
        var (host, _) = Host();
        // Installments ≤ number of lines + 2; promo = min(5% of Total, 30) — Money arithmetic with a coerced literal.
        Publish(host, """
            { "code": "tenant.max-inst", "group": "Installments",
              "then": [ { "limit": { "field": "Installments", "max": { "+": [ { "count": { ">": [ { "line": "Quantity" }, 0 ] } }, 2 ] } } } ] }
            """);
        Publish(host, """
            { "code": "tenant.capped", "group": "Promo",
              "when": { ">": [ { "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }, 100 ] },
              "then": [ { "decrease": { "target": "total", "amount": { "min": [ { "percentOf": [ { "var": "Total" }, 5 ] }, 30 ] } } } ] }
            """);

        var d = host.Evaluate(Sale(installments: 5));
        d.Verdict.IsDenied.Should().BeTrue();                                // 5 > 2 lines + 2
        d.TryGetMax(ShopSituation.Fields.Installments, out int max).Should().BeTrue();
        max.Should().Be(4);
        d.Totals.Final.As<Money>().Should().Be(new Money(930));              // min(48, 30) = 30 off
    }

    [Fact]
    public void Runtime_values_above_the_group_cap_are_clamped()
    {
        var (host, _) = Host();
        Publish(host, """
            { "code": "tenant.big", "group": "Promo", "then": [ { "decrease": { "target": "total", "percent": { "*": [ 5, 4 ] } } } ] }
            """);                                                            // 20% computed → cap 10%
        host.Evaluate(Sale()).Totals.Final.As<Money>().Should().Be(new Money(864));
    }

    [Fact]
    public void Publish_checks_report_json_paths()
    {
        var (host, _) = Host();
        string[] Codes(string json) => host.ValidateRule("a", Tenant, Json(json))
            .Violations.AsSpan().ToArray().Select(v => $"{v.Path}:{v.Code.Value}").ToArray();

        Codes("""{ "code": "a", "group": "Legal", "then": [ { "warn": {} } ] }""")
            .Should().Contain("group:rules.dynamic.group-closed");
        Codes("""{ "code": "a", "group": "LineDisc", "then": [ { "deny": { "code": "x" } } ] }""")
            .Should().Contain("then[0]:rules.dynamic.directive-not-allowed");
        Codes("""{ "code": "a", "group": "LineDisc", "then": [ { "decrease": { "target": "line", "percent": 25 } } ] }""")
            .Should().Contain("then[0].decrease.percent:rules.dynamic.over-cap");
        Codes("""{ "code": "a", "group": "Promo", "when": { ">": [ { "var": "Payment" }, 5 ] }, "then": [ { "warn": {} } ] }""")
            .Should().Contain(c => c.EndsWith("rules.dynamic.literal") || c.EndsWith("rules.dynamic.type"));
        Codes("""{ "code": "a", "group": "Promo", "when": { "==": [ { "var": "Payment" }, "Bitcoin" ] }, "then": [ { "warn": {} } ] }""")
            .Should().Contain("when.==[1]:rules.dynamic.literal");
        Codes("""{ "code": "a", "group": "Promo", "when": { "isSet": { "var": "Nope" } }, "then": [ { "warn": {} } ] }""")
            .Should().Contain("when.isSet:rules.dynamic.unknown-field");
        Codes("""{ "code": "a", "group": "Promo", "when": { "gold": [ 1 ] }, "then": [ { "warn": {} } ] }""")
            .Should().Contain("when.gold:rules.dynamic.unknown-function");
        Codes("""{ "code": "a", "group": "Promo", "when": { "==": [ { "abs": [ "x" ] }, 1 ] }, "then": [ { "warn": {} } ] }""")
            .Should().Contain(c => c.Contains("abs") && c.EndsWith("rules.dynamic.type"));
        Codes("""{ "code": "a", "group": "Promo", "when": { "==": [ { "+": [ { "var": "Total" }, { "var": "Installments" } ] }, 1 ] }, "then": [ { "warn": {} } ] }""")
            .Should().Contain(c => c.EndsWith("rules.dynamic.type"));                // Money + int
        Codes("""{ "code": "a", "group": "Promo", "then": [ { "warn": {} }""")
            .Should().ContainSingle().Which.Should().EndWith("rules.dynamic.malformed");
    }

    [Fact]
    public void Tiers_follow_the_group_policy_and_overrides_work_on_runtime_rules()
    {
        var (host, _) = Host();
        const string StoreLine = """{ "code": "s.line", "group": "LineDisc", "then": [ { "decrease": { "target": "line", "percent": 5 } } ] }""";
        host.ValidateRule("s.line", Store, Json(StoreLine)).Violations.AsSpan()[0].Code.Value.Should().Be(DynamicRuleCodes.TierNotAllowed);

        Publish(host, PixBonus);
        host.Evaluate(Sale()).Adjustments.Count.Should().Be(1);
        host.Apply(new RuleDataRecord(new RuleDataInfo(new("shop", RuleDataKind.Override, "tenant.pix-bonus", Store), 999, DateOnly.MinValue, null, default, null),
            RuleDataCodec.WriteOverride(RuleOverrideAction.Disable)));
        host.Evaluate(Sale()).Adjustments.Count.Should().Be(0);               // the store switched the tenant's rule off
    }

    [Fact]
    public void Effective_dates_select_the_version_in_force()
    {
        var (host, _) = Host();
        Publish(host, PixBonus, from: new DateOnly(2026, 10, 1));            // not yet
        host.Evaluate(Sale()).Adjustments.Count.Should().Be(0);
        host.Evaluate(Sale() with { Date = new DateOnly(2026, 10, 2) }).Adjustments.Count.Should().Be(1);
    }

    [Fact]
    public void App_functions_and_contracts()
    {
        // A domain contract declared by a library: isWeekend(date) → bool. Rules may reference it…
        var contract = RuleFunction.Contract("isWeekend", DynType.Bool, [new("date", DynType.Date)], "True on Saturday and Sunday.");
        var (declared, _) = Host(FunctionCatalog.BuiltIn.With(contract));
        const string Weekend = """
            { "code": "tenant.weekend", "group": "Promo", "when": { "isWeekend": [ { "var": "$effectiveDate" } ] },
              "then": [ { "decrease": { "target": "total", "percent": 2 } } ] }
            """;
        declared.ValidateRule("tenant.weekend", Tenant, Json(Weekend)).Violations.AsSpan()[0].Code.Value.Should().Be(DynamicRuleCodes.Unimplemented);

        // …and the domain app implements it.
        var implemented = FunctionCatalog.BuiltIn.With(contract).Implement("isWeekend",
            static a => RawValue.From(BuiltInFunctions.Date(a[0]).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
        var (host, _) = Host(implemented);
        Publish(host, Weekend);
        host.Evaluate(Sale() with { Date = new DateOnly(2026, 10, 3) }).Adjustments.Count.Should().Be(1);   // Saturday
        host.Evaluate(Sale()).Adjustments.Count.Should().Be(0);                                            // Wednesday
        implemented.Unimplemented.Should().BeEmpty();
    }

    [Fact]
    public void A_version_compiles_once_and_specs_round_trip_canonically()
    {
        var (host, dynamic) = Host();
        Publish(host, PixBonus);
        host.Evaluate(Sale());
        host.Evaluate(Sale() with { Date = Today.AddDays(1) });              // another compiled set, same rule
        var record = host.Data.Snapshots[0];
        dynamic.CompileCount.Should().Be(1);

        var canonical = RuleSpecJson.Write(RuleSpecJson.Read(Json(PixBonus)));
        RuleSpecJson.Write(RuleSpecJson.Read(canonical)).Should().Equal(canonical);
        Encoding.UTF8.GetString(canonical).Should().StartWith("{\"code\":\"tenant.pix-bonus\",\"group\":\"Promo\",\"priority\":10,");
    }

    [Fact]
    public void Expression_text_is_readable()
    {
        var spec = RuleSpecJson.Read(Json(PixBonus));
        ExprText.Render(spec.When!).Should().Be("Payment = \"Pix\" and Total ≥ 500");
    }

    [Fact]
    public void Null_safe_and_total_division_by_zero_is_null()
    {
        var (host, _) = Host();
        Publish(host, """
            { "code": "tenant.div", "group": "Promo",
              "when": { "isSet": { "/": [ { "var": "Total" }, 0 ] } },
              "then": [ { "warn": { "code": "never" } } ] }
            """);
        host.Evaluate(Sale()).Violations.Count.Should().Be(0);
    }
}
