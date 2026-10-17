using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Persistence;
using YFex.Validation;
using static YFex.Rules.Cells;

namespace YFex.Rules.Admin.Tests;

// A small "pricing" rule set: a tenant/store-editable table + a code rule with a guarded parameter + a sealed rule.

public enum Plan { Basic, Pro }

[Situation("pricing")]
public readonly partial record struct PricingSituation(
    [property: RuleTier] string Tenant,
    [property: RuleTier] string Store,
    [property: RuleEffectiveDate] DateOnly Date,
    Plan Plan,
    [property: Requested] int Seats,
    Money Amount);

[RuleSet<PricingSituation>("pricing")]
public static partial class PricingRules
{
    public static readonly Stage Main = Stage.First("main");
    public static readonly RuleGroup Discounts = new(Main, Resolve.All);
    public static readonly RuleGroup Limits = new(Stage.After(Main, "limits"), Resolve.MostRestrictive);
    /// <summary>Tenants may add their own discounts (runtime rules, ≤ 15%).</summary>
    public static readonly RuleGroup Promos = new(Main, Resolve.All)
        { Dynamic = new([nameof(PricingSituation.Tenant)], DynamicDirectives.Decrease, maxPercent: Percentual.FromPercentage(15)) };
    public static Money Base(in PricingSituation s) => s.Amount;
}

[Rule("pricing.volume", Group = nameof(PricingRules.Discounts))]
public readonly struct VolumeDiscount : IRule<PricingSituation>
{
    public static readonly RuleParameter<Percentual> Rate =
        new("rate", Percentual.FromPercentage(5), [nameof(PricingSituation.Tenant)], guard: new(Percentual.Zero, Percentual.FromPercentage(20)));
    public static bool Applies(in PricingSituation s) => s.Seats >= 10;
    public static void Evaluate(in PricingSituation s, in RuleContext c, ref DirectiveWriter d) => d.Decrease(AdjustTarget.Total, c.Get(Rate));
}

[Sealed, Rule("pricing.min-seats", Group = nameof(PricingRules.Limits))]
public readonly struct MinSeats : IRule<PricingSituation>
{
    public static bool Applies(in PricingSituation s) => true;
    public static void Evaluate(in PricingSituation s, ref DirectiveWriter d) => d.Limit(PricingSituation.Fields.Seats, min: 1);
}

public readonly struct SeatGuard : IRowGuard<SeatTable.Row>
{
    public static void Check(in SeatTable.Row r, ref ViolationWriter w)
    {
        if (r.MaxSeats > 500) w.Add(FieldPath.Of("MaxSeats"), "pricing.seats.above-legal", RawValue.From(500));
    }
}

/// <summary>The app's permissions (the seat table names its own publish permission; the checkout approval of PublishTests).</summary>
[PermissionCatalog]
public static partial class PricingPermissions
{
    [Permission("pricing.rules.seats.publish", Name = "Publicar tabela de assentos")]
    public static partial Permission SeatsPublish { get; }

    [Permission("sales.discount.override", Name = "Aprovar desconto acima do limite")]
    public static partial Permission DiscountOverride { get; }
}

[DecisionTable<PricingSituation>("pricing.seats", HitPolicy.First, Group = nameof(PricingRules.Limits),
    EditableAt = new[] { nameof(PricingSituation.Tenant) }, PublishPermission = PricingPermissions.Ids.SeatsPublish, Guardrail = typeof(SeatGuard))]
public partial class SeatTable
{
    public readonly partial record struct Row(
        [property: When("Plan")] Match<Plan> Plan,
        [property: Then(DirectiveKind.LimitMax, "Seats")] int MaxSeats);

    public static readonly Row[] DefaultRows = [new(Is(Plan.Basic), 5), new(Is(Plan.Pro), 50)];
}

