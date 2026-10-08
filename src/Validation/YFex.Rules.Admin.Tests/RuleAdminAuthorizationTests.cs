using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Cqrs;
using static YFex.Rules.Admin.RuleAdmin;
using static YFex.Rules.Cells;

namespace YFex.Rules.Admin.Tests;

/// <summary>
/// Authorization-consolidation Phase 4a: RuleAdmin permissions are typed, catalog-checked and enforced by the message gate
/// (04 §2.5), so search/AI/UI see the publish actions only for holders and a denial names the missing permission.
/// </summary>
public class RuleAdminAuthorizationTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static Commands.PublishTable Table(params SeatTable.Row[] rows)
        => new("pricing", "pricing.seats", AdminFixture.Tenant, 0, Today, AdminFixture.Rows(rows), "raise Pro");

    private static Commands.PublishParameters Parameters()
        => new("pricing", "pricing.volume", AdminFixture.Tenant, 0, Today, AdminFixture.Json("""{ "rate": 7 }"""), "7%");

    [Fact]
    public async Task A_denied_publish_names_the_tables_own_permission()
    {
        var f = new AdminFixture();

        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(new SeatTable.Row(Is(Plan.Pro), 100)));

        r.TryGetError(out var e).Should().BeTrue();
        e.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Kind.Should().Be(DenialKind.MissingPermission);
        forbidden.Denial.Missing.Should().Be(PricingPermissions.SeatsPublish, "the table names its own publish permission");
    }

    [Fact]
    public void Publish_actions_are_visible_only_to_holders_of_their_permission()
    {
        var f = new AdminFixture();
        var gate = f.Services.GetRequiredService<IMessageAuthorization>();

        gate.IsVisible(typeof(Commands.PublishTable), f.User).Should().BeFalse();
        gate.IsVisible(typeof(Commands.PublishParameters), f.User).Should().BeFalse();

        f.User.Permissions.Add(PricingPermissions.Ids.SeatsPublish);
        gate.IsVisible(typeof(Commands.PublishTable), f.User).Should().BeTrue("the caller may publish at least one table");
        gate.IsVisible(typeof(Queries.Simulate), f.User).Should().BeTrue();
        gate.IsVisible(typeof(Commands.PublishParameters), f.User).Should().BeFalse("parameters need rules.parameters.publish");
        gate.IsVisible(typeof(Queries.GetRuleData), f.User).Should().BeTrue("no view permission configured");
    }

    [Fact]
    public async Task Options_remap_the_permission_a_message_needs()
    {
        var f = new AdminFixture(options: o =>
        {
            o.ParameterPublishPermission = PricingPermissions.SeatsPublish;
            o.ViewPermission = RuleAdminPermissions.View;
        });
        f.User.Permissions.Add(RuleAdminPermissions.ParametersPublish.Value);

        var denied = await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(Parameters());
        denied.TryGetError(out var e).Should().BeTrue();
        e.TryGetValue(out Forbidden forbidden).Should().BeTrue();
        forbidden.Denial.Missing.Should().Be(PricingPermissions.SeatsPublish);
        (await f.Dispatcher.QueryAsync<Queries.GetRuleData, RuleDataPayload[]>(new("pricing"))).TryGetError(out var view).Should().BeTrue();
        view.Kind.Should().Be(ErrorKind.Forbidden, "the view permission is configured");

        f.User.Permissions.Add(PricingPermissions.Ids.SeatsPublish);
        f.User.Permissions.Add(RuleAdminPermissions.View.Value);
        var ok = await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(Parameters());
        ok.TryGetValue(out long _).Should().BeTrue(ok.TryGetError(out var why) ? why.ToString() : "");
        (await f.Dispatcher.QueryAsync<Queries.GetRuleData, RuleDataPayload[]>(new("pricing"))).TryGetError(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_runtime_rule_naming_an_undeclared_permission_is_rejected_at_publish()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add(RuleAdminPermissions.RulesPublish.Value);
        const string spec = """
            { "code": "t1.pro-10", "group": "Promos",
              "when": { "==": [ { "var": "Plan" }, "Pro" ] },
              "then": [ { "approval": { "permission": "pricing.typo.override", "path": "Seats", "code": "t1.pro-10.approval" } } ] }
            """;

        var r = await f.Dispatcher.CommandAsync<Commands.PublishRule, long>(
            new("pricing", "t1.pro-10", AdminFixture.Tenant, 0, Today, AdminFixture.Json(spec), "typo"));

        r.TryGetError(out var e).Should().BeTrue();
        e.Violations.AsSpan().ToArray().Should().Contain(v => v.Code.Value == YFex.Rules.Dynamic.DynamicRuleCodes.UnknownPermission);
    }

    [Fact]
    public void An_undeclared_table_publish_permission_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddYFexRulesAdmin(o => o.TablePublishPermission = Permission.Parse("pricing.never.declared"));

        using var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<RuleAdminRequirements>())
            .Should().Throw<InvalidOperationException>().WithMessage("*pricing.never.declared*");
    }
}
