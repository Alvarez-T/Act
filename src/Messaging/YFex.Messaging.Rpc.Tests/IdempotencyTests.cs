using ActualLab.Rpc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using CreateCustomer = YFex.Messaging.Rpc.Tests.ShopAggregate.Commands.CreateCustomer;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Offline design F3 (docs/internal/offline/01 §2, 11 Phase 0.3) over a real Fusion RPC connection: a command re-sent with the
/// same idempotency key — an outbox replay after a lost reply — executes once and the duplicate gets the stored result.
/// </summary>
public class IdempotencyTests(RpcFixture fx) : IClassFixture<RpcFixture>
{
    private static readonly CreateCustomer Valid = new("Ana", "ana@shop.test", "12345678901");

    private Task<QueueableResult<int>> SendAsync(CreateCustomer command, string? key)
    {
        if (key is null) return fx.Contract.CreateCustomerAsync(command);
        // What FusionMessageBus does for every command: the key travels as an RPC header.
        using (new RpcOutboundCallSetup { Headers = [new RpcHeader(IdempotencyHeaders.Key, key)] }.Activate())
            return fx.Contract.CreateCustomerAsync(command);
    }

    [Fact]
    public async Task Same_key_twice_executes_once_and_the_duplicate_gets_the_stored_result()
    {
        int calls = fx.Handler.Calls;
        var key = Guid.CreateVersion7().ToString("N");

        var first = await SendAsync(Valid, key);
        var replay = await SendAsync(Valid, key);

        first.OkValue.Should().Be(42);
        replay.OkValue.Should().Be(42);
        fx.Handler.Calls.Should().Be(calls + 1, "the replay after a lost reply must not create the customer twice");
    }

    [Fact]
    public async Task Different_keys_execute_each_time()
    {
        int calls = fx.Handler.Calls;

        await SendAsync(Valid, Guid.CreateVersion7().ToString("N"));
        await SendAsync(Valid, Guid.CreateVersion7().ToString("N"));

        fx.Handler.Calls.Should().Be(calls + 2);
    }

    [Fact]
    public async Task Calls_without_a_key_are_not_deduplicated()
    {
        int calls = fx.Handler.Calls;

        await SendAsync(Valid, key: null);   // old clients
        await SendAsync(Valid, key: null);

        fx.Handler.Calls.Should().Be(calls + 2);
    }

    [Fact]
    public async Task Rejected_call_is_not_remembered_so_a_retry_with_the_same_key_executes()
    {
        int calls = fx.Handler.Calls;
        var key = Guid.CreateVersion7().ToString("N");

        var rejected = await SendAsync(new CreateCustomer("", "not-an-email", "1"), key);
        var retried = await SendAsync(Valid, key);

        rejected.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.ValidationProblem);
        retried.OkValue.Should().Be(42);
        fx.Handler.Calls.Should().Be(calls + 1);
    }

    [Fact]
    public async Task Client_bus_sends_a_fresh_key_per_dispatch()
    {
        int calls = fx.Handler.Calls;
        var bus = fx.CreateClientBus();

        await bus.CommandAsync<CreateCustomer, int>(Valid);
        await bus.CommandAsync<CreateCustomer, int>(Valid);

        fx.Handler.Calls.Should().Be(calls + 2, "two user actions are two commands; only replays reuse a key");
        fx.ServerServices.GetRequiredService<IIdempotencyStore>().Should().BeOfType<InMemoryIdempotencyStore>()
            .Which.Count.Should().BeGreaterThanOrEqualTo(2, "every bus dispatch carried a key the server recorded");
    }

    [Fact]
    public async Task Concurrent_duplicates_wait_for_the_first_execution()
    {
        int calls = fx.Handler.Calls;
        var key = Guid.CreateVersion7().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync(Valid, key)));

        results.Should().AllSatisfy(r => r.OkValue.Should().Be(42));
        fx.Handler.Calls.Should().Be(calls + 1);
    }
}
