using YFex.Cqrs;
using YFex.Persistence;
using CreateCustomer = YFex.Messaging.Rpc.Tests.ShopAggregate.Commands.CreateCustomer;
using SearchCustomers = YFex.Messaging.Rpc.Tests.ShopAggregate.Queries.SearchCustomers;

namespace YFex.Messaging.Rpc.Tests;

/// <summary>
/// Validation Phase 3 acceptance over a real Fusion RPC connection (two hubs, serialized with mempack6): the server
/// validates (D25, G1), the structured error crosses the wire (D26, G2) and reaches the caller.
/// </summary>
/// <remarks>One connection per class: tearing down ActualLab peers takes ~4 s, so the fixture is shared and handler
/// assertions are relative (<see cref="CreateCustomerHandler.Calls"/> before/after).</remarks>
public class RpcRoundTripTests(RpcFixture fx) : IClassFixture<RpcFixture>
{
    private static readonly CreateCustomer Valid = new("Ana", "ana@shop.test", "12345678901");

    [Fact]
    public async Task Valid_command_round_trips()
    {
        int calls = fx.Handler.Calls;

        var r = await fx.Contract.CreateCustomerAsync(Valid);

        r.OkValue.Should().Be(42);
        fx.Handler.Calls.Should().Be(calls + 1);
    }

    [Fact]
    public async Task Server_rejects_invalid_shape_and_violations_cross_the_wire()
    {
        // Straight to the proxy: an old/tampered client that skipped client-side validation (drift B, 11 §2.3).
        int calls = fx.Handler.Calls;

        var r = await fx.Contract.CreateCustomerAsync(new CreateCustomer("", "not-an-email", "1"));

        r.TryGetError(out var e).Should().BeTrue();
        e.Type.Should().Be(ErrorType.ValidationProblem);
        e.Violations.AsSpan().ToArray().Select(v => $"{v.Path}:{v.Code}").Should().Equal("Name:required", "Email:email", "Document:len");
        fx.Handler.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task Server_context_rule_violation_keeps_code_path_and_args()
    {
        int calls = fx.Handler.Calls;

        var r = await fx.Contract.CreateCustomerAsync(Valid with { Email = EmailNotRegistered.Taken });

        r.TryGetError(out var e).Should().BeTrue();
        var v = e.Violations[0];
        v.Path.ToString().Should().Be("Email");
        v.Code.Value.Should().Be("customer.email-taken");
        v.Args[0].TryGet(out string? email).Should().BeTrue();
        email.Should().Be(EmailNotRegistered.Taken);
        fx.Handler.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task Invalid_query_is_rejected_by_the_server()
    {
        int calls = fx.Handler.Calls;

        var r = await fx.Contract.SearchCustomersAsync(new SearchCustomers("a", 0));

        r.TryGetValue(out Error e).Should().BeTrue();
        e.Violations[0].Path.ToString().Should().Be("Take");
        (await fx.Contract.SearchCustomersAsync(new SearchCustomers("a", 5))).OkValue.Should().Equal("a:5");
    }

    [Fact]
    public async Task FusionMessageBus_returns_the_server_error_instead_of_throwing()
    {
        // G2: the generated routes used to throw "Command failed." and drop the Error.
        int calls = fx.Handler.Calls;
        var bus = fx.CreateClientBus();

        var r = await bus.CommandAsync<CreateCustomer, int>(Valid with { Email = EmailNotRegistered.Taken });

        r.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Code.Value.Should().Be("customer.email-taken");
        (await bus.CommandAsync<CreateCustomer, int>(Valid)).OkValue.Should().Be(42);
    }

    [Fact]
    public async Task FusionMessageBus_rejects_invalid_shape_before_calling_the_server()
    {
        int calls = fx.Handler.Calls;
        var bus = fx.CreateClientBus();

        var r = await bus.CommandAsync<CreateCustomer, int>(new CreateCustomer("", "a@b.co", "12345678901"));

        r.TryGetError(out var e).Should().BeTrue();
        e.Violations[0].Path.ToString().Should().Be("Name");
        fx.Handler.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task FusionMessageBus_offline_never_enqueues_an_invalid_command()
    {
        int calls = fx.Handler.Calls;
        var outbox = new InMemoryOutbox(new OutboxOptions());
        var bus = fx.CreateClientBus(connected: false, outbox: outbox);

        var r = await bus.CommandAsync<CreateCustomer, int>(new CreateCustomer("", "a@b.co", "12345678901"));

        r.IsError.Should().BeTrue();
        outbox.PendingCount.Should().Be(0);
    }
}
