using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YFex.Rules;

/// <summary>What a published record holds.</summary>
/// <remarks><see cref="Rule"/> = a runtime-authored rule (05 §8; decoded by the host's <see cref="IRuntimeRuleCodec{TSituation}"/>).</remarks>
public enum RuleDataKind : byte { Table = 1, Parameters = 2, Override = 3, Rule = 4 }

/// <summary>Identity of a versioned stream of published data: (rule set, kind, table/rule code, tier path).</summary>
public readonly record struct RuleDataKey(string RuleSet, RuleDataKind Kind, string Code, RuleTierPath TierPath)
{
    public override string ToString() => $"{RuleSet}/{Kind}/{Code}@{TierPath}";
}

/// <summary>Header of a published version (no payload) — history lists, "how was the rule" (06 §6).</summary>
public sealed record RuleDataInfo(RuleDataKey Key, long Version, DateOnly EffectiveFrom, string? PublishedBy, DateTimeOffset PublishedAt, string? Note);

/// <summary>A published version: header + JSON payload (rows document / parameter values / override action).</summary>
public sealed record RuleDataRecord(RuleDataInfo Info, ReadOnlyMemory<byte> Payload);

/// <summary>Optimistic-concurrency failure: someone published another version since the editor opened it (05 §4.2).</summary>
public sealed class RuleStoreConflictException(RuleDataKey key, long expected, long actual)
    : Exception($"'{key}' is at version {actual}; the change was based on {expected}.")
{
    public RuleDataKey Key { get; } = key;
    public long ExpectedVersion { get; } = expected;
    public long ActualVersion { get; } = actual;
}

/// <summary>
/// Persistence for published rule data (05 §4.3). Versions are immutable (rollback = publish the old content again) and
/// monotonic per RULE SET (so <see cref="RulesetVersion.DataVersion"/> orders data changes). Implementations: in-memory
/// (tests, single process), SQL (server), and clients cache the cacheable <c>RuleAdmin</c> queries.
/// </summary>
public interface IRuleStore
{
    /// <summary>
    /// Appends a version. <paramref name="baseVersion"/> = the latest version the change was based on (0 = none yet);
    /// a different latest version throws <see cref="RuleStoreConflictException"/>. Returns the assigned version.
    /// </summary>
    ValueTask<RuleDataInfo> AppendAsync(RuleDataKey key, long baseVersion, DateOnly effectiveFrom, ReadOnlyMemory<byte> payload,
        string? publishedBy, string? note, CancellationToken ct);

    /// <summary>
    /// Versions of every stream of a rule set with <see cref="RuleDataInfo.Version"/> &gt; <paramref name="afterVersion"/>,
    /// ascending by version (0 = everything: host warm-up; the last known version: incremental client sync).
    /// </summary>
    ValueTask<ImmutableArray<RuleDataRecord>> LoadAsync(string ruleSet, long afterVersion, CancellationToken ct);

    /// <summary>Headers of one stream, newest first.</summary>
    ValueTask<ImmutableArray<RuleDataInfo>> GetVersionsAsync(RuleDataKey key, CancellationToken ct);

    ValueTask<RuleDataRecord?> GetAsync(RuleDataKey key, long version, CancellationToken ct);

    /// <summary>The record carrying <paramref name="version"/> (versions are unique per rule set) — "rule as applied" (06 §6).</summary>
    ValueTask<RuleDataRecord?> GetByVersionAsync(string ruleSet, long version, CancellationToken ct);
}

/// <summary>Thread-safe in-memory <see cref="IRuleStore"/> (tests, demos, single-process apps).</summary>
public sealed class InMemoryRuleStore(TimeProvider? clock = null) : IRuleStore
{
    private readonly object _gate = new();
    private readonly List<RuleDataRecord> _records = [];
    private readonly Dictionary<string, long> _lastVersionBySet = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ValueTask<RuleDataInfo> AppendAsync(RuleDataKey key, long baseVersion, DateOnly effectiveFrom, ReadOnlyMemory<byte> payload,
        string? publishedBy, string? note, CancellationToken ct)
    {
        lock (_gate)
        {
            long latest = 0;
            foreach (var r in _records) if (r.Info.Key == key && r.Info.Version > latest) latest = r.Info.Version;
            if (latest != baseVersion) throw new RuleStoreConflictException(key, baseVersion, latest);
            long version = (_lastVersionBySet.TryGetValue(key.RuleSet, out var v) ? v : 0) + 1;
            _lastVersionBySet[key.RuleSet] = version;
            var info = new RuleDataInfo(key, version, effectiveFrom, publishedBy, _clock.GetUtcNow(), note);
            _records.Add(new RuleDataRecord(info, payload.ToArray()));
            return ValueTask.FromResult(info);
        }
    }

