using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace YFex.Privacy;

/// <summary>
/// What kind of personal data a value is (privacy design 02 §3). Open set: apps add categories, so this is an interned class
/// (same pattern as <see cref="RawType"/>), not an enum. <see cref="Key"/> is the persisted form; <see cref="Id"/> is a
/// process-local index that must never be persisted.
/// </summary>
public sealed class DataCategory : IEquatable<DataCategory>
{
    private static readonly object s_gate = new();
    private static readonly ConcurrentDictionary<string, DataCategory> s_byKey = new(StringComparer.Ordinal);
    private static DataCategory[] s_byId = new DataCategory[32];
    private static int s_count;

    /// <summary>Not personal data.</summary>
    public static DataCategory None { get; } = Register("none", DataSensitivity.Public);

    /// <summary>CPF, RG, name, customer number.</summary>
    public static DataCategory Identification { get; } = Register("identification", DataSensitivity.Personal);

    /// <summary>E-mail, phone, address, CEP.</summary>
    public static DataCategory Contact { get; } = Register("contact", DataSensitivity.Personal);

    /// <summary>Birth date, gender, marital status.</summary>
    public static DataCategory Demographic { get; } = Register("demographic", DataSensitivity.Personal);

    /// <summary>Geolocation, IP address.</summary>
    public static DataCategory Location { get; } = Register("location", DataSensitivity.Personal);

    /// <summary>Usage, navigation, purchase history.</summary>
    public static DataCategory Behavioral { get; } = Register("behavioral", DataSensitivity.Personal);

    /// <summary>Unstructured text that may contain anything (scrubbed when it leaves the process).</summary>
    public static DataCategory FreeText { get; } = Register("free-text", DataSensitivity.Personal);

    /// <summary>Card, bank account, income.</summary>
    public static DataCategory Financial { get; } = Register("financial", DataSensitivity.Financial);

    /// <summary>Passwords, tokens, keys.</summary>
    public static DataCategory Credentials { get; } = Register("credentials", DataSensitivity.Secret);

    /// <summary>Art. 5 II: diagnoses, prescriptions, medications, test results.</summary>
    public static DataCategory Health { get; } = Register("health", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II: fingerprint, face template.</summary>
    public static DataCategory Biometric { get; } = Register("biometric", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II.</summary>
    public static DataCategory Genetic { get; } = Register("genetic", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II.</summary>
    public static DataCategory EthnicOrigin { get; } = Register("ethnic-origin", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II.</summary>
    public static DataCategory ReligiousBelief { get; } = Register("religious-belief", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II.</summary>
    public static DataCategory PoliticalOpinion { get; } = Register("political-opinion", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II: union, religious, philosophical or political organization membership.</summary>
    public static DataCategory Membership { get; } = Register("membership", DataSensitivity.SensitivePersonal);

    /// <summary>Art. 5 II.</summary>
    public static DataCategory SexLife { get; } = Register("sex-life", DataSensitivity.SensitivePersonal);

    private DataCategory(string key, ushort id, DataSensitivity minimumSensitivity)
    {
        Key = key;
        Id = id;
        MinimumSensitivity = minimumSensitivity;
    }

    /// <summary>Persisted form, lower-case <c>[a-z0-9.-]</c>, e.g. <c>"health"</c>.</summary>
    public string Key { get; }

    /// <summary>Process-local interned id (bit/array index). Never persist it; persist <see cref="Key"/>.</summary>
    public ushort Id { get; }

    /// <summary>The lowest sensitivity a value of this category may be declared with (health → SensitivePersonal).</summary>
    public DataSensitivity MinimumSensitivity { get; }

    /// <summary>A special category of LGPD art. 5 II (requires an art. 11 legal basis).</summary>
    public bool IsSpecialCategory => MinimumSensitivity == DataSensitivity.SensitivePersonal;

    /// <summary>
    /// Defines (or returns the existing) category. Idempotent for equal arguments; redefining a key with another minimum
    /// sensitivity throws.
    /// </summary>
    public static DataCategory Define(string key, DataSensitivity minimumSensitivity)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (s_byKey.TryGetValue(key, out DataCategory? existing)) return Check(existing, minimumSensitivity);
        if (!IsValidKey(key)) throw new ArgumentException($"Invalid data category key '{key}': use lower-case letters, digits, '.' and '-'.", nameof(key));
        return Register(key, minimumSensitivity);
    }

    /// <summary>Finds a category by its key.</summary>
    public static bool TryGet(string key, [NotNullWhen(true)] out DataCategory? category) => s_byKey.TryGetValue(key, out category);

    /// <summary>Finds a category by its key, or throws.</summary>
    public static DataCategory Get(string key)
        => s_byKey.TryGetValue(key, out DataCategory? category) ? category : throw new KeyNotFoundException($"Unknown data category '{key}'.");

    /// <summary>The category with a process-local id (see <see cref="Id"/>).</summary>
    public static DataCategory FromId(ushort id)
    {
        DataCategory[] snapshot = Volatile.Read(ref s_byId);
        return id < snapshot.Length && snapshot[id] is { } category ? category : None;
    }

    public bool Equals(DataCategory? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => Id;

    public override string ToString() => Key;

    private static DataCategory Register(string key, DataSensitivity minimumSensitivity)
    {
        lock (s_gate)
        {
            if (s_byKey.TryGetValue(key, out DataCategory? existing)) return Check(existing, minimumSensitivity);
            if (s_count == ushort.MaxValue) throw new InvalidOperationException("Too many data categories.");
            var category = new DataCategory(key, (ushort)s_count, minimumSensitivity);
            DataCategory[] current = s_byId;
            if (s_count == current.Length)
            {
                var grown = new DataCategory[current.Length * 2];
                Array.Copy(current, grown, current.Length);
                current = grown;
            }
            current[s_count++] = category;
            Volatile.Write(ref s_byId, current);
            s_byKey[key] = category;
            return category;
        }
    }

    private static DataCategory Check(DataCategory existing, DataSensitivity minimumSensitivity)
        => existing.MinimumSensitivity == minimumSensitivity
            ? existing
            : throw new InvalidOperationException(
                $"Data category '{existing.Key}' is already defined with minimum sensitivity {existing.MinimumSensitivity}.");

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0) return false;
        foreach (char c in key)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')) return false;
        return true;
    }
}
