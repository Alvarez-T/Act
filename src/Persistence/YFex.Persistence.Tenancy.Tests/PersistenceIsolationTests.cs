using Microsoft.Extensions.DependencyInjection;
using YFex.Tenancy;

namespace YFex.Persistence.Tenancy.Tests;

public enum AppScope : byte { Tenant = 0, User = 1 }

public class PersistenceIsolationTests
{
    private static readonly byte[] A = [1], B = [2];

    private static ScopeIdentity Tenant(string t) => ScopeIdentity.Of(ScopeValue.Of(AppScope.Tenant, t));

    // One composition root: backend, provider, and the single tenancy configuration call.
    private static (IServiceProvider sp, MutableScopeContext scope, MemoryKeyValueStore raw) Build(
        Action<PersistenceIsolationOptions>? configure = null, Action<ScopePartitionOptions>? isolation = null)
    {
        var raw = new MemoryKeyValueStore();
        var services = new ServiceCollection();
        services.AddSingleton<MutableScopeContext>();
        services.AddSingleton<IScopeContext>(sp => sp.GetRequiredService<MutableScopeContext>());
        services.AddYFexPersistence(raw);
        services.AddYFexTenancy(t => t.Isolation(isolation).IsolatePersistence(configure));
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<MutableScopeContext>(), raw);
    }

    [Fact]
    public async Task Without_opt_in_keys_are_unchanged()
    {
        var raw = new MemoryKeyValueStore();
        var sp = new ServiceCollection().AddYFexPersistence(raw).AddYFexTenancy().BuildServiceProvider();

        await sp.GetRequiredService<IKeyValueStore>().SetAsync("draft", A);

        (await raw.GetKeysWithPrefixAsync("")).Should().Equal("draft");
    }

    [Fact]
    public async Task Implicit_scope_partitions_tenants_on_one_device()
    {
        var (sp, scope, raw) = Build();
        var store = sp.GetRequiredService<IKeyValueStore>();

        scope.Current = Tenant("acme");
        await store.SetAsync("draft", A);
        scope.Current = Tenant("globex");
        (await store.GetAsync("draft")).Should().BeNull();          // no cross-tenant leak
        await store.SetAsync("draft", B);

        scope.Current = Tenant("acme");
        (await store.GetAsync("draft")).Should().Equal(A);
        (await store.GetKeysWithPrefixAsync("")).Should().Equal("draft");   // logical keys only
        (await raw.GetKeysWithPrefixAsync("")).Should().BeEquivalentTo(["~s/0=acme/draft", "~s/0=globex/draft"]);
    }

    [Fact]
    public async Task Explicit_override_targets_another_tenant_or_the_shared_area()
    {
        var (sp, scope, raw) = Build();
        var store = sp.GetRequiredService<IKeyValueStore>();
        scope.Current = Tenant("acme");

        using (ScopeOverride.Enter(ScopeValue.Of(AppScope.Tenant, "globex")))
            await store.SetAsync("report", A);
        using (ScopeOverride.EnterUnscoped())
            await store.SetAsync("device-id", B);

        (await raw.GetAsync("~s/0=globex/report")).Should().Equal(A);
        (await raw.GetAsync("device-id")).Should().Equal(B);
        (await store.GetAsync("report")).Should().BeNull();         // back to acme outside the block
    }

    [Fact]
    public async Task Clear_only_wipes_the_current_partition()
    {
        var (sp, scope, raw) = Build();
        var store = sp.GetRequiredService<IKeyValueStore>();

        scope.Current = Tenant("acme");     await store.SetAsync("k", A);
        scope.Current = Tenant("globex");   await store.SetAsync("k", B);
        scope.Current = ScopeIdentity.None; await store.SetAsync("shared", A);

        scope.Current = Tenant("acme");
        await store.ClearAsync();
        (await raw.GetKeysWithPrefixAsync("")).Should().BeEquivalentTo(["~s/0=globex/k", "shared"]);

        scope.Current = ScopeIdentity.None;
        await store.ClearAsync();                                   // shared clear spares tenants
        (await raw.GetKeysWithPrefixAsync("")).Should().Equal("~s/0=globex/k");
    }

    [Fact]
    public async Task Shared_key_prefixes_bypass_partitioning_even_when_fail_closed()
    {
        var (sp, scope, raw) = Build(o => o.SharedKeyPrefixes.Add("device/"), i => i.WhenUnscoped = UnscopedBehavior.Throw);
        var store = sp.GetRequiredService<IKeyValueStore>();

        await store.SetAsync("device/id", A);                       // before sign-in: allowed
        await store.Invoking(s => s.SetAsync("draft", A).AsTask()).Should().ThrowAsync<ScopeIsolationException>();

        scope.Current = Tenant("acme");
        (await store.GetAsync("device/id")).Should().Equal(A);
        (await raw.GetKeysWithPrefixAsync("")).Should().Equal("device/id");
    }

    [Fact]
    public async Task Per_subsystem_window_can_partition_by_user_while_global_policy_is_tenant_only()
    {
        var (sp, scope, raw) = Build(o => o.Partition = p => p.MaxRank = byte.MaxValue, i => i.MaxRank = (byte)AppScope.Tenant);
        scope.Current = ScopeIdentity.Of(ScopeValue.Of(AppScope.Tenant, "acme"), ScopeValue.Of(AppScope.User, "bob"));

        await sp.GetRequiredService<IKeyValueStore>().SetAsync("prefs", A);

        (await raw.GetKeysWithPrefixAsync("")).Should().Equal("~s/0=acme/1=bob/prefs");
        sp.GetRequiredService<IScopePartitioner>().CurrentPrefix().Should().Be("~s/0=acme/");   // global unchanged
    }

    [Fact]
    public async Task Cache_tags_are_partitioned_so_eviction_stays_inside_the_tenant()
    {
        var scope = new MutableScopeContext(new ScopeValueInterner());
        var scoper = new PersistenceKeyScoper(new ScopeKeyPartitioner(new ScopePartitionOptions(), scope));
        var cache = new ScopedCache(new KeyValueCache(new MemoryKeyValueStore()), scoper);
        var tagged = new CacheEntryOptions { Tags = ["orders"] };

        scope.Current = Tenant("acme");   await cache.SetAsync("o1", 1, tagged);
        scope.Current = Tenant("globex"); await cache.SetAsync("o1", 2, tagged);

        await cache.RemoveByTagAsync("orders");                     // globex evicts its own only

        (await cache.GetAsync<int>("o1")).Should().Be(0);
        scope.Current = Tenant("acme");
        (await cache.GetAsync<int>("o1")).Should().Be(1);
    }

    public enum FarmaScope : byte { Company = 0, Store = 1, Operator = 2 }

    private static ScopeIdentity Pos(string store, string op) => ScopeIdentity.Of(
        ScopeValue.Of(FarmaScope.Company, "drogamais"), ScopeValue.Of(FarmaScope.Store, store), ScopeValue.Of(FarmaScope.Operator, op));

    [Fact]
    public async Task Pos_terminal_drafts_are_per_operator_while_catalog_cache_is_per_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MutableScopeContext>();
        services.AddSingleton<IScopeContext>(sp => sp.GetRequiredService<MutableScopeContext>());
        services.AddYFexPersistence(new MemoryKeyValueStore());          // also registers an InMemoryCache
        services.AddYFexTenancy(t => t
            .Isolation(o => o.UpTo(FarmaScope.Operator))                 // stores: private per operator
            .IsolatePersistence(p => p.CachePartition = w => w.UpTo(FarmaScope.Store)));   // cache: per store
        var sp = services.BuildServiceProvider();
        var scope = sp.GetRequiredService<MutableScopeContext>();
        var store = sp.GetRequiredService<IKeyValueStore>();
        var cache = sp.GetRequiredService<ICache>();

        scope.Current = Pos("sp01", "ana");
        await store.SetAsync("cart/current", A);
        await cache.SetAsync("catalog/version", 42);

        scope.Current = Pos("sp01", "bruno");                             // shift change, same terminal
        (await store.GetAsync("cart/current")).Should().BeNull();         // Ana's customer data stays hers
        (await cache.GetAsync<int>("catalog/version")).Should().Be(42);   // same store → shared catalog

        scope.Current = Pos("sp02", "bruno");                             // terminal moved to another store
        (await cache.GetAsync<int>("catalog/version")).Should().Be(0);
    }

    [Fact]
    public void Isolating_before_a_backend_is_registered_fails_fast()
    {
        var services = new ServiceCollection();
        services.Invoking(s => s.AddYFexTenancy(t => t.IsolatePersistence()))
            .Should().Throw<InvalidOperationException>().WithMessage("*AddYFexPersistence*");
    }
}
