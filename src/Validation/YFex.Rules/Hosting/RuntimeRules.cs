using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// A published runtime rule (<see cref="RuleDataKind.Rule"/>, 05 §8): an ordinary <see cref="RuleDescriptor{TSituation}"/>
/// produced by a <see cref="IRuntimeRuleCodec{TSituation}"/> (e.g. YFex.Rules.Dynamic's compiled spec). The compiled rule set
/// copies the template into its group with its own index, so the engine runs it exactly like a code rule.
/// </summary>
public sealed class RuntimeRuleSnapshot<TSituation>(RuleDescriptor<TSituation> rule, RuleTierPath tierPath, long version, DateOnly effectiveFrom)
    : RulesetSnapshot(rule.Code, tierPath, version, effectiveFrom)
    where TSituation : ISituation<TSituation>
{
    /// <summary>Template (its <see cref="RuleDescriptor{TSituation}.GroupName"/> names a group with a <see cref="DynamicRulePolicy"/>).</summary>
    public RuleDescriptor<TSituation> Rule { get; } = rule;
}

/// <summary>
/// Turns stored runtime-rule payloads into rules (YFex.Rules can't reference the languages that author them; they plug in
/// here — <c>host.UseRuntimeRules(codec)</c>). Implementations validate against the rule set (fields, groups, policies),
/// compile once per version and cache.
/// </summary>
public interface IRuntimeRuleCodec<TSituation> where TSituation : ISituation<TSituation>
{
    /// <summary>Publish-time checks of <paramref name="payload"/> for rule <paramref name="code"/> at <paramref name="tierPath"/>.</summary>
    ValidationResult Validate(RuleSetDefinition<TSituation> set, string code, in RuleTierPath tierPath, ReadOnlySpan<byte> payload);

    /// <summary>Stored form of a valid payload (stable formatting → clean diffs).</summary>
    byte[] Canonicalize(RuleSetDefinition<TSituation> set, ReadOnlySpan<byte> payload);

    /// <summary>Stored record → rule. Throws (Argument/Json/Format/NotSupported exceptions) when it no longer fits the rule set.</summary>
    RuntimeRuleSnapshot<TSituation> Decode(RuleSetDefinition<TSituation> set, RuleDataRecord record);
}
