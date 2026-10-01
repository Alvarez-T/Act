namespace YFex.Rules;

/// <summary>
/// Stable reference to the exact rule definition that acted (06 §2). Persist the CODE (stable across builds), never
/// the in-memory index.
/// <list type="bullet">
/// <item>code rule → <c>("loyalty.gold", Version: 1, Tier: 0, DataVersion: parameters version)</c></item>
/// <item>table row → <c>("sale.installments", Version: 1, Tier: 1, DataVersion: table version 42, RowId)</c></item>
/// </list>
/// <see cref="Tier"/> = depth of the <see cref="RuleTierPath"/> the acting data was published at (0 = code).
/// </summary>
public readonly record struct RuleRef(string Code, int Version, int Tier, long DataVersion, Guid RowId = default)
{
    public bool IsTableRow => RowId != Guid.Empty;
    public override string ToString() => IsTableRow ? $"{Code}#{RowId:N} v{DataVersion} tier {Tier}" : $"{Code} v{Version} tier {Tier}";
}

/// <summary>
/// Code version (hash of every code rule's code + <c>Version</c>, emitted by the generator) + data version (highest
/// published table/parameter/override version used). Stored with every decision.
/// </summary>
public readonly record struct RulesetVersion(ulong CodeVersion, long DataVersion);

/// <summary>Which split condition of a rule was false (06 §4). <see cref="None"/> = no specific condition.</summary>
public readonly record struct ConditionId(ushort RuleId, byte Index)
{
    public static ConditionId None => new(ushort.MaxValue, byte.MaxValue);
    public bool IsNone => RuleId == ushort.MaxValue;
}
