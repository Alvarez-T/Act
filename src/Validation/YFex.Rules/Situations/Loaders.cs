namespace YFex.Rules;

/// <summary>
/// Loads everything the rules need BEFORE evaluation (Principle A, 04 §2) — all I/O happens here. Must be
/// deterministic for the same input + same data: never <c>DateTime.Now</c> (take the business date from the message).
/// Registered server-side with <c>.UseSituation&lt;TS, TLoader&gt;()</c> (09 §6).
/// </summary>
public interface ISituationLoader<in TFrom, TSituation> where TSituation : ISituation<TSituation>
{
    ValueTask<TSituation> LoadAsync(TFrom source, CancellationToken ct);
}

/// <summary>A message that carries its own situation (thin clients: <c>GetSaleOptions(SaleSituation Situation)</c>).</summary>
public interface ICarriesSituation<TSituation> where TSituation : ISituation<TSituation>
{
    TSituation Situation { get; }
}

/// <summary>
/// Overwrites trusted fields (<see cref="ScopeAttribute"/> fields, actor/role, anything security-relevant) of a
/// client-supplied situation from the server context — a client must never claim another tenant, store or role.
/// </summary>
public interface ISituationSanitizer<TSituation> where TSituation : ISituation<TSituation>
{
    TSituation Sanitize(in TSituation situation);
}

/// <summary>
/// Loader for messages that carry the situation. The sanitizer is REQUIRED (constructor argument) so the
/// "trust the client's situation" path can't be wired by accident.
/// </summary>
public sealed class IdentitySituationLoader<TFrom, TSituation>(ISituationSanitizer<TSituation> sanitizer)
    : ISituationLoader<TFrom, TSituation>
    where TFrom : ICarriesSituation<TSituation>
    where TSituation : ISituation<TSituation>
{
    public ValueTask<TSituation> LoadAsync(TFrom source, CancellationToken ct)
    {
        var s = source.Situation;
        return ValueTask.FromResult(sanitizer.Sanitize(in s));
    }
}
