using YFex.Cqrs;
using YFex.Cryptography;
using YFex.Messaging.Rpc;
using YFex.Tenancy;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>
/// Server-pushed events reach only their audience; the audience comes from a server-issued token that expires and names its key
/// (cryptography C4: MessageToken over a shared, rotating key ring).
/// </summary>
[Trait("Category", "Rpc")]
public sealed class EventChannelAudienceTests
{
    public enum Level : byte { Tenant = 0, User = 1 }

    public sealed record OrderShipped(string Id) : IEvent;

    private static readonly ScopeIdentity AcmeBob =
        ScopeIdentity.Of(ScopeValue.Of(Level.Tenant, "acme"), ScopeValue.Of(Level.User, "bob"));
    private static readonly ScopeIdentity GlobexAnn =
        ScopeIdentity.Of(ScopeValue.Of(Level.Tenant, "globex"), ScopeValue.Of(Level.User, "ann"));

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    // Audience = tenant: every user of the publishing tenant sees the event. Each host without a shared ring has its own key.
    private static EventChannelHost<OrderShipped> NewHost(KeyRingProvider? rings = null, TimeProvider? time = null) => new(new EventChannelOptions
    {
        Audience = new ScopePartitionOptions().UpTo(Level.Tenant),
    }, rings: rings, time: time);

    private static KeyRingProvider SharedRings(IKeyRingStore store, TimeProvider? time = null) => new(store,
        new Dictionary<string, KeyRingOptions> { ["messaging/event-audience"] = new KeyRingOptions().For(KeyPurpose.MessageAuthentication) }, time);

    private static async Task<string?> TokenFor(EventChannelHost<OrderShipped> host, ScopeIdentity caller)
    {
        using (ScopeOverride.Enter(caller))
            return await host.GetAudienceTokenAsync();
    }

    private static async Task<string[]> Ids(EventChannelHost<OrderShipped> host, string? token)
        => (await host.GetEventsAsync(new EventFilter(token), 0)).Items.Select(e => e.Id).ToArray();

    [Fact]
    public async Task Each_tenant_sees_its_own_events_plus_global_ones()
    {
        var host = NewHost();
        using (ScopeOverride.Enter(AcmeBob)) host.Append(new OrderShipped("acme-1"));
        using (ScopeOverride.Enter(GlobexAnn)) host.Append(new OrderShipped("globex-1"));
        using (ScopeOverride.EnterUnscoped()) host.Append(new OrderShipped("broadcast"));

        var acmeAlice = ScopeIdentity.Of(ScopeValue.Of(Level.Tenant, "acme"), ScopeValue.Of(Level.User, "alice"));
        (await Ids(host, await TokenFor(host, acmeAlice))).Should().Equal("acme-1", "broadcast");
        (await Ids(host, await TokenFor(host, GlobexAnn))).Should().Equal("globex-1", "broadcast");
        (await Ids(host, token: null)).Should().Equal("broadcast");
    }

    [Fact]
    public async Task A_forged_or_foreign_token_grants_nothing()
    {
        var host = NewHost();
        using (ScopeOverride.Enter(AcmeBob)) host.Append(new OrderShipped("acme-1"));

        var forged = "0=acme." + Convert.ToBase64String(new byte[32]).TrimEnd('=');
        (await Ids(host, forged)).Should().BeEmpty();
        (await Ids(host, "0=acme")).Should().BeEmpty();

        var otherServer = NewHost();
        (await Ids(host, await TokenFor(otherServer, AcmeBob))).Should().BeEmpty();
    }

    [Fact]
    public async Task An_audience_token_is_refused_after_it_expires()
    {
        var time = new ManualClock(Now);
        var host = NewHost(time: time);
        string? token = await TokenFor(host, AcmeBob);
        using (ScopeOverride.Enter(AcmeBob)) host.Append(new OrderShipped("acme-1"));

        (await Ids(host, token)).Should().Equal("acme-1");
        MessageToken.TryReadHeader(token, out _, out DateTimeOffset expiresAt).Should().BeTrue();
        expiresAt.Should().Be(Now.AddHours(12), "default lifetime");

        time.Now = Now.AddHours(12);
        using (ScopeOverride.Enter(AcmeBob)) host.Append(new OrderShipped("acme-2"));   // invalidates; the recomputation sees the expiry
        (await Ids(host, token)).Should().BeEmpty();
        (await Ids(host, await TokenFor(host, AcmeBob))).Should().Equal("acme-1", "acme-2");   // a fresh token works again
    }

    [Fact]
    public async Task Nodes_sharing_the_key_ring_accept_each_others_tokens_also_after_rotation()
    {
        var store = new InMemoryKeyRingStore();
        using KeyRingProvider ringsA = SharedRings(store), ringsB = SharedRings(store);
        var nodeA = NewHost(ringsA);
        var nodeB = NewHost(ringsB);
        using (ScopeOverride.Enter(AcmeBob)) nodeB.Append(new OrderShipped("acme-1"));

        string? fromA = await TokenFor(nodeA, AcmeBob);
        (await Ids(nodeB, fromA)).Should().Equal("acme-1");

        await ringsA.RotateAsync(new KeyRingName("messaging/event-audience"));          // node A rotates; B has the old snapshot cached
        string? rotated = await TokenFor(nodeA, AcmeBob);
        MessageToken.TryReadHeader(rotated, out KeyId newKey, out _).Should().BeTrue();
        MessageToken.TryReadHeader(fromA, out KeyId oldKey, out _).Should().BeTrue();
        newKey.Should().NotBe(oldKey);
        (await Ids(nodeB, rotated)).Should().Equal(new[] { "acme-1" }, "node B reloads the ring on an unknown key id");
        (await Ids(nodeB, fromA)).Should().Equal(new[] { "acme-1" }, "the previous key still verifies");
    }

    [Fact]
    public async Task Tokens_of_the_old_format_grant_nothing()
    {
        var host = NewHost();
        using (ScopeOverride.Enter(AcmeBob)) host.Append(new OrderShipped("acme-1"));
        (await Ids(host, "0=acme.mx1Nf0Vb6eGmHq0n3bW0sLwq6dQ3b2u2m8uAaYpVt2U")).Should().BeEmpty();
    }

    [Fact]
    public async Task The_obsolete_fixed_key_still_lets_two_nodes_share_tokens()
    {
        byte[] key = Enumerable.Repeat((byte)7, 32).ToArray();
#pragma warning disable CS0618
        var options = new EventChannelOptions { Audience = new ScopePartitionOptions().UpTo(Level.Tenant), SigningKey = key };
#pragma warning restore CS0618
        var nodeA = new EventChannelHost<OrderShipped>(options);
        var nodeB = new EventChannelHost<OrderShipped>(options);
        using (ScopeOverride.Enter(AcmeBob)) nodeB.Append(new OrderShipped("acme-1"));

        (await Ids(nodeB, await TokenFor(nodeA, AcmeBob))).Should().Equal("acme-1");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Caller_without_scope_gets_no_token()
        => (await NewHost().GetAudienceTokenAsync()).Should().BeNull();

    [Fact]
    public async Task Without_tenancy_every_event_is_global()
    {
        var host = new EventChannelHost<OrderShipped>();
        host.Append(new OrderShipped("a"));
        host.Append(new OrderShipped("b"));

        var cursor = await host.GetEventsAsync(new EventFilter(), 0);
        cursor.Items.Select(e => e.Id).Should().Equal("a", "b");
        cursor.Sequence.Should().Be(2);
        (await host.GetEventsAsync(new EventFilter(), 1)).Items.Should().ContainSingle();
    }
}
