using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.Runtime;
using YFex.Messaging.Rpc;
using YFex.Persistence;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Messaging.Tests.Fixtures;

// Rules enforcement (validation Phase 4, stages 3–4): a small checkout rule set evaluated by the dispatch pipeline.

public enum PayKind { Cash, Card }

[Situation("checkout")]
public readonly partial record struct CheckoutSituation(
    [property: RuleTier] string Tenant,
    [property: RuleEffectiveDate] DateOnly Date,
    bool IsManager,
    PayKind Pay,
    [property: Requested] int Installments,
    [property: Requested] Percentual Discount,
    Money Amount);

[RuleSet<CheckoutSituation>("checkout")]
public static partial class CheckoutRules
{
    public static readonly Stage Main = Stage.First("main");
    public static readonly RuleGroup Discounts = new(Main, Resolve.All);
    public static readonly RuleGroup Limits = new(Stage.After(Main, "limits"), Resolve.MostRestrictive);

    public static Money Base(in CheckoutSituation s) => s.Amount;
}

[Rule("checkout.cash-1x", Group = nameof(CheckoutRules.Limits))]
public readonly struct CashSingleInstallment : IRule<CheckoutSituation>
{
    public static bool Applies(in CheckoutSituation s) => s.Pay == PayKind.Cash;
    public static void Evaluate(in CheckoutSituation s, ref DirectiveWriter d) => d.Limit(CheckoutSituation.Fields.Installments, max: 1);
}

[Rule("checkout.discount", Group = nameof(CheckoutRules.Discounts))]
public readonly struct ManualDiscount : IRule<CheckoutSituation>
{
    public static bool Applies(in CheckoutSituation s) => s.Discount > Percentual.Zero;
    public static void Evaluate(in CheckoutSituation s, ref DirectiveWriter d)
    {
        d.Decrease(AdjustTarget.Total, s.Discount);
        d.Limit(CheckoutSituation.Fields.Discount, max: Percentual.FromPercentage(s.IsManager ? 15 : 5),
            onExceed: Exceed.RequireApproval, permission: "sales.discount.override");
    }
}

public partial class CheckoutAggregate
{
    public static partial class Commands
    {
        public sealed partial record CloseCheckout(string Tenant, DateOnly Date, PayKind Pay, int Installments, Percentual Discount, Money Amount,
            RulesetVersion RulesetVersion = default, ulong DecisionFingerprint = 0, IReadOnlyList<OverrideToken>? Approvals = null)
            : ICommand<Money>, IDecisionBound;
    }

    public static partial class Queries
    {
        /// <summary>Thin clients ask the server for the decision (09 §7).</summary>
        public sealed partial record GetCheckoutOptions(CheckoutSituation Situation) : IQuery<int>, ICarriesSituation<CheckoutSituation>;
    }
}

/// <summary>Stand-in for the authenticated actor (server-side identity, never from the client).</summary>
public sealed class ActorStub { public bool IsManager { get; set; } }

public sealed class CheckoutLoader(ActorStub actor, StageLog log) : ISituationLoader<CheckoutAggregate.Commands.CloseCheckout, CheckoutSituation>
{
    public ValueTask<CheckoutSituation> LoadAsync(CheckoutAggregate.Commands.CloseCheckout c, CancellationToken ct)
    {
        log.Entries.Add("gather");
        return ValueTask.FromResult(new CheckoutSituation(c.Tenant, c.Date, actor.IsManager, c.Pay, c.Installments, c.Discount, c.Amount));
    }
}

/// <summary>Overwrites trusted fields of a client-supplied situation.</summary>
public sealed class CheckoutSanitizer(ActorStub actor) : ISituationSanitizer<CheckoutSituation>
{
    public CheckoutSituation Sanitize(in CheckoutSituation s) => s with { IsManager = actor.IsManager };
}

public sealed class CloseCheckoutHandler(StageLog log) : IRuledCommandHandler<CheckoutAggregate.Commands.CloseCheckout, Money, CheckoutSituation>
{
    public Task<Money> HandleAsync(Valid<CheckoutAggregate.Commands.CloseCheckout> command, Decision<CheckoutSituation> decision, CancellationToken ct)
    {
        log.Entries.Add("ruled-handler");
        return Task.FromResult(decision.Totals.Final.As<Money>());
    }
}

public sealed class ClassicCloseCheckoutHandler : ICommandHandler<CheckoutAggregate.Commands.CloseCheckout, Money>
{
    public Task<Money> HandleAsync(CheckoutAggregate.Commands.CloseCheckout command, CancellationToken ct) => Task.FromResult(Money.Zero);
}

