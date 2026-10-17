using System.Collections.Immutable;

namespace YFex.Rules;

// Hooks for signed rule data (offline 05 §2.1, fixes F6). They live in YFex.Rules, not Rules.Admin, so an implementation
// (YFex.Offline.Rules) needs no RPC/MemoryPack dependency; Rules.Admin's publish pipeline, RuleSetSync and KeyValueRuleDataCache use them.

/// <summary>
/// Signs published rule-data versions at central (offline 05 §2.1, fixes F6). Optional: without one, rule data travels unsigned as
/// before. The publish pipeline calls it once per new version; <c>RuleAdmin.Queries.GetSignedRuleData</c> calls it for every version it serves, so an
/// implementation must keep what it signed and return the same bytes for the same version (a second signature of one version is a
/// conflict on devices). YFex.Offline.Rules implements it with signed bundles of kind <c>rules.data</c>.
/// </summary>
public interface IRuleDataSigner
{
    ValueTask<byte[]> SignAsync(RuleDataRecord record, CancellationToken ct);
}

/// <summary>Outcome of checking one rule-data version on a device.</summary>
public enum RuleDataCheck : byte
{
    Valid = 0,
    /// <summary>No signature, a signature by an unknown key, or content that differs from what was signed (tampered).</summary>
    BadSignature = 1,
    /// <summary>Loaded from the device cache while a newer version of its stream, accepted before, is missing (a rollback: offline 09 S4).</summary>
    RolledBack = 2,
    /// <summary>The same version as an accepted one with other content: a security finding.</summary>
    Conflicting = 3,
    Malformed = 4,
}

/// <summary>A rule-data version with its signed form (opaque to Rules; a signed bundle with YFex.Offline.Rules).</summary>
public sealed record SignedRuleData(RuleDataRecord Record, ReadOnlyMemory<byte> Signature);

/// <summary>
/// Verifies rule data on devices (offline 05 §2.1, fixes F6). Optional: registered, it makes <c>RuleSetSync</c> fetch
/// <c>RuleAdmin.Queries.GetSignedRuleData</c> and refuse what does not verify, and <c>KeyValueRuleDataCache</c> re-verify at every load (with version
/// floors: a version older than one already accepted for its stream is not loaded unless that newer version is cached too).
/// </summary>
public interface IRuleDataVerifier
{
    /// <param name="records">Versions of one rule set, ascending.</param>
    /// <param name="received">True when they just came from central (accepted versions raise the floors); false when loaded from the device cache.</param>
    /// <returns>One check per record, in order.</returns>
    ValueTask<ImmutableArray<RuleDataCheck>> VerifyAsync(IReadOnlyList<SignedRuleData> records, bool received, CancellationToken ct);
}

