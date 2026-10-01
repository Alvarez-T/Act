using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Persistence;
using static YFex.Rules.Admin.RuleAdmin;
using static YFex.Rules.Cells;

namespace YFex.Rules.Admin.Tests;

/// <summary>4b completion: canonical payloads, audit + diffs, what-if simulation, device cache and incremental sync.</summary>
public class RuntimeEditingTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static Commands.PublishTable Table(long baseVersion, params SeatTable.Row[] rows)
        => new("pricing", "pricing.seats", AdminFixture.Tenant, baseVersion, Today, AdminFixture.Rows(rows), null);

    private static async Task<long> Publish(AdminFixture f, Commands.PublishTable c)
    {
        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(c);
        r.TryGetValue(out long v).Should().BeTrue(r.TryGetError(out var e) ? e.Message : "");
        return v;
    }

    // ── Canonical payloads ────────────────────────────────────────────────

    [Fact]
    public async Task Rows_sent_without_ids_get_one_stable_id_that_every_host_decodes_identically()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        var json = AdminFixture.Json("""{"rows":[{"cells":{"Plan":{"is":"Pro"},"MaxSeats":70}}]}""");   // a new editor row: no id

        await Publish(f, new("pricing", "pricing.seats", AdminFixture.Tenant, 0, Today, json, null));

        var stored = (await f.Store.LoadAsync("pricing", 0, default)).Single();
        Encoding.UTF8.GetString(stored.Payload.Span).Should().Contain("\"id\":");
        var client = PricingRules.CreateHost();
        client.Apply(stored);
        var serverRow = RowIdOf(f.Host);
        serverRow.Should().NotBe(Guid.Empty);
        RowIdOf(client).Should().Be(serverRow);                              // before: each decode invented a new id

        static Guid RowIdOf(RuleSetHost<PricingSituation> host)
            => PricingRules.Evaluate(host, AdminFixture.Situation(seats: 80), new() { Trace = TraceLevel.Applied })
                .Trace!.Entries.ToArray().Single(e => e.Rule.Code == "pricing.seats").Rule.RowId;
    }

    // ── Audit + diffs ─────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_sink_gets_the_diff_of_each_publish_and_every_rejection()
    {
        var sink = new RecordingAudit();
        var f = new AdminFixture(sink);
        f.User.Permissions.Add("pricing.rules.seats.publish");

        await Publish(f, Table(0, new SeatTable.Row(Is(Plan.Basic), 5), new(Is(Plan.Pro), 60)));
        var first = sink.Published.Single();
        first.Diff.FromVersion.Should().Be(0);                                  // vs the code rows (Basic 5, Pro 50)
        first.Diff.Rows.Should().NotBeEmpty();

        // v2 = v1's rows (same ids, read back from the store) with Pro 60 → 70 and a new Basic row moved to the top.
        var v1 = await f.Store.GetAsync(Key(), 1, default);
        var doc = Encoding.UTF8.GetString(v1!.Payload.Span).Replace("\"MaxSeats\":60", "\"MaxSeats\":70")
            .Replace("{\"rows\":[", "{\"rows\":[{\"cells\":{\"Plan\":{\"is\":\"Basic\"},\"MaxSeats\":3}},");
        await Publish(f, new("pricing", "pricing.seats", AdminFixture.Tenant, 1, Today, Encoding.UTF8.GetBytes(doc), "Pro +10"));

        var second = sink.Published[1].Diff;
        second.FromVersion.Should().Be(1);
        second.ToVersion.Should().Be(2);
        second.Rows.Should().HaveCount(2);
        var added = second.Rows.Single(r => r.Kind == RowChangeKind.Added);
        added.NewIndex.Should().Be(0);
        added.Values.Should().Contain(new ValueChange("MaxSeats", null, "3"));
        var changed = second.Rows.Single(r => r.Kind.HasFlag(RowChangeKind.Changed));
        changed.Kind.Should().Be(RowChangeKind.Changed);                        // shifted by the insert, not "moved"
        changed.Values.Should().ContainSingle().Which.Should().Be(new ValueChange("MaxSeats", "60", "70"));

        f.User.Permissions.Clear();
        await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(Table(2, new SeatTable.Row(Is(Plan.Pro), 1)));
        sink.Rejected.Should().ContainSingle().Which.Error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task GetDataChanges_describes_a_version_against_the_one_before_it()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("rules.parameters.publish");
        await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(
            new("pricing", "pricing.volume", AdminFixture.Tenant, 0, Today, AdminFixture.Json("""{"rate": 10}"""), null));
        await f.Dispatcher.CommandAsync<Commands.PublishParameters, long>(
            new("pricing", "pricing.volume", AdminFixture.Tenant, 1, Today, AdminFixture.Json("""{ "rate" : 12.5 }"""), "summer"));

        var r = await f.Dispatcher.QueryAsync<Queries.GetDataChanges, RuleDataChanges>(
            new("pricing", RuleDataKind.Parameters, "pricing.volume", AdminFixture.Tenant, 2));

        r.TryGetValue(out RuleDataChanges? changes).Should().BeTrue();
        changes!.FromVersion.Should().Be(1);
        changes.Header.Note.Should().Be("summer");
        changes.Values.Should().ContainSingle().Which.Should().Be(new ValueChangeView("rate", "10", "12.5", false));
    }

    [Fact]
    public void Row_moves_are_the_minimal_set()
    {
        static string Doc(params (string Id, int Max)[] rows)
            => "{\"rows\":[" + string.Join(",", rows.Select(r => $"{{\"id\":\"{r.Id}\",\"cells\":{{\"MaxSeats\":{r.Max}}}}}")) + "]}";
        string a = "00000000-0000-0000-0000-00000000000a", b = "00000000-0000-0000-0000-00000000000b",
               c = "00000000-0000-0000-0000-00000000000c", d = "00000000-0000-0000-0000-00000000000d";

        var diff = RuleDataDiff.Compute(RuleDataKind.Table, AdminFixture.Json(Doc((a, 1), (b, 2), (c, 3), (d, 4))),
            AdminFixture.Json(Doc((d, 4), (a, 1), (b, 2), (c, 3))), 1, 2);

        diff.Rows.Should().ContainSingle().Which.Should().Match<RowChange>(r => r.RowId == Guid.Parse(d) && r.Kind == RowChangeKind.Moved
            && r.OldIndex == 3 && r.NewIndex == 0);
    }

    // ── What-if ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Simulate_replays_stored_situations_with_the_draft_without_publishing()
    {
        var f = new AdminFixture();
        f.User.Permissions.Add("pricing.rules.seats.publish");
        f.Samples.Add(AdminFixture.Situation(Plan.Pro, 45), AdminFixture.Situation(Plan.Pro, 30), AdminFixture.Situation(Plan.Basic, 4));

        var draft = AdminFixture.Rows(new SeatTable.Row(Is(Plan.Basic), 5), new(Is(Plan.Pro), 40));   // Pro 50 → 40
        var r = await f.Dispatcher.QueryAsync<Queries.Simulate, SimulationResult>(
            new("pricing", RuleDataKind.Table, "pricing.seats", AdminFixture.Tenant, draft, Today.AddDays(-30), Today));

        r.TryGetValue(out SimulationResult? report).Should().BeTrue(r.TryGetError(out var e) ? e.Message : "");
        report!.Evaluated.Should().Be(3);
        report.Changed.Should().Be(2);                                          // both Pro sales get the new limit (max 50 → 40)…
        report.VerdictChanged.Should().Be(1);                                   // …only the 45-seat one crosses it
        report.NewlyDenied.Should().Be(1);
        report.Examples.Select(x => x.Index).Should().Equal(0, 1);              // Basic (row value unchanged) is not an example
        report.Examples[0].Before.Verdict.Should().Be(VerdictKind.Allowed);
        report.Examples[0].After.Verdict.Should().Be(VerdictKind.Denied);
        report.Examples[1].After.Verdict.Should().Be(VerdictKind.Allowed);
        (await f.Store.LoadAsync("pricing", 0, default)).Should().BeEmpty();                    // nothing published
        PricingRules.Evaluate(f.Host, AdminFixture.Situation(Plan.Pro, 45)).Verdict.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Simulate_rejects_an_invalid_draft_and_needs_the_publish_permission()
    {
        var f = new AdminFixture();
        var draft = AdminFixture.Rows(new SeatTable.Row(Is(Plan.Pro), 900));   // above the guardrail
        var q = new Queries.Simulate("pricing", RuleDataKind.Table, "pricing.seats", AdminFixture.Tenant, draft, Today, Today);

        (await f.Dispatcher.QueryAsync<Queries.Simulate, SimulationResult>(q)).TryGetError(out var e1).Should().BeTrue();
        e1.Type.Should().Be(ErrorType.Unauthorized);

        f.User.Permissions.Add("pricing.rules.seats.publish");
        (await f.Dispatcher.QueryAsync<Queries.Simulate, SimulationResult>(q)).TryGetError(out var e2).Should().BeTrue();
        e2.Violations[0].Code.Value.Should().Be("pricing.seats.above-legal");
    }

    // ── Device cache + incremental sync ───────────────────────────────────

    [Fact]
    public async Task Offline_restart_evaluates_with_the_cached_versions()
    {
        var server = new AdminFixture();
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(server, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));
        var device = new MemoryKeyValueStore();

        var client = PricingRules.CreateHost();
        using (var sync = new RuleSetSync(server.Dispatcher, new RuleSetRegistry([client]), server.Events, new KeyValueRuleDataCache(device)))
            await sync.StartAsync();

        // Restart while the server is unreachable (here: a server that no longer has the data).
        var unreachable = new AdminFixture();
        var restarted = PricingRules.CreateHost();
        using var again = new RuleSetSync(unreachable.Dispatcher, new RuleSetRegistry([restarted]), unreachable.Events, new KeyValueRuleDataCache(device));
        await again.StartAsync();

        PricingRules.Evaluate(restarted, AdminFixture.Situation(seats: 55)).Verdict.IsAllowed.Should().BeTrue();   // v1 (60), not code (50)
        again.LastVersion("pricing").Should().Be(1);
    }

    [Fact]
    public async Task Missed_versions_are_caught_up_in_order_from_the_last_known_one()
    {
        var server = new AdminFixture();
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(server, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));

        var client = PricingRules.CreateHost();
        using var sync = new RuleSetSync(server.Dispatcher, new RuleSetRegistry([client]), new DefaultEventBus());   // not subscribed to the server
        await sync.StartAsync();
        await Publish(server, Table(1, new SeatTable.Row(Is(Plan.Pro), 70)));
        await Publish(server, Table(2, new SeatTable.Row(Is(Plan.Pro), 40)));

        await sync.ReceiveAsync(new Events.RulesetPublished("pricing", RuleDataKind.Table, "pricing.seats", AdminFixture.Tenant, 3, Today));

        sync.LastVersion("pricing").Should().Be(3);
        client.Data.Snapshots.Select(s => s.Version).Should().Equal(1, 2, 3);
        PricingRules.Evaluate(client, AdminFixture.Situation(seats: 45)).Verdict.IsDenied.Should().BeTrue();       // v3 (40)
        (await server.Store.LoadAsync("pricing", 1, default)).Select(r => r.Info.Version).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Device_cache_keeps_the_last_N_versions_per_stream_and_the_one_in_force()
    {
        var device = new MemoryKeyValueStore();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var cache = new KeyValueRuleDataCache(device, keepVersions: 2, clock);
        var key = Key();
        RuleDataRecord Rec(long v, DateOnly from) => new(new RuleDataInfo(key, v, from, "ana", default, null), AdminFixture.Json("{\"rows\":[]}"));

        // v1 in force since Sep; v2..v4 scheduled for October → v1 must survive although it is the oldest.
        await cache.SaveAsync("pricing", [Rec(1, new(2026, 9, 1)), Rec(2, new(2026, 10, 1)), Rec(3, new(2026, 10, 2)), Rec(4, new(2026, 10, 3))], default);

        (await cache.LoadAsync("pricing", default)).Select(r => r.Info.Version).Should().Equal(1, 3, 4);
    }

    private static RuleDataKey Key() => new("pricing", RuleDataKind.Table, "pricing.seats", RuleTierPaths.Parse(AdminFixture.Tenant));

    private sealed class RecordingAudit : IRuleAuditSink
    {
        public List<RulePublishAudit> Published { get; } = [];
        public List<(RuleDataKey Key, string? Actor, Error Error)> Rejected { get; } = [];
        public ValueTask OnPublishedAsync(RulePublishAudit audit, CancellationToken ct) { Published.Add(audit); return ValueTask.CompletedTask; }
        public ValueTask OnRejectedAsync(RuleDataKey key, string? actor, Error error, CancellationToken ct) { Rejected.Add((key, actor, error)); return ValueTask.CompletedTask; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>In-memory <see cref="ISituationSamples"/>: canonical situation bytes, as an app would store them with its documents.</summary>
public sealed class TestSamples : ISituationSamples
{
    private readonly List<byte[]> _items = [];

    public void Add(params PricingSituation[] situations)
    {
        foreach (var s in situations)
        {
            var buffer = new ArrayBufferWriter<byte>();
            Situation.WriteTo(in s, buffer);
            _items.Add(buffer.WrittenSpan.ToArray());
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> GetAsync(string ruleSet, RuleTierPath tierPath, DateOnly from, DateOnly to, int max,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var item in _items) yield return item;
    }
}
