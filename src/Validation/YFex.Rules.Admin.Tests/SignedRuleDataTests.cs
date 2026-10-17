using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using MemoryPack;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Persistence;
using static YFex.Rules.Admin.RuleAdmin;
using static YFex.Rules.Cells;

namespace YFex.Rules.Admin.Tests;

/// <summary>
/// Signed rule data hooks (offline 05 §2.1, fixes F6): the publish pipeline signs each version once, <c>GetSignedRuleData</c> serves the
/// signed forms, <see cref="RuleSetSync"/> applies only what verifies and <see cref="KeyValueRuleDataCache"/> re-verifies at load. The real
/// signer/verifier (signed bundles with version floors) is YFex.Offline.Rules; these tests use an HMAC stand-in.
/// </summary>
public class SignedRuleDataTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Fact]
    public async Task Each_published_version_is_signed_once_and_served_with_its_signature()
    {
        var signer = new HmacSigner();
        var server = new AdminFixture(signer: signer);
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(server, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));
        signer.Signed.Should().Be(1, "the publish pipeline signs the new version");

        var first = await server.Dispatcher.QueryAsync<Queries.GetSignedRuleData, SignedRuleDataPayload[]>(new("pricing"));
        first.TryGetValue(out SignedRuleDataPayload[]? payloads).Should().BeTrue();
        payloads!.Should().ContainSingle().Which.Signature.Should().Equal(signer.Expected(payloads[0].Data.ToRecord()));
        var again = await server.Dispatcher.QueryAsync<Queries.GetSignedRuleData, SignedRuleDataPayload[]>(new("pricing", 0));
        again.TryGetValue(out SignedRuleDataPayload[]? second).Should().BeTrue();
        second![0].Signature.Should().Equal(payloads[0].Signature);
        signer.Signed.Should().Be(1, "serving a version does not sign it again");

        var unsigned = new AdminFixture();
        unsigned.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(unsigned, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));
        (await unsigned.Dispatcher.QueryAsync<Queries.GetSignedRuleData, SignedRuleDataPayload[]>(new("pricing"))).TryGetValue(out _)
            .Should().BeFalse("no signer at the server");
    }

    [Fact]
    public async Task Sync_with_a_verifier_applies_and_caches_only_versions_that_verify()
    {
        var signer = new HmacSigner();
        var server = new AdminFixture(signer: signer);
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(server, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));
        await Publish(server, Table(1, new SeatTable.Row(Is(Plan.Pro), 40)));
        var verifier = new HmacVerifier(signer) { Refuse = 2 };   // v2 arrives tampered
        var device = new MemoryKeyValueStore();
        var cache = new KeyValueRuleDataCache(device, verifier: verifier);

        var client = PricingRules.CreateHost();
        using var sync = new RuleSetSync(server.Dispatcher, new RuleSetRegistry([client]), new DefaultEventBus(), cache, verifier: verifier);
        await sync.StartAsync();

        PricingRules.Evaluate(client, AdminFixture.Situation(seats: 55)).Verdict.IsAllowed.Should().BeTrue("v1 (60) applied, v2 (40) refused");
        sync.Refused.Should().ContainSingle().Which.Check.Should().Be(RuleDataCheck.BadSignature);
        (await cache.LoadAsync("pricing", default)).Select(r => r.Info.Version).Should().Equal(1);
        verifier.ReceivedCalls.Should().Be(1);
        sync.LastVersion("pricing").Should().Be(1, "a refused version is fetched again at the next sync");
    }

    [Fact]
    public async Task The_device_cache_re_verifies_at_every_load()
    {
        var signer = new HmacSigner();
        var server = new AdminFixture(signer: signer);
        server.User.Permissions.Add("pricing.rules.seats.publish");
        await Publish(server, Table(0, new SeatTable.Row(Is(Plan.Pro), 60)));
        var verifier = new HmacVerifier(signer);
        var device = new MemoryKeyValueStore();
        using (var sync = new RuleSetSync(server.Dispatcher, new RuleSetRegistry([PricingRules.CreateHost()]), new DefaultEventBus(),
                   new KeyValueRuleDataCache(device, verifier: verifier), verifier: verifier))
            await sync.StartAsync();

        // Someone raises the cached limit to 500 seats on the device's disk.
        string key = "yfex.rules/pricing/" + 1.ToString("D19");
        RuleDataPayload cached = MemoryPackSerializer.Deserialize<RuleDataPayload>((await device.GetAsync(key))!)!;
        await device.SetAsync(key, MemoryPackSerializer.Serialize(cached with { Payload = AdminFixture.Rows(new SeatTable.Row(Is(Plan.Pro), 500)) }));

        var restarted = PricingRules.CreateHost();
        var cache = new KeyValueRuleDataCache(device, verifier: verifier);
        using var offline = new RuleSetSync(new AdminFixture().Dispatcher, new RuleSetRegistry([restarted]), new DefaultEventBus(), cache, verifier: verifier);
        await offline.StartAsync();

        cache.Refused.Should().ContainSingle().Which.Check.Should().Be(RuleDataCheck.BadSignature);
        PricingRules.Evaluate(restarted, AdminFixture.Situation(seats: 55)).Verdict.IsDenied.Should().BeTrue("the code table (50) applies, never the edited 500");
        new KeyValueRuleDataCache(device).LoadAsync("pricing", default).Result.Should().ContainSingle("without a verifier the cache reads as before");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Commands.PublishTable Table(long baseVersion, params SeatTable.Row[] rows)
        => new("pricing", "pricing.seats", AdminFixture.Tenant, baseVersion, Today, AdminFixture.Rows(rows), null);

    private static async Task<long> Publish(AdminFixture f, Commands.PublishTable c)
    {
        var r = await f.Dispatcher.CommandAsync<Commands.PublishTable, long>(c);
        r.TryGetValue(out long v).Should().BeTrue(r.TryGetError(out var e) ? e.Message : "");
        return v;
    }

    private sealed class HmacSigner : IRuleDataSigner
    {
        private readonly ConcurrentDictionary<long, byte[]> _signed = new();
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private int _count;

        public int Signed => _count;

        public ValueTask<byte[]> SignAsync(RuleDataRecord record, CancellationToken ct)
            => ValueTask.FromResult(_signed.GetOrAdd(record.Info.Version, _ =>
            {
                Interlocked.Increment(ref _count);
                return Expected(record);
            }));

        public byte[] Expected(RuleDataRecord record) => HMACSHA256.HashData(_key, MemoryPackSerializer.Serialize(RuleDataPayload.From(record)));
    }

    private sealed class HmacVerifier(HmacSigner signer) : IRuleDataVerifier
    {
        public long Refuse { get; init; } = -1;
        public int ReceivedCalls { get; private set; }

        public ValueTask<ImmutableArray<RuleDataCheck>> VerifyAsync(IReadOnlyList<SignedRuleData> records, bool received, CancellationToken ct)
        {
            if (received) ReceivedCalls++;
            var checks = ImmutableArray.CreateBuilder<RuleDataCheck>(records.Count);
            foreach (SignedRuleData data in records)
                checks.Add(data.Record.Info.Version != Refuse && data.Signature.Span.SequenceEqual(signer.Expected(data.Record))
                    ? RuleDataCheck.Valid
                    : RuleDataCheck.BadSignature);
            return ValueTask.FromResult(checks.MoveToImmutable());
        }
    }
}