public sealed class GetCheckoutOptionsHandler(IDecisionAccessor<CheckoutSituation> decisions) : IQueryHandler<CheckoutAggregate.Queries.GetCheckoutOptions, int>
{
    public Task<int> HandleAsync(CheckoutAggregate.Queries.GetCheckoutOptions query, CancellationToken ct)
        => Task.FromResult(decisions.Current is { } d && d.TryGetMax(CheckoutSituation.Fields.Installments, out int max) ? max : -1);
}

public sealed class CheckoutContextRule(StageLog log) : IContextRule<CheckoutAggregate.Commands.CloseCheckout>
{
    public ValueTask CheckAsync(CheckoutAggregate.Commands.CloseCheckout value, ViolationCollector violations, CancellationToken ct)
    {
        log.Entries.Add("context");
        return ValueTask.CompletedTask;
    }
}

public sealed class CheckoutBaseline(DecisionMismatchPolicy mismatch, Func<SituationPathMap, SituationPathMap>? paths = null)
    : IAggregateConfiguration<CheckoutAggregate>
{
    public void Configure(AggregateConfigurationBuilder<CheckoutAggregate> b)
    {
        var close = b.Command<CheckoutAggregate.Commands.CloseCheckout, Money>()
            .Authorize((u, c) => true)
            .UseSituation<CheckoutSituation, CheckoutLoader>()
            .Enforce(CheckoutRules.Set)
            .OnDecisionMismatch(mismatch)
            .Validate<CheckoutContextRule>();
        if (paths is not null) close.MapSituationPaths(paths);
        b.Query<CheckoutAggregate.Queries.GetCheckoutOptions, int>()
            .UseSituation<CheckoutSituation, IdentitySituationLoader<CheckoutAggregate.Queries.GetCheckoutOptions, CheckoutSituation>>()
            .Enforce(CheckoutRules.Set);
    }
}

public sealed class CheckoutFixture
{
    public CheckoutFixture(DecisionMismatchPolicy mismatch = DecisionMismatchPolicy.Reject, bool ruledHandler = true, bool decorateInvoker = false,
        Func<SituationPathMap, SituationPathMap>? paths = null)
    {
        Registry = CompiledMessagingRegistry.Build(baseline: [new CheckoutBaseline(mismatch, paths)]);
        var services = new ServiceCollection();
        services.AddSingleton(Log);
        services.AddSingleton(Actor);
        services.AddSingleton<ISituationSanitizer<CheckoutSituation>, CheckoutSanitizer>();
        services.AddSingleton<IDecisionAccessor<CheckoutSituation>, DecisionAccessor<CheckoutSituation>>();
        services.AddSingleton<IOverrideTokenVerifier>(Tokens);
        services.AddTransient<ICommandHandler<CheckoutAggregate.Commands.CloseCheckout, Money>, ClassicCloseCheckoutHandler>();
        if (ruledHandler)
            services.AddTransient<IRuledCommandHandler<CheckoutAggregate.Commands.CloseCheckout, Money, CheckoutSituation>, CloseCheckoutHandler>();
        services.AddTransient<IQueryHandler<CheckoutAggregate.Queries.GetCheckoutOptions, int>, GetCheckoutOptionsHandler>();
        Services = services.BuildServiceProvider();

        IHandlerInvoker invoker = new LocalHandlerInvoker(Services, new LocalHandlerInvokerRegistryBuilder()
            .AddCommand<CheckoutAggregate.Commands.CloseCheckout, Money>()
            .AddQuery<CheckoutAggregate.Queries.GetCheckoutOptions, int>()
            .Build());
        Invoker = decorateInvoker ? new ValidatingHandlerInvoker(invoker, Registry, Services) : invoker;
        Dispatcher = new LocalDispatcher(Invoker, Registry, new ManualNetworkStatus(true), new InMemoryCache(),
            new InMemoryOutbox(new OutboxOptions()), new DefaultEventBus(), Services);
    }

    public StageLog Log { get; } = new();
    public ActorStub Actor { get; } = new();
    public HmacOverrideTokens Tokens { get; } = new(new Dictionary<string, byte[]> { ["k1"] = new byte[32] }, "k1", TimeSpan.FromMinutes(15));
    public CompiledMessagingRegistry Registry { get; }
    public IServiceProvider Services { get; }
    public IHandlerInvoker Invoker { get; }
    public LocalDispatcher Dispatcher { get; }
}
