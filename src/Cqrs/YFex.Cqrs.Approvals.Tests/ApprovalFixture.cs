using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Authorization.Testing;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.Runtime;
using YFex.Installations;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Persistence;

namespace YFex.Cqrs.ApprovalsTests;

[PermissionCatalog]
public static partial class SalesPermissions
{
    [Permission("sales.void", Name = "Cancelar venda")]
    public static partial Permission SaleVoid { get; }

    [Permission("sales.void.approve", Name = "Aprovar cancelamento de venda")]
    public static partial Permission SaleVoidApprove { get; }

    [Permission("sales.refund.approve", Name = "Aprovar devolução")]
    public static partial Permission RefundApprove { get; }
}

public partial class Sales
{
    public static partial class Commands
    {
        public sealed record VoidSale(Guid SaleId, IReadOnlyList<ApprovalToken>? Approvals = null) : ICommand<int>, IApprovalBound;

        /// <summary>Same-person approval allowed (separation of duties off), bound to an integer.</summary>
        public sealed record RefundSale(long Number, IReadOnlyList<ApprovalToken>? Approvals = null) : ICommand<int>, IApprovalBound;
    }
}

public sealed class SalesBaseline : IAggregateConfiguration<Sales>
{
    public void Configure(AggregateConfigurationBuilder<Sales> b)
    {
        b.Command<Sales.Commands.VoidSale, int>().RequirePermission(SalesPermissions.SaleVoid)
            .RequireApproval(SalesPermissions.SaleVoidApprove, bind: command => command.SaleId);
        b.Command<Sales.Commands.RefundSale, int>().RequireAuthenticated()
            .RequireApproval(SalesPermissions.RefundApprove, bind: command => command.Number, separationOfDuties: false);
    }
}

public sealed class VoidSaleHandler : ICommandHandler<Sales.Commands.VoidSale, int>
{
    public Task<int> HandleAsync(Sales.Commands.VoidSale command, CancellationToken ct) => Task.FromResult(1);
}

public sealed class RefundSaleHandler : ICommandHandler<Sales.Commands.RefundSale, int>
{
    public Task<int> HandleAsync(Sales.Commands.RefundSale command, CancellationToken ct) => Task.FromResult(2);
}

/// <summary>Mutable test caller (switch between cashier and supervisor on the same terminal).</summary>
public sealed class SwitchablePrincipal : IPrincipalAccessor, IAuthorizationContext
{
    public HashSet<string> Granted { get; } = new(StringComparer.Ordinal);
    public string Subject { get; set; } = "cashier";
    public bool Authenticated { get; set; } = true;
    public string? CurrentScope { get; set; }
    public ActorRelation CurrentRelation { get; set; }

    public SwitchablePrincipal As(string subject, string? scope = null, params Permission[] permissions)
    {
        Subject = subject;
        CurrentScope = scope;
        Authenticated = true;
        CurrentRelation = ActorRelation.None;
        Granted.Clear();
        foreach (var permission in permissions) Granted.Add(permission.Value);
        return this;
    }

    public IAuthorizationContext Current => this;
    public ClaimsPrincipal Principal => new(Authenticated ? new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, Subject)], "test") : new ClaimsIdentity());
    public ActorIdentity Actor => Authenticated ? new ActorIdentity(Subject) : ActorIdentity.Anonymous;
    public ActorIdentity? RealActor => CurrentRelation == ActorRelation.None ? null : new ActorIdentity("support");
    public ActorRelation Relation => CurrentRelation;
    public string? Scope => CurrentScope;
    public PermissionSet Permissions
    {
        get
        {
            var set = new PermissionSet.Builder();
            foreach (var id in Granted) set.Add(id);
            return set.Build();
        }
    }
    public long Version => 0;
    public AccessToken? Token => null;
    public bool IsAuthenticated => Authenticated;
    public bool IsInRole(in Role role) => false;
    public bool HasPermission(in Permission permission) => Permissions.Contains(permission);
    public bool HasClaim(in YFex.Authorization.Public.Claim claim) => false;
}

public sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Stand-in for the device signature check (the real one is YFex.Authorization.Installations.Central's): accepts signature "ok".</summary>
public sealed class FakeDeviceSignatures : IApprovalSignatureVerifier
{
    public ApprovalTokenKind Kind => ApprovalTokenKind.Device;
    public ValueTask<ApprovalCheck> VerifySignatureAsync(ApprovalToken token, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(token.Signature.Span.SequenceEqual("ok"u8) ? ApprovalCheck.Valid : ApprovalCheck.InvalidSignature);
}

public sealed class FixedOrigin : IApprovalOriginAccessor
{
    public ApprovalOrigin Current { get; set; }
}

public sealed class RecordingAuditSink : IDecisionAuditSink
{
    public List<object> Flagged { get; } = [];
    public ValueTask OnFlaggedAsync(object message, object decision, ulong clientFingerprint, CancellationToken ct)
    {
        Flagged.Add(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Always-online <see cref="IServerConnection"/> (the approvals run on the server).</summary>
internal sealed class ManualServerConnection(bool connected = true) : IServerConnection
{
    public ServerConnectionState State => connected ? ServerConnectionState.Connected : ServerConnectionState.Disconnected;
    public event Action<ServerConnectionChange>? Changed { add { } remove { } }
}

public sealed class ApprovalFixture
{
    public static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    public static readonly InstallationId Terminal = new(Guid.Parse("0199e3a0-0000-7000-8000-000000000007"));

    public ApprovalFixture(Action<ApprovalOptions>? options = null, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IPrincipalAccessor>(User);
        collection.AddSingleton<TimeProvider>(Time);
        collection.AddSingleton<IApprovalOriginAccessor>(Origin);
        collection.AddSingleton<IDecisionAuditSink>(Audit);
        var registry = CompiledMessagingRegistry.Build(baseline: [new ApprovalsBaseline(), new SalesBaseline()]);
        collection.AddSingleton(registry);
        YFex.Authorization.ServiceCollectionExtensions.AddYFexAuthorization(collection);
        collection.AddYFexCqrsAuthorizationConfiguration();
        collection.AddYFexCqrsApprovals(options);
        collection.AddTransient<ICommandHandler<Sales.Commands.VoidSale, int>, VoidSaleHandler>();
        collection.AddTransient<ICommandHandler<Sales.Commands.RefundSale, int>, RefundSaleHandler>();
        services?.Invoke(collection);
        Services = collection.BuildServiceProvider();

        var invoker = new LocalHandlerInvoker(Services, new LocalHandlerInvokerRegistryBuilder()
            .AddApprovals()
            .AddCommand<Sales.Commands.VoidSale, int>()
            .AddCommand<Sales.Commands.RefundSale, int>()
            .Build());
        Dispatcher = new LocalDispatcher(invoker, registry, new ManualServerConnection(true), new InMemoryCache(),
            new InMemoryOutbox(new OutboxOptions()), new DefaultEventBus(), Services);
    }

    public SwitchablePrincipal User { get; } = new();
    public ManualTime Time { get; } = new(Start);
    public FixedOrigin Origin { get; } = new();
    public RecordingAuditSink Audit { get; } = new();
    public IServiceProvider Services { get; }
    public LocalDispatcher Dispatcher { get; }

    /// <summary>The supervisor approves <paramref name="digest"/> (own login on the terminal) and the cashier logs back in.</summary>
    public async Task<ApprovalToken> ApproveAsync(Permission permission, YFex.Cryptography.Hash256 digest, string supervisor = "sup",
        string? supervisorScope = null)
    {
        string cashier = User.Subject;
        string? scope = User.CurrentScope;
        var granted = User.Granted.ToArray();
        User.As(supervisor, supervisorScope, permission);
        var result = await Dispatcher.CommandAsync<Approvals.Commands.Approve, ApprovalToken>(new(permission, digest));
        User.Subject = cashier;
        User.CurrentScope = scope;
        User.Granted.Clear();
        foreach (var id in granted) User.Granted.Add(id);
        result.TryGetValue(out ApprovalToken? token).Should().BeTrue(result.ToString());
        return token!;
    }
}
