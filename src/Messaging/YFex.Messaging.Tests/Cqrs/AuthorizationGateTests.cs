using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Authorization.Testing;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Diagnostics;
using YFex.Messaging.Rpc;
using YFex.Messaging.Rpc.Wolverine;
using YFex.Messaging.Tests.Fixtures;

namespace YFex.Messaging.Tests.Cqrs;

/// <summary>Permissions of the gate tests (a real catalog: ids are declared, so they can be granted).</summary>
[PermissionCatalog]
public static partial class GatePermissions
{
    [Permission("gate.orders.edit", Name = "Editar pedidos")]
    public static partial Permission OrdersEdit { get; }

    [Permission("gate.orders.void", Name = "Cancelar pedidos")]
    public static partial Permission OrdersVoid { get; }
}

/// <summary>A command whose requirement is declared ONLY by an attribute on the record (⚑Z6) — enforced on dispatch (D1).</summary>
public partial class GateAggregate
{
    public static partial class Commands
    {
        [RequirePermission(GatePermissions.Ids.OrdersVoid)]
        public partial record VoidOrder(int Id) : ICommand;
    }
}

/// <summary>
/// The ONE message gate (authorization-consolidation 04 §2, Phase 2a): Cqrs builder verbs, <c>ForMessage&lt;T&gt;()</c> rows and
/// <c>[RequirePermission]</c> on records feed one policy that LocalDispatcher, the server invoker and Wolverine enforce through
/// <see cref="IMessageAuthorization"/> (D1); named policies and requirement classes really run (D3); roles are case-insensitive
/// (D4); uncatalogued permissions fail at startup (D5); a missing gate fails closed; denials are emitted as events.
/// </summary>
[Trait("Category", "Cqrs")]
public sealed class AuthorizationGateTests
{
    private static readonly TestAggregate.Commands.DeleteCommand Delete = new(1);

