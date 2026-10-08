using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging.Rpc;
using YFex.Messaging.Tests.Fixtures;
using YFex.Validation;
using CreateCustomer = YFex.Messaging.Tests.Fixtures.ShopAggregate.Commands.CreateCustomer;
using SearchCustomers = YFex.Messaging.Tests.Fixtures.ShopAggregate.Queries.SearchCustomers;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Validation Phase 3, step 3: typed stages 1 → 2 → 5 (09 §1), server decorator (D25), G1/G3/G6.</summary>
public sealed class PipelineStageTests
{
    private static readonly CreateCustomer Valid = new("Ana", "ana@shop.test", "123");

    [Fact]
    public async Task Invalid_shape_stops_before_authorize_and_context()
    {
        var fx = new ShopFixture([new ShopServer()], authorize: (_, _) => true);

        var r = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(new CreateCustomer("", "nope", "1"));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        e.Violations.Count.Should().Be(2, "Name required + Email format, from fluent-only rules");
        fx.Log.Entries.Should().BeEmpty("shape runs first and is pure");
    }

    [Fact]
    public async Task Authorize_runs_before_context_rules()
    {
        var fx = new ShopFixture([new ShopServer()], authorize: (_, _) => false);

        var r = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid);

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.Forbidden);
        fx.Log.Entries.Should().Equal(["authorize"], "a uniqueness check must not leak existence to unauthorized callers");
    }

    [Fact]
    public async Task Stages_run_in_order_then_the_handler()
    {
        var fx = new ShopFixture([new ShopServer()], authorize: (_, _) => true);

        var r = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid);

        r.OkValue.Should().Be(42);
        fx.Log.Entries.Should().Equal("authorize", "context", "handler");
    }

    [Fact]
    public async Task TypeRef_context_rule_runs_and_returns_structured_violation()
    {
        // G3: .Validate<TRule>() used to be silently ignored.
        var fx = new ShopFixture([new ShopServer()]);

        var r = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid with { Email = EmailNotRegistered.TakenEmail });

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        e.Violations[0].Path.ToString().Should().Be("Email");
        e.Violations[0].Code.Value.Should().Be("customer.email-taken");
        fx.Log.Entries.Should().Equal("context");
    }

    [Fact]
    public async Task Invalid_query_returns_ValidationProblem()
    {
        var fx = new ShopFixture();

        var r = await fx.Dispatcher.QueryAsync<SearchCustomers, List<string>>(new SearchCustomers("a", 0));

        r.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        e.Violations[0].Path.ToString().Should().Be("Take");
    }

    [Fact]
    public async Task Offline_invalid_command_is_never_enqueued()
    {
        // G6: shape before enqueue (09 §8).
        var fx = new ShopFixture(connected: false);

        var invalid = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(new CreateCustomer("", "x@y.co", "1"));
        invalid.TryGetError(out var e).Should().BeTrue();
        e.Kind.Should().Be(ErrorKind.ValidationProblem);
        fx.Outbox.PendingCount.Should().Be(0);

        var valid = await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid);
        valid.IsQueued.Should().BeTrue();
        fx.Outbox.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Server_invoker_decorator_rejects_with_a_typed_error()
    {
        // G1 / D25: everything entering through IHandlerInvoker (RPC contract impl, Wolverine, replay) is validated.
        var fx = new ShopFixture([new ShopServer()], decorateInvoker: true);

        var shape = () => fx.Invoker.InvokeAsync<int>(new CreateCustomer("", "x@y.co", "1"), default);
        (await shape.Should().ThrowAsync<ErrorResultException>()).Which.TypedError.Violations[0].Code.Value.Should().Be("required");

        var context = () => fx.Invoker.InvokeAsync<int>(Valid with { Email = EmailNotRegistered.TakenEmail }, default);
        (await context.Should().ThrowAsync<ErrorResultException>()).Which.TypedError.Kind.Should().Be(ErrorKind.ValidationProblem);

        (await fx.Invoker.InvokeAsync<int>(Valid, default)).Should().Be(42);
    }

    [Fact]
    public async Task LocalDispatcher_does_not_validate_twice_through_the_decorator()
    {
        var fx = new ShopFixture([new ShopServer()], decorateInvoker: true);

        (await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid)).OkValue.Should().Be(42);

        fx.Log.Entries.Should().Equal("context", "handler");
    }

    [Fact]
    public void Decorate_wraps_the_registered_invoker_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StageLog());
        services.AddSingleton(CompiledMessagingRegistry.Build([new ShopBaseline()]));
        services.AddSingleton<IHandlerInvoker>(sp => new LocalHandlerInvoker(sp));
        ValidatingHandlerInvoker.Decorate(services);
        ValidatingHandlerInvoker.Decorate(services);

        var invoker = services.BuildServiceProvider().GetRequiredService<IHandlerInvoker>();

        var validating = invoker.Should().BeOfType<ValidatingHandlerInvoker>().Subject;
        validating.Inner.Should().BeOfType<LocalHandlerInvoker>();
    }

    [Fact]
    public async Task Validated_handler_receives_Valid_T_and_wins_over_the_classic_one()
    {
        var fx = new ShopFixture(configureServices: s =>
            s.AddTransient<IValidatedCommandHandler<CreateCustomer, int>, ValidatedCreateCustomerHandler>());

        (await fx.Dispatcher.CommandAsync<CreateCustomer, int>(Valid)).OkValue.Should().Be(7);
        (await fx.Dispatcher.CommandAsync<CreateCustomer, int>(new CreateCustomer("", "x", "1"))).IsError.Should().BeTrue();

        fx.Log.Entries.Should().Equal("validated-handler:Ana");
    }

    [Fact]
    public void Stage1_of_a_valid_command_allocates_nothing()
    {
        var cmd = Valid;
        MessagePipeline.CheckShape(in cmd, out _);   // warm-up (JIT, statics)

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = MessagePipeline.CheckShape(in cmd, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        ok.Should().BeTrue();
        allocated.Should().Be(0);
    }
}
