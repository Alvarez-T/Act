using YFex.Authorization.Public;
using YFex.Rules;

namespace YFex.Cqrs;

/// <summary>
/// Messages that carry the client's rules decision (offline-capable commands, validation 09 §6): the client fills these from
/// <see cref="Decision{TSituation}.RulesetVersion"/> / <see cref="Decision{TSituation}.Fingerprint"/> when it built the command, and
/// <see cref="IApprovalBound.Approvals"/> from the approvals of <see cref="Decision{TSituation}.Digest"/> (moved from YFex.Rules,
/// authorization-consolidation ⚑Z12).
/// </summary>
public interface IDecisionBound : IApprovalBound
{
    RulesetVersion RulesetVersion { get; }
    ulong DecisionFingerprint { get; }
}
