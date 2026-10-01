using System.Text.Json;
using static YFex.Rules.Dynamic.Tests.Shop;

namespace YFex.Rules.Dynamic.Tests;

/// <summary>Phase 6 — authoring catalog, rule-spec JSON Schema, situation JSON Schema with live limits, rule-set description.</summary>
public class AiSchemaTests
{
    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public void Authoring_catalog_lists_fields_functions_and_open_groups()
    {
        var (_, dynamic) = Host();
        var c = JsonDocument.Parse(dynamic.CatalogJson(ShopRules.Set)).RootElement;

        c.GetProperty("amount").GetString().Should().Be("money");
        var payment = c.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Payment");
        Strings(payment.GetProperty("options")).Should().Equal("Cash", "Card", "Pix");
        c.GetProperty("lineFields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Contain("Category");
        c.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Contain(["round", "percentOf", "daysBetween"]);

        var groups = c.GetProperty("groups").EnumerateArray().ToArray();
        groups.Select(g => g.GetProperty("name").GetString()).Should().Equal("LineDisc", "Promo", "Installments");   // Legal is closed
        var promo = groups[1];
        promo.GetProperty("maxPercent").GetDecimal().Should().Be(10);
        Strings(promo.GetProperty("editableAt")).Should().Equal("Tenant", "Store");
        Strings(promo.GetProperty("directives")).Should().Equal("deny", "warn", "decrease", "increase");
    }

    [Fact]
    public void Spec_json_schema_is_specialized_to_the_rule_set()
    {
        var (_, dynamic) = Host();
        var s = JsonDocument.Parse(dynamic.SpecJsonSchema(ShopRules.Set)).RootElement;

        Strings(s.GetProperty("properties").GetProperty("group").GetProperty("enum")).Should().Equal("LineDisc", "Promo", "Installments");
        var defs = s.GetProperty("$defs");
        Strings(defs.GetProperty("var").GetProperty("properties").GetProperty("var").GetProperty("enum"))
            .Should().Contain(["Payment", "Total", "$base", "$effectiveDate"]).And.NotContain("Lines");
        Strings(defs.GetProperty("line").GetProperty("properties").GetProperty("line").GetProperty("enum")).Should().Contain("UnitPrice");
        defs.GetProperty("call").GetProperty("properties").GetProperty("round").GetProperty("maxItems").GetInt32().Should().Be(2);
        defs.GetProperty("directive").GetProperty("oneOf").EnumerateArray()
            .Select(d => d.GetProperty("required")[0].GetString()).Should().Equal("deny", "warn", "limit", "grant", "decrease", "increase", "approval");
    }

    [Fact]
    public void Situation_schema_carries_the_current_decision_limits()
    {
        var (host, _) = Host();
        Publish(host, """
            { "code": "tenant.max-inst", "group": "Installments",
              "then": [ { "limit": { "field": "Installments", "max": 4 } }, { "grant": { "field": "Payment", "options": ["Pix", "Card"] } } ] }
            """);
        var decision = host.Evaluate(Sale());
        var s = JsonDocument.Parse(SituationJsonSchema.ToJson(decision)).RootElement.GetProperty("properties");

        s.GetProperty("Installments").GetProperty("maximum").GetInt32().Should().Be(4);
        Strings(s.GetProperty("Payment").GetProperty("enum")).Should().Equal("Card", "Pix");             // granted options only
        s.GetProperty("Total").GetProperty("x-type").GetString().Should().Be("money");
        s.GetProperty("Lines").GetProperty("items").GetProperty("properties").GetProperty("Quantity").GetProperty("type").GetString().Should().Be("integer");

        var plain = JsonDocument.Parse(SituationJsonSchema.ToJson<ShopSituation>()).RootElement.GetProperty("properties");
        plain.GetProperty("Installments").TryGetProperty("maximum", out _).Should().BeFalse();             // no decision → no live limit
    }

    [Fact]
    public void Rule_set_description_includes_runtime_rules_with_readable_conditions()
    {
        var (host, dynamic) = Host();
        Publish(host, """
            { "code": "tenant.pix-bonus", "group": "Promo", "when": { "==": [ { "var": "Payment" }, "Pix" ] },
              "then": [ { "decrease": { "target": "total", "percent": 3 } } ], "messages": { "": { "name": "Pix bonus" } } }
            """);
        var compiled = host.Resolve(Tenant, Today);
        var d = JsonDocument.Parse(RuleSetDescription.ToJson(ShopRules.Set, compiled, Catalog(dynamic), En)).RootElement;

        var promo = d.GetProperty("stages").EnumerateArray().SelectMany(st => st.GetProperty("groups").EnumerateArray())
            .Single(g => g.GetProperty("name").GetString() == "Promo");
        promo.GetProperty("runtimeRules").GetProperty("maxPercent").GetDecimal().Should().Be(10);
        var rule = promo.GetProperty("rules").EnumerateArray().Single();
        rule.GetProperty("kind").GetString().Should().Be("runtime");
        rule.GetProperty("tier").GetInt32().Should().Be(1);
        rule.GetProperty("name").GetString().Should().Be("Pix bonus");
        Strings(rule.GetProperty("conditions")).Single().Should().Contain("Payment").And.Contain("Pix");
    }
}