/// <summary>Test caller: authenticated with a mutable set of permission ids (declared ones become its <see cref="PermissionSet"/>).</summary>
public sealed class TestPrincipal : IPrincipalAccessor, IAuthorizationContext
{
    public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);
    public string Name { get; set; } = "ana";
    public bool Authenticated { get; set; } = true;

    public IAuthorizationContext Current => this;
    public ClaimsPrincipal Principal => new(Authenticated ? new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Name, Name)], "test") : new ClaimsIdentity());
    public ActorIdentity Actor => Authenticated ? new ActorIdentity(Name) : ActorIdentity.Anonymous;
    public ActorIdentity? RealActor => null;
    public ActorRelation Relation => ActorRelation.None;
    public string? Scope => null;
    PermissionSet IAuthorizationContext.Permissions
    {
        get
        {
            var set = new PermissionSet.Builder();
            foreach (var id in Permissions) set.Add(id);
            return set.Build();
        }
    }
    public long Version => 0;
    public AccessToken? Token => null;
    public bool IsAuthenticated => Authenticated;
    public bool IsInRole(in Role role) => false;
    public bool HasPermission(in Permission permission) => ((IAuthorizationContext)this).Permissions.Contains(permission);
    public bool HasClaim(in YFex.Authorization.Public.Claim claim) => false;
}

public sealed class AdminFixture
{
    public AdminFixture(IRuleAuditSink? audit = null, Action<RuleAdminOptions>? options = null, IRuleDataSigner? signer = null)
    {
        var services = new ServiceCollection();
        if (audit is not null) services.AddSingleton(audit);
        if (signer is not null) services.AddSingleton(signer);
        services.AddSingleton<ISituationSamples>(Samples);
        services.AddSingleton<IPrincipalAccessor>(User);
        services.AddSingleton<IRuleStore>(Store);
        services.AddSingleton<IEventBus>(Events);
        services.AddYFexRuleSet(Host);
        services.AddYFexRulesAdmin(options);
        // The one message gate (authorization-consolidation 04 §2): RuleAdmin's requirements are enforced through it.
        var registry = CompiledMessagingRegistry.Build(baseline: [new RuleAdminBaseline()]);
        services.AddSingleton(registry);
        YFex.Authorization.ServiceCollectionExtensions.AddYFexAuthorization(services);
        services.AddYFexCqrsAuthorizationConfiguration();
        Services = services.BuildServiceProvider();

        var invoker = new LocalHandlerInvoker(Services, new LocalHandlerInvokerRegistryBuilder().AddRuleAdmin().Build());
        Dispatcher = new LocalDispatcher(invoker, registry, new ManualServerConnection(true), new InMemoryCache(),
            new InMemoryOutbox(new OutboxOptions()), Events, Services);
    }

    public TestPrincipal User { get; } = new();
    public TestSamples Samples { get; } = new();
    public InMemoryRuleStore Store { get; } = new();
    public DefaultEventBus Events { get; } = new();
    public RuleSetHost<PricingSituation> Host { get; } = PricingRules.CreateHost().UseRuntimeRules(new YFex.Rules.Dynamic.DynamicRules<PricingSituation>());
    public IServiceProvider Services { get; }
    public LocalDispatcher Dispatcher { get; }

    public static readonly string Tenant = RuleTierPaths.Format(RuleTierPath.Of(RawValue.From("t1")));
    public static readonly string Store1 = RuleTierPaths.Format(RuleTierPath.Of(RawValue.From("t1"), RawValue.From("s1")));

    public static PricingSituation Situation(Plan plan = Plan.Pro, int seats = 10)
        => new("t1", "s1", new DateOnly(2026, 9, 30), plan, seats, new Money(1000));

    public static byte[] Rows(params SeatTable.Row[] rows)
    {
        PricingRules.Set.TryGetTable("pricing.seats", out var t);
        return t.Serialize(new TableVersion<SeatTable.Row>(new("pricing.seats", 0, RuleTierPath.Code, DateOnly.MinValue), [.. rows]));
    }

    public static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>Always-online <see cref="IServerConnection"/> (the admin commands run on the server).</summary>
internal sealed class ManualServerConnection(bool connected = true) : IServerConnection
{
    public ServerConnectionState State => connected ? ServerConnectionState.Connected : ServerConnectionState.Disconnected;
    public event Action<ServerConnectionChange>? Changed { add { } remove { } }
}