    public ValueTask<ImmutableArray<RuleDataRecord>> LoadAsync(string ruleSet, long afterVersion, CancellationToken ct)
    {
        lock (_gate)
        {
            var b = ImmutableArray.CreateBuilder<RuleDataRecord>();
            foreach (var r in _records) if (r.Info.Key.RuleSet == ruleSet && r.Info.Version > afterVersion) b.Add(r);   // appended in version order
            return ValueTask.FromResult(b.ToImmutable());
        }
    }

    public ValueTask<RuleDataRecord?> GetByVersionAsync(string ruleSet, long version, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var r in _records) if (r.Info.Version == version && r.Info.Key.RuleSet == ruleSet) return ValueTask.FromResult<RuleDataRecord?>(r);
            return ValueTask.FromResult<RuleDataRecord?>(null);
        }
    }

    public ValueTask<ImmutableArray<RuleDataInfo>> GetVersionsAsync(RuleDataKey key, CancellationToken ct)
    {
        lock (_gate)
        {
            var b = ImmutableArray.CreateBuilder<RuleDataInfo>();
            for (int i = _records.Count - 1; i >= 0; i--) if (_records[i].Info.Key == key) b.Add(_records[i].Info);   // newest first
            return ValueTask.FromResult(b.ToImmutable());
        }
    }

    public ValueTask<RuleDataRecord?> GetAsync(RuleDataKey key, long version, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var r in _records) if (r.Info.Key == key && r.Info.Version == version) return ValueTask.FromResult<RuleDataRecord?>(r);
            return ValueTask.FromResult<RuleDataRecord?>(null);
        }
    }
}

/// <summary>Lossless, sortable text form of a <see cref="RuleTierPath"/> for storage columns and cache keys.</summary>
public static class RuleTierPaths
{
    /// <summary><c>"code"</c> | <c>"tier:&lt;v1&gt;"</c> | <c>"tier:&lt;v1&gt;|&lt;v2&gt;"</c>… (each value encoded losslessly, type included).</summary>
    public static string Format(in RuleTierPath path)
    {
        if (path.IsCode) return "code";
        var sb = new StringBuilder("tier:");
        for (int i = 0; i < path.Depth; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(Encode(path[i]));
        }
        return sb.ToString();
    }

    public static RuleTierPath Parse(string text)
    {
        if (text == "code") return RuleTierPath.Code;
        if (!text.StartsWith("tier:", StringComparison.Ordinal)) throw new FormatException($"Invalid rule tier path '{text}'.");
        var body = text.AsSpan(5);
        var values = new RawValue[RuleTierPath.MaxDepth];
        int n = 0;
        foreach (var range in body.Split('|'))
        {
            if (n == RuleTierPath.MaxDepth) throw new FormatException($"Rule tier path '{text}' is deeper than {RuleTierPath.MaxDepth}.");
            values[n++] = Decode(body[range]);
        }
        return RuleTierPath.Of(values.AsSpan(0, n));
    }

    // kind.low.high.aux.type.base64url(text)
    private static string Encode(in RawValue v)
    {
        v.GetParts(out var kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);
        string t = text is null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.Create(CultureInfo.InvariantCulture, $"{(byte)kind}.{low:x}.{high:x}.{aux}.{type:x}.{t}");
    }

