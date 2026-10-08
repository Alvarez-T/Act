using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>
/// Authorization-consolidation Phase 3a (decision Q5): a <c>ScopedByUser()</c> query is keyed by the actor's subject
/// (<see cref="ActorContext"/>) and is never cached without one — it used to fall back to one key shared by every user.
/// </summary>
[Collection("DispatcherTests")]
public sealed class UserScopedCacheTests
{
    public UserScopedCacheTests() => TestDispatcherFixture.ClearStore();

    private sealed class UserScopedConfig : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b)
            => b.Query<TestAggregate.Queries.GetByIdQuery, TestItem>().Cacheable().ScopedByUser();
    }

    private static Task<CacheableQueryResult<TestItem>> Query(TestDispatcherFixture fx)
        => fx.Dispatcher.QueryAsync<TestAggregate.Queries.GetByIdQuery, TestItem>(new TestAggregate.Queries.GetByIdQuery(1)).AsTask();

    [Fact]
    public async Task Without_a_subject_a_per_user_query_is_not_cached()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "A");
        var fx = new TestDispatcherFixture([new UserScopedConfig()]);

        await Query(fx);
        using (ActorContext.Enter(ActorScope.Anonymous))
            await Query(fx);

        (await fx.Cache.GetKeysWithPrefixAsync("query:")).Should().BeEmpty("nobody to scope it to: never shared across users");
    }

    [Fact]
    public async Task Each_actor_gets_its_own_entry()
    {
        TestDispatcherFixture.Store[1] = new TestItem(1, "A");
        var fx = new TestDispatcherFixture([new UserScopedConfig()]);

        using (ActorContext.Enter(new ActorScope(new ActorIdentity("joao"))))
        {
            await Query(fx);
            await Query(fx);
        }
        using (ActorContext.Enter(new ActorScope(new ActorIdentity("maria"))))
            await Query(fx);

        var keys = await fx.Cache.GetKeysWithPrefixAsync("query:");
        keys.Should().HaveCount(2);
        keys.Should().Contain(static key => key.Contains(":joao:")).And.Contain(static key => key.Contains(":maria:"));
    }
}
