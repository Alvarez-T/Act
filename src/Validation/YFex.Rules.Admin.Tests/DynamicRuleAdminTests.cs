using YFex.Cqrs;
using static YFex.Rules.Admin.RuleAdmin;

namespace YFex.Rules.Admin.Tests;

/// <summary>Phase 6: runtime-authored rules through the RuleAdmin pipeline (permission, validation, store, apply, history, what-if).</summary>
public class DynamicRuleAdminTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private const string Pro10 = """
        { "code": "t1.pro-10", "group": "Promos",
          "when": { "==": [ { "var": "Plan" }, "Pro" ] },
          "then": [ { "decrease": { "target": "total", "percent": 10 } } ],
          "messages": { "": { "name": "Pro 10% {0}" } } }
        """;

    private static Commands.PublishRule Rule(string json, long baseVersion = 0)
        => new("pricing", "t1.pro-10", AdminFixture.Tenant, baseVersion, Today, AdminFixture.Json(json), "pro promo");

    [Fact]
    public async Task Publishing_a_runtime_rule_needs_the_permission_then_applies_it()
    {
        var f = new AdminFixture();
        (await f.Dispatcher.CommandAsync<Commands.PublishRule, long>(Rule(Pro10))).TryGetError(out var denied).Should().BeTrue();
        denied.Kind.Should().Be(ErrorKind.Forbidden);

        f.User.Permissions.Add("rules.dynamic.publish");
        var r = await f.Dispatcher.CommandAsync<Commands.PublishRule, long>(Rule(Pro10));
        r.TryGetValue(out long version).Should().BeTrue(r.TryGetError(out var e) ? e.ToString() : "");

        var d = PricingRules.Evaluate(f.Host, AdminFixture.Situation(Plan.Pro, seats: 3));
        d.Totals.Final.As<Money>().Should().Be(new Money(900));
        d.Adjustments.AsSpan()[0].Rule.DataVersion.Should().Be(version);
    }

    [Fact]
    public async Task Invalid_specs_come_back_as_violations_with_json_paths()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("rules.dynamic.publish");
        var r = await f.Dispatcher.CommandAsync<Commands.PublishRule, long>(Rule(Pro10.Replace("\"percent\": 10", "\"percent\": 40")));

        r.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Code.Value.Should().Be("rules.dynamic.over-cap");
        e.Violations[0].Path.ToString().Should().Be("then[0].decrease.percent");
    }

    [Fact]
    public async Task Rule_as_applied_returns_the_spec_of_that_version_and_what_if_works_for_rules()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("rules.dynamic.publish");
        (await f.Dispatcher.CommandAsync<Commands.PublishRule, long>(Rule(Pro10))).TryGetValue(out long v1).Should().BeTrue();

        var view = await f.Dispatcher.QueryAsync<Queries.GetRuleDefinition, RuleDefinitionView>(new("pricing", "t1.pro-10", 1, 1, v1, Guid.Empty));
        view.TryGetValue(out RuleDefinitionView? def).Should().BeTrue();
        def!.Group.Should().Be("Promos");
        def.DataJson.Should().Contain("\"percent\":10");
        def.Note.Should().Be("pro promo");

        f.Samples.Add(AdminFixture.Situation(Plan.Pro, 3), AdminFixture.Situation(Plan.Basic, 3));
        var sim = await f.Dispatcher.QueryAsync<Queries.Simulate, SimulationResult>(new("pricing", RuleDataKind.Rule, "t1.pro-10",
            AdminFixture.Tenant, AdminFixture.Json(Pro10.Replace("\"percent\": 10", "\"percent\": 15")), Today, Today));
        sim.TryGetValue(out SimulationResult? report).Should().BeTrue(sim.TryGetError(out var se) ? se.ToString() : "");
        report!.Changed.Should().Be(1);                                      // only the Pro sale: 10% → 15%
        report.FinalDelta.Should().Be(-50m);
    }
}
