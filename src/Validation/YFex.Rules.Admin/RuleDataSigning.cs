using MemoryPack;

namespace YFex.Rules.Admin;

/// <summary>An <see cref="IRuleDataCache"/> that also keeps each version's signed form (for re-verification at load).</summary>
public interface ISignedRuleDataCache : IRuleDataCache
{
    ValueTask SaveSignedAsync(string ruleSet, IReadOnlyList<SignedRuleData> records, CancellationToken ct);
}

/// <summary>Wire form of <see cref="SignedRuleData"/>.</summary>
[MemoryPackable]
public sealed partial record SignedRuleDataPayload(RuleDataPayload Data, byte[] Signature)
{
    public static SignedRuleDataPayload From(SignedRuleData d) => new(RuleDataPayload.From(d.Record), d.Signature.ToArray());
    public SignedRuleData ToSigned() => new(Data.ToRecord(), Signature);
}
