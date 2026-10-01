using YFex.Cqrs.Runtime;
using YFex.Rules;

namespace YFex.Cqrs.Configuration;

/// <summary>Builder-side state of <c>.UseSituation&lt;TS, TLoader&gt;().Enforce(set).OnDecisionMismatch(p)</c>.</summary>
internal abstract class RulesConfig
{
    public EnforceOptions Options { get; set; } = EnforceOptions.Default;
    public abstract RulesStage Build(Type messageType, bool isQuery);
}

internal sealed class RulesConfig<TMessage, TSituation>(Func<IServiceProvider, ISituationLoader<TMessage, TSituation>> loader) : RulesConfig
    where TSituation : ISituation<TSituation>
{
    public RuleSetDefinition<TSituation>? Set { get; set; }

    public override RulesStage Build(Type messageType, bool isQuery)
        => Set is null
            ? throw new InvalidOperationException($"'{messageType.Name}' configures .UseSituation<{typeof(TSituation).Name}, …>() without .Enforce(ruleSet).")
            : new RulesStage<TMessage, TSituation>(loader, Set, Options, isQuery);

    public static RulesConfig<TMessage, TSituation> Expect(RulesConfig? current, Type messageType)
        => current as RulesConfig<TMessage, TSituation>
           ?? throw new InvalidOperationException(
               $"'{messageType.Name}': call .UseSituation<{typeof(TSituation).Name}, TLoader>() before .Enforce(...) (same situation type).");
}
