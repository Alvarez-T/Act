using YFex.Cqrs;
using YFex.Messaging;
using static YFex.Rules.Admin.RuleAdmin;
using static YFex.Rules.Cells;

namespace YFex.Rules.Admin.Tests;

/// <summary>4b publish pipeline (05 §4): permissions, validation with cell paths, optimistic concurrency, apply, notify, history.</summary>
public class PublishTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static Commands.PublishTable Table(long baseVersion, params SeatTable.Row[] rows)
        => new("pricing", "pricing.seats", AdminFixture.Tenant, baseVersion, Today, AdminFixture.Rows(rows), "raise Pro");

    [Fact]
    public async Task Publishing_a_table_version_applies_it_and_notifies_clients()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        var notified = new List<Events.RulesetPublished>();
        using var sub = f.Events.SubscribeAsync(new Recipient<Events.RulesetPublished>(notified.Add));

        PricingRules.Evaluate(f.Host, AdminFixture.Situation(seats: 80)).Verdict.IsDenied.Should().BeTrue();   // code rows: Pro ≤ 50

        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Basic), 5), new(Is(Plan.Pro), 100)));

        r.TryGetValue(out long version).Should().BeTrue();
        version.Should().Be(1);
        PricingRules.Evaluate(f.Host, AdminFixture.Situation(seats: 80)).Verdict.IsAllowed.Should().BeTrue();   // v1 in force
        notified.Should().ContainSingle().Which.Version.Should().Be(1);
        (await f.Store.GetVersionsAsync(new("pricing", RuleDataKind.Table, "pricing.seats", RuleTierPaths.Parse(AdminFixture.Tenant)), default))
            .Single().PublishedBy.Should().Be("ana");
    }

    [Fact]
    public async Task Missing_permission_is_unauthorized()
    {
        var f = new AdminFixture();
        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Pro), 100)));
        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Guardrail_violations_come_back_with_cell_paths()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Basic), 5), new(Is(Plan.Pro), 900)));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        e.Violations[0].Path.ToString().Should().Be("Rows[1].MaxSeats");
        e.Violations[0].Code.Value.Should().Be("pricing.seats.above-legal");
    }

    [Fact]
    public async Task Stale_base_version_is_a_conflict()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        (await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Pro), 60)))).TryGetValue(out long _).Should().BeTrue();

        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Pro), 70)));   // editor opened v0
        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Store_layer_is_rejected_for_a_tenant_only_table()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Pro), 60)) with { TierPath = AdminFixture.Store1 });
        r.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Code.Value.Should().Be(RulePublishCodes.NotEditable);
    }

    [Fact]
    public async Task Parameters_are_guarded_at_publish_and_applied()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("rules.parameters.publish");

        var tooHigh = await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(
            new("pricing", "pricing.volume", AdminFixture.Tenant, 0, Today, AdminFixture.Json("""{"rate": 35}"""), null));
        tooHigh.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Path.ToString().Should().Be("Values.rate");
        e.Violations[0].Code.Value.Should().Be(RulePublishCodes.Guard);

        var ok = await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(
            new("pricing", "pricing.volume", AdminFixture.Tenant, 0, Today, AdminFixture.Json("""{"rate": 10}"""), null));
        ok.TryGetValue(out long _).Should().BeTrue();
        PricingRules.Evaluate(f.Host, AdminFixture.Situation()).Totals.Final.As<Money>().Should().Be(new Money(900));
    }

    [Fact]
    public async Task Sealed_rules_cannot_be_overridden_others_can()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("rules.override");

        var sealedRule = await f.Dispatcher.CommandAsync<Commands.SetRuleOverride, long>(
            new("pricing", "pricing.min-seats", AdminFixture.Tenant, 0, Today, "disable", null, null));
        sealedRule.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Code.Value.Should().Be(RuleEngineCodes.Sealed);

        (await f.Dispatcher.CommandAsync<Commands.SetRuleOverride, long>(
            new("pricing", "pricing.volume", AdminFixture.Tenant, 0, Today, "disable", null, null))).TryGetValue(out long _).Should().BeTrue();
        PricingRules.Evaluate(f.Host, AdminFixture.Situation()).Totals.Final.As<Money>().Should().Be(new Money(1000));
    }

    [Fact]
    public async Task History_and_rule_definition_as_applied()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Basic), 5), new(Is(Plan.Pro), 100)));

        var d = PricingRules.Evaluate(f.Host, AdminFixture.Situation(seats: 120));
        var applied = d.Violations[0];                                            // Seats 120 > 100 from the published row
        applied.Code.Value.Should().Be("rules.limit.max");
        var withTrace = PricingRules.Evaluate(f.Host, AdminFixture.Situation(seats: 120), new() { Trace = TraceLevel.Applied });
        var rowRef = withTrace.Trace!.Entries.ToArray().Single(e => e.Rule.Code == "pricing.seats").Rule;
        rowRef.DataVersion.Should().Be(1);

        var view = await f.Dispatcher.QueryAsync<Queries.GetRuleDefinition, RuleDefinitionView>(
            new("pricing", rowRef.Code, rowRef.Version, rowRef.Tier, rowRef.DataVersion, rowRef.RowId));
        view.TryGetValue(out RuleDefinitionView? v).Should().BeTrue();
        v!.IsTable.Should().BeTrue();
        v.DataJson.Should().Contain("\"MaxSeats\":100").And.Contain(rowRef.RowId.ToString());
        v.PublishedBy.Should().Be("ana");

        var history = await f.Dispatcher.QueryAsync<Queries.GetDataVersions, RuleDataHeader[]>(new("pricing", RuleDataKind.Table, "pricing.seats", AdminFixture.Tenant));
        history.TryGetValue(out RuleDataHeader[]? headers).Should().BeTrue();
        headers!.Single().Note.Should().Be("raise Pro");
    }

    [Fact]
    public async Task Client_sync_warms_up_and_follows_published_versions()
    {
        var server = new AdminFixture();
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await server.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));

        // Client = another host fed through the same dispatcher (in production: the RPC client dispatcher).
        var client = PricingRules.CreateHost();
        var sync = new RuleSetSync(server.Dispatcher, new RuleSetRegistry([client]), server.Events);
        await sync.StartAsync();
        PricingRules.Evaluate(client, AdminFixture.Situation(seats: 55)).Verdict.IsAllowed.Should().BeTrue();   // v1 (60) loaded

        await server.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(1, new SeatTable.Row(Is(Plan.Pro), 40)));
        PricingRules.Evaluate(client, AdminFixture.Situation(seats: 55)).Verdict.IsDenied.Should().BeTrue();    // v2 (40) followed
        sync.Failures.Should().BeEmpty();
    }

    private sealed class Recipient<T>(Action<T> onEvent) : IAsyncEventRecipient<T>
    {
        public ValueTask ReceiveAsync(T e, CancellationToken ct = default) { onEvent(e); return ValueTask.CompletedTask; }
    }
}