    private static RawValue Decode(ReadOnlySpan<char> s)
    {
        Span<Range> parts = stackalloc Range[6];
        if (s.Split(parts, '.') != 6) throw new FormatException("Invalid rule tier value.");
        var kind = (ValueKind)byte.Parse(s[parts[0]], CultureInfo.InvariantCulture);
        ulong low = ulong.Parse(s[parts[1]], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ulong high = ulong.Parse(s[parts[2]], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte aux = byte.Parse(s[parts[3]], CultureInfo.InvariantCulture);
        uint type = uint.Parse(s[parts[4]], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var t = s[parts[5]];
        string? text = null;
        if (!(t.Length == 1 && t[0] == '-'))
        {
            string b64 = t.ToString().Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        return RawValue.FromParts(kind, low, high, text, aux, type);
    }
}

/// <summary>Encodes/decodes published payloads of a rule set (table rows, parameter values, override actions).</summary>
public static class RuleDataCodec
{
    /// <summary>
    /// Stored record → snapshot the host can publish. Unknown codes / parameters throw; runtime rules need the host's
    /// <paramref name="runtimeRules"/> codec (none → <see cref="NotSupportedException"/>).
    /// </summary>
    public static RulesetSnapshot Decode<TS>(RuleSetDefinition<TS> set, RuleDataRecord record, IRuntimeRuleCodec<TS>? runtimeRules = null)
        where TS : ISituation<TS>
    {
        var info = record.Info;
        var key = info.Key;
        switch (key.Kind)
        {
            case RuleDataKind.Table:
                if (!set.TryGetTable(key.Code, out var table)) throw new ArgumentException($"Rule set '{set.Name}' has no table '{key.Code}'.");
                var version = table.Parse(record.Payload.Span,
                    new TableVersionInfo(key.Code, info.Version, key.TierPath, info.EffectiveFrom, info.PublishedBy, info.PublishedAt, info.Note));
                return new TableSnapshot(version);
            case RuleDataKind.Parameters:
                if (!set.TryGetRule(key.Code, out var rule)) throw new ArgumentException($"Rule set '{set.Name}' has no rule '{key.Code}'.");
                return new ParameterSnapshot(key.Code, key.TierPath, info.Version, info.EffectiveFrom, ReadParameters(rule.Parameters, record.Payload.Span));
            case RuleDataKind.Override:
                return new OverrideSnapshot(key.Code, key.TierPath, info.Version, info.EffectiveFrom, ReadOverride(record.Payload.Span));
            case RuleDataKind.Rule:
                if (runtimeRules is null) throw new NotSupportedException($"Rule set '{set.Name}' has no runtime rule codec (host.UseRuntimeRules).");
                return runtimeRules.Decode(set, record);
            default:
                throw new ArgumentOutOfRangeException(nameof(record), key.Kind, null);
        }
    }

    /// <summary>Parameter values document: <c>{"rate": 7.5}</c> (only the parameters being overridden).</summary>
    public static ImmutableDictionary<string, object?> ReadParameters(ReadOnlySpan<RuleParameter> parameters, ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json);
        r.Read();
        TableJson.Expect(ref r, JsonTokenType.StartObject);
        var b = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        while (TableJson.NextProperty(ref r, out var name))
        {
            RuleParameter? p = null;
            foreach (var candidate in parameters) if (candidate.Name == name) { p = candidate; break; }
            if (p is null) throw new JsonException($"Unknown parameter '{name}'.");
            b[name] = p.ReadJson(ref r);
        }
        return b.ToImmutable();
    }

    public static byte[] WriteParameters(ReadOnlySpan<RuleParameter> parameters, IReadOnlyDictionary<string, object?> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in parameters)
            {
                if (!values.TryGetValue(p.Name, out var v)) continue;
                w.WritePropertyName(p.Name);
                p.WriteJson(w, v);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Override document: <c>{"action":"disable"}</c> | <c>{"action":"enable"}</c> | <c>{"action":"replace","code":"…"}</c>.</summary>
    public static RuleOverrideAction ReadOverride(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json);
        r.Read();
        TableJson.Expect(ref r, JsonTokenType.StartObject);
        string? action = null, code = null;
        while (TableJson.NextProperty(ref r, out var name))
        {
            if (name == "action") action = r.GetString();
            else if (name == "code") code = r.GetString();
            else r.Skip();
        }
        return action switch
        {
            "disable" => RuleOverrideAction.Disable,
            "enable" => RuleOverrideAction.Enable,
            "replace" when code is not null => RuleOverrideAction.ReplaceWith(code),
            _ => throw new JsonException($"Invalid override action '{action}'."),
        };
    }

    public static byte[] WriteOverride(in RuleOverrideAction action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("action", action.Kind switch
            {
                RuleOverrideKind.Disable => "disable",
                RuleOverrideKind.Enable => "enable",
                RuleOverrideKind.ReplaceWith => "replace",
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            });
            if (action.ReplacementCode is { } c) w.WriteString("code", c);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