    private sealed class Config(Action<CommandConfigurationBuilder<TestAggregate.Commands.DeleteCommand>> configure) : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b) => configure(b.Command<TestAggregate.Commands.DeleteCommand>());
    }

    private sealed class NoRequirements : IAggregateConfiguration<TestAggregate>
    {
        public void Configure(AggregateConfigurationBuilder<TestAggregate> b) => b.Command<TestAggregate.Commands.DeleteCommand>();
    }

    private sealed class Harness
    {
        public required LocalDispatcher Dispatcher { get; init; }
        public required TestPrincipalAccessor Caller { get; init; }
        public required IServiceProvider Services { get; init; }
        public required CompiledMessagingRegistry Registry { get; init; }

        public async Task<QueueableResult> SendAsync<TCommand>(TCommand command, IAuthorizationContext caller) where TCommand : ICommand
        {
            Caller.Current = caller;
            return await Dispatcher.CommandAsync(command);
        }
    }

    private static Harness Build(IAggregateConfiguration config, Action<AuthorizationConfigurator>? authorization = null,
        Action<IServiceCollection>? services = null, bool wireAuthorization = true, bool exportCqrs = true)
    {
        var registry = CompiledMessagingRegistry.Build([config]);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(registry);
        if (wireAuthorization) collection.AddYFexAuthorization(authorization);
        if (exportCqrs) collection.AddYFexCqrsAuthorizationConfiguration();
        var caller = new TestPrincipalAccessor();
        collection.AddSingleton<IPrincipalAccessor>(caller);
        collection.AddTransient<ICommandHandler<TestAggregate.Commands.DeleteCommand>, TestDispatcherFixture.DeleteCommandHandler>();
        collection.AddTransient<ICommandHandler<GateAggregate.Commands.VoidOrder>, VoidOrderHandler>();
        services?.Invoke(collection);
        var sp = collection.BuildServiceProvider();
        var dispatcher = new LocalDispatcher(
            new LocalHandlerInvoker(sp, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()),
            registry, new ManualServerConnection(true), new InMemoryCache(), new InMemoryOutbox(new OutboxOptions()), new DefaultEventBus(), sp);
        return new Harness { Dispatcher = dispatcher, Caller = caller, Services = sp, Registry = registry };
    }

    public sealed class VoidOrderHandler : ICommandHandler<GateAggregate.Commands.VoidOrder>
    {
        public Task HandleAsync(GateAggregate.Commands.VoidOrder command, CancellationToken ct) => Task.CompletedTask;
    }

    private static IAuthorizationContext Holder(params Permission[] permissions) => TestCaller.Authenticated("ana").WithPermissions(permissions).Build();

    private static void ShouldBeDenied(QueueableResult result, string? naming = null)
    {
        result.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().BeOneOf(ErrorKind.Forbidden, ErrorKind.Unauthenticated);
        if (naming is not null) error.Message.Should().Contain(naming);
    }

    // ── D1: every declaration path is enforced on dispatch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cqrs_configuration_permission_allows_the_holder()
    {
        var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)));
        (await fx.SendAsync(Delete, Holder(GatePermissions.OrdersEdit))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Cqrs_configuration_permission_denies_naming_the_missing_permission()
    {
        var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)));
        ShouldBeDenied(await fx.SendAsync(Delete, Holder()), "gate.orders.edit");
    }

    [Fact]
    public async Task ForMessage_rows_are_enforced_on_cqrs_dispatch()
    {
        var fx = Build(new NoRequirements(), a => a.AddConfiguration(new DelegateConfig(b =>
            b.ForMessage<TestAggregate.Commands.DeleteCommand>().RequirePermission(GatePermissions.OrdersVoid))));
        ShouldBeDenied(await fx.SendAsync(Delete, Holder(GatePermissions.OrdersEdit)), "gate.orders.void");
        (await fx.SendAsync(Delete, Holder(GatePermissions.OrdersVoid))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RequirePermission_attribute_on_a_record_is_enforced_without_wiring()
    {
        var fx = Build(new NoRequirements());
        ShouldBeDenied(await fx.SendAsync(new GateAggregate.Commands.VoidOrder(7), Holder(GatePermissions.OrdersEdit)), "gate.orders.void");
        (await fx.SendAsync(new GateAggregate.Commands.VoidOrder(7), Holder(GatePermissions.OrdersVoid))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Configuration_and_ForMessage_rows_merge_with_AND()
    {
        var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)), a => a.AddConfiguration(new DelegateConfig(b =>
            b.ForMessage<TestAggregate.Commands.DeleteCommand>().RequirePermission(GatePermissions.OrdersVoid))));
        ShouldBeDenied(await fx.SendAsync(Delete, Holder(GatePermissions.OrdersEdit)), "gate.orders.void");
        (await fx.SendAsync(Delete, Holder(GatePermissions.OrdersEdit, GatePermissions.OrdersVoid))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Unauthenticated_caller_is_told_to_authenticate()
    {
        var fx = Build(new Config(c => c.RequireAuthenticated()));
        var anonymous = await fx.SendAsync(Delete, TestCaller.Anonymous);
        ShouldBeDenied(anonymous, "Authentication required");
        anonymous.TryGetError(out var error).Should().BeTrue();
        error.Kind.Should().Be(ErrorKind.Unauthenticated);
        (await fx.SendAsync(Delete, Holder())).IsSuccess.Should().BeTrue();
    }

    // ── D3: named policies and requirement classes actually run ──────────────────────────────────────────────────────

    private sealed class ManagersOnly : INamedPolicyHandler
    {
        public PolicyName Name { get; } = PolicyName.Parse("managers-only");
        public ValueTask<bool> HandleAsync(IAuthorizationContext caller, object? resource, CancellationToken cancellationToken)
            => new(caller.Actor.Subject == "manager");
    }

    [Fact]
    public async Task RequirePolicy_is_decided_by_its_registered_handler()
    {
        var fx = Build(new Config(c => c.RequirePolicy(PolicyName.Parse("managers-only"))),
            services: s => s.AddSingleton<INamedPolicyHandler, ManagersOnly>());
        ShouldBeDenied(await fx.SendAsync(Delete, TestCaller.Authenticated("cashier").Build()), "managers-only");
        (await fx.SendAsync(Delete, TestCaller.Authenticated("manager").Build())).IsSuccess.Should().BeTrue();
    }

    public sealed class SmallIdsOnly : IAuthorizationRequirement<TestAggregate.Commands.DeleteCommand>
    {
        public bool IsSatisfied(IAuthorizationContext caller, TestAggregate.Commands.DeleteCommand command) => command.Id < 100;
    }

    public sealed class RegisterOpen : IAsyncAuthorizationRequirement<TestAggregate.Commands.DeleteCommand>
    {
        public bool Open { get; set; } = true;
        public async ValueTask<bool> IsSatisfiedAsync(IAuthorizationContext caller, TestAggregate.Commands.DeleteCommand command, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Open;
        }
    }

    [Fact]
    public async Task Authorize_with_a_sync_requirement_class()
    {
        var fx = Build(new Config(c => c.Authorize<SmallIdsOnly>()), services: s => s.AddSingleton<SmallIdsOnly>());
        (await fx.SendAsync(new TestAggregate.Commands.DeleteCommand(5), Holder())).IsSuccess.Should().BeTrue();
        ShouldBeDenied(await fx.SendAsync(new TestAggregate.Commands.DeleteCommand(500), Holder()), "SmallIdsOnly");
    }

    [Fact]
    public async Task Authorize_with_an_async_requirement_class()
    {
        var fx = Build(new Config(c => c.Authorize<RegisterOpen>()), services: s => s.AddSingleton<RegisterOpen>());
        (await fx.SendAsync(Delete, Holder())).IsSuccess.Should().BeTrue();
        fx.Services.GetRequiredService<RegisterOpen>().Open = false;
        ShouldBeDenied(await fx.SendAsync(Delete, Holder()), "RegisterOpen");
    }

    [Fact]
    public async Task Inline_predicate_receives_the_caller_and_the_command()
    {
        var fx = Build(new Config(c => c.Authorize((caller, command) => command.Id < 10 || caller.Actor.Subject == "boss")));
        (await fx.SendAsync(new TestAggregate.Commands.DeleteCommand(3), Holder())).IsSuccess.Should().BeTrue();
        ShouldBeDenied(await fx.SendAsync(new TestAggregate.Commands.DeleteCommand(30), Holder()));
        (await fx.SendAsync(new TestAggregate.Commands.DeleteCommand(30), TestCaller.Authenticated("boss").Build())).IsSuccess.Should().BeTrue();
    }

    // ── D4: role names compare case-insensitively ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Roles_are_case_insensitive_on_dispatch()
    {
        var fx = Build(new Config(c => c.RequireRoles(Role.Parse("Supervisor"))));
        (await fx.SendAsync(Delete, TestCaller.Authenticated().WithRoles(Role.Parse("SUPERVISOR")).Build())).IsSuccess.Should().BeTrue();
        ShouldBeDenied(await fx.SendAsync(Delete, TestCaller.Authenticated().WithRoles(Role.Parse("caixa")).Build()));
    }

    // ── D5 + fail-closed startup ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Uncatalogued_cqrs_permission_fails_startup_listing_it()
    {
        var act = () => Build(new Config(c => c.RequirePermission(Permission.Parse("gate.orders.typo"))));
        act.Should().Throw<AuthorizationConfigurationException>().WithMessage("*gate.orders.typo*DeleteCommand*");
    }

    [Fact]
    public void Requirements_without_AddYFexAuthorization_fail_at_construction()
    {
        var act = () => Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)), wireAuthorization: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*AddYFexAuthorization*");
    }

    [Fact]
    public void Requirements_not_exported_to_the_authorization_registry_fail_at_construction()
    {
        var act = () => Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)), exportCqrs: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*AddYFexCqrsAuthorizationConfiguration*");
    }

    // ── Server invoker and Wolverine dispatcher ask the same gate ────────────────────────────────────────────────────

    [Fact]
    public async Task ValidatingHandlerInvoker_denies_with_the_reason()
    {
        var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)));
        var invoker = new ValidatingHandlerInvoker(
            new LocalHandlerInvoker(fx.Services, YFexLocalHandlerRegistrations.BuildLocalHandlerInvokerRegistry()), fx.Registry, fx.Services);
        fx.Caller.Current = Holder();

        var act = () => invoker.InvokeAsync(Delete, default);
        (await act.Should().ThrowAsync<ErrorResultException>()).Which.TypedError.Message.Should().Contain("gate.orders.edit");
    }

    [Fact]
    public async Task Wolverine_dispatcher_denies_configuration_and_attribute_requirements_before_the_bus()
    {
        var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)));
        var wolverine = new WolverineLocalDispatcher(bus: null!, new DefaultEventBus(), fx.Registry, fx.Services);   // a denial never reaches the bus
        fx.Caller.Current = Holder();

        ShouldBeDenied(await wolverine.CommandAsync(Delete), "gate.orders.edit");
        ShouldBeDenied(await wolverine.CommandAsync(new GateAggregate.Commands.VoidOrder(1)), "gate.orders.void");
    }

    [Fact]
    public async Task Wolverine_dispatcher_denies_ForMessage_rows()
    {
        var fx = Build(new NoRequirements(), a => a.AddConfiguration(new DelegateConfig(b =>
            b.ForMessage<TestAggregate.Commands.DeleteCommand>().RequirePermission(GatePermissions.OrdersVoid))));
        var wolverine = new WolverineLocalDispatcher(bus: null!, new DefaultEventBus(), fx.Registry, fx.Services);
        fx.Caller.Current = Holder(GatePermissions.OrdersEdit);

        ShouldBeDenied(await wolverine.CommandAsync(Delete), "gate.orders.void");
    }

    // ── Events (⚑Z17): server-side denials go through the core probe ─────────────────────────────────────────────────

    [Fact]
    public async Task Denied_is_emitted_through_the_core_probe()
    {
        var engine = new CapturingEngine();
        Operations.Install(engine, allowReinstall: true);
        try
        {
            var fx = Build(new Config(c => c.RequirePermission(GatePermissions.OrdersEdit)));
            await fx.SendAsync(Delete, TestCaller.Authenticated("denied-event-subject").Build());
        }
        finally
        {
            Operations.Uninstall(engine);
        }

        var denied = engine.Denied.Should().ContainSingle(e => e.Actor.Subject == "denied-event-subject").Subject;
        denied.Missing.Should().Be("gate.orders.edit");
        denied.Reason.Should().Be(DenialKind.MissingPermission);
        denied.Target.Should().Be(nameof(TestAggregate.Commands.DeleteCommand));
    }

    private sealed class DelegateConfig(Action<AuthorizationBuilder> configure) : IAuthorizationConfiguration
    {
        public void Configure(AuthorizationBuilder builder) => configure(builder);
    }

    private sealed class CapturingEngine : IOperationEngine
    {
        public System.Collections.Concurrent.ConcurrentBag<AuthorizationEvent> Denied { get; } = [];
        public TraceContext Current => default;
        public bool IsEnabled(OperationDescriptor descriptor, LogLevel level) => ReferenceEquals(descriptor, AuthorizationEvents.Denied);
        public ActiveOperation Start(OperationDescriptor descriptor) => default;
        public ActiveOperation Start<T>(OperationDescriptor<T> descriptor, in T value) => default;
        public ActiveOperation StartLinked(OperationDescriptor descriptor, in OperationLink link) => default;
        public void Mark(OperationDescriptor descriptor, LogLevel level, OperationOutcome outcome, int code) { }
        public void Mark<T>(OperationDescriptor<T> descriptor, in T value, LogLevel level, OperationOutcome outcome, int code)
        {
            if (value is AuthorizationEvent e) Denied.Add(e);
        }
    }
}
