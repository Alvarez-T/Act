using YFex.Cqrs;
using YFex.Messaging.Rpc;
using YFex.Tenancy;

namespace YFex.Messaging.Tests.Rpc;

/// <summary>Server-pushed events reach only their audience; the audience comes from a server-signed token.</summary>
[Trait("Category", "Rpc")]
public sealed class EventChannelAudienceTests
{
    public enum Level : byte { Tenant = 0, User = 1 }

    public sealed record OrderShipped(string Id) : IEvent;

    private static readonly ScopeIdentity AcmeBob =
        ScopeIdentity.Of(ScopeValue.Of(Level.Tenant, "acme"), ScopeValue.Of(Level.User, "bob"));
    private static readonly ScopeIdentity GlobexAnn =
        ScopeIdentity.Of(ScopeValue.Of(Level.Tenant, "globex"), ScopeValue.Of(Level.User, "ann"));

    // Audience = tenant: every user of the publishing tenant sees the event.
    private static EventChannelHost<OrderShipped> NewHost(byte[]? key = null) => new(new EventChannelOptions
    {
        Audience = new ScopePartitionOptions().UpTo(Level.Tenant),
        SigningKey = key ?? new byte[32],
    });

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

        var otherServer = NewHost(key: Enumerable.Repeat((byte)7, 32).ToArray());
        (await Ids(host, await TokenFor(otherServer, AcmeBob))).Should().BeEmpty();
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
