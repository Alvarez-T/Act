using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;
using YFex.Cqrs;
using YFex.Rules;
using YFex.Validation;

namespace YFex.Messaging.Rpc.Serialization;

/// <summary>
/// MemoryPack formatters for the CQRS result unions and the structured validation payload they carry (D26): Fusion
/// RPC's default wire format is MemoryPack (<c>mempack6</c>), and <see cref="Error"/>, <see cref="Result{T}"/>,
/// <see cref="QueueableResult{T}"/>… live in libraries that must not reference MemoryPack (YFex core, YFex.Validation,
/// YFex.Cqrs) — so the formatters are registered from here. Every payload is prefixed by a format version byte.
/// </summary>
/// <remarks>
/// Non-generic formatters are registered by a module initializer. Closed generic results need
/// <see cref="Register{T}"/> per result type — the CQRS generator emits those calls next to the RPC contracts
/// (AOT-safe). On runtimes with dynamic code the open generic formatters are also registered as a fallback.
/// </remarks>
public static class RpcResultFormatters
{
    // v2 (validation Phase 4): Error carries approvals + the decision fingerprint.
    // v3: RawValue carries its RawType id (semantic types are open — Money/Mass/app types), storage kinds renumbered;
    //     approvals carry the rule tier depth (same byte as v2's layer: Code 0, Tenant 1, Store 2 ≙ tiers 0, 1, 2).
    // v1/v2 payloads are still read (legacy kinds mapped by RawValueLegacy).
    internal const byte Version = 3;
    private static int s_registered;

    /// <summary>Registers the non-generic formatters (idempotent; also run by the module initializer).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        MemoryPackFormatterProvider.Register(new ErrorFormatter());
        MemoryPackFormatterProvider.Register(new ValidationResultFormatter());
        MemoryPackFormatterProvider.Register(new ResultFormatter());
        MemoryPackFormatterProvider.Register(new QueueableResultFormatter());
        RpcErrorResults<Result>.Factory = static error => error;
        RpcErrorResults<QueueableResult>.Factory = static error => error;
        if (RuntimeFeature.IsDynamicCodeSupported) RegisterOpenGenerics();
    }

    /// <summary>Registers the formatters of <see cref="Result{T}"/> and <see cref="QueueableResult{T}"/> for one result type.</summary>
    public static void Register<T>()
    {
        EnsureRegistered();
        if (!MemoryPackFormatterProvider.IsRegistered<Result<T>>())
            MemoryPackFormatterProvider.Register(new ResultFormatter<T>());
        if (!MemoryPackFormatterProvider.IsRegistered<QueueableResult<T>>())
            MemoryPackFormatterProvider.Register(new QueueableResultFormatter<T>());
        RpcErrorResults<Result<T>>.Factory ??= static error => error;
        RpcErrorResults<QueueableResult<T>>.Factory ??= static error => error;
    }

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Formatter registration must precede any RPC serialization.")]
    internal static void Initialize() => EnsureRegistered();

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported; AOT uses Register<T>().")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Formatter types are referenced statically.")]
    private static void RegisterOpenGenerics()
    {
        MemoryPackFormatterProvider.RegisterGenericType(typeof(Result<>), typeof(ResultFormatter<>));
        MemoryPackFormatterProvider.RegisterGenericType(typeof(QueueableResult<>), typeof(QueueableResultFormatter<>));
    }

    // ── Shared pieces ──────────────────────────────────────────────────────────

    internal static void WriteError<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in Error error)
        where TBuffer : IBufferWriter<byte>
    {
        writer.WriteUnmanaged((byte)error.Type);
        writer.WriteString(error.Message);
        WriteViolations(ref writer, error.Violations);
        // v2: approvals + fingerprint (ErrorType.ApprovalRequired, YFex.Rules 09 §6)
        var approvals = error.Approvals;
        writer.WriteUnmanaged(approvals.Count);
        for (int i = 0; i < approvals.Count; i++)
        {
            ref readonly var a = ref approvals[i];
            writer.WriteString(a.Permission);
            WriteViolations(ref writer, ViolationList.From([a.Reason]));
            writer.WriteString(a.Rule.Code);
            writer.WriteUnmanaged(a.Rule.Version, (byte)a.Rule.Tier, a.Rule.DataVersion, a.Rule.RowId);
        }
        writer.WriteUnmanaged(error.DecisionFingerprint);
    }

    internal static Error ReadError(ref MemoryPackReader reader, byte version = Version)
    {
        var type = (ErrorType)reader.ReadUnmanaged<byte>();
        string message = reader.ReadString() ?? "";
        var violations = ReadViolations(ref reader, version);
        if (version < 2) return new Error(type, message, violations);

        int count = reader.ReadUnmanaged<int>();
        ApprovalRequest[]? approvals = count > 0 ? new ApprovalRequest[count] : null;
        for (int i = 0; i < count; i++)
        {
            string permission = reader.ReadString() ?? "";
            var reason = ReadViolations(ref reader, version);
            string code = reader.ReadString() ?? "";
            reader.ReadUnmanaged(out int ruleVersion, out byte tier, out long dataVersion, out Guid rowId);
            approvals![i] = new ApprovalRequest(permission, reason.IsEmpty ? default : reason[0],
                new RuleRef(code, ruleVersion, tier, dataVersion, rowId));
        }
        ulong fingerprint = reader.ReadUnmanaged<ulong>();
        return new Error(type, message, violations, approvals is null ? default : ApprovalRequestList.From(approvals), fingerprint);
    }

    internal static void WriteViolations<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in ViolationList list)
        where TBuffer : IBufferWriter<byte>
    {
        writer.WriteUnmanaged(list.Count);
        foreach (ref readonly var v in list)
        {
            writer.WriteString(v.Path.Template);
            var indices = v.Path.Indices;
            writer.WriteUnmanaged((byte)indices.Count);
            for (int i = 0; i < indices.Count; i++) writer.WriteUnmanaged(indices[i]);
            writer.WriteString(v.Code.Value);
            writer.WriteUnmanaged((byte)v.Severity);
            writer.WriteUnmanaged((byte)v.Args.Count);
            for (int i = 0; i < v.Args.Count; i++) WriteRaw(ref writer, v.Args[i]);
            writer.WriteUnmanaged((byte)v.Origin.Source);
            writer.WriteUnmanaged(v.Origin.OwnerId);
            writer.WriteString(v.Detail);
        }
    }

    internal static ViolationList ReadViolations(ref MemoryPackReader reader, byte version = Version)
    {
        int count = reader.ReadUnmanaged<int>();
        if (count <= 0) return ViolationList.Empty;
        var items = new Violation[count];
        Span<int> indices = stackalloc int[IndexPath.MaxDepth];
        for (int n = 0; n < count; n++)
        {
            string? template = reader.ReadString();
            int depth = reader.ReadUnmanaged<byte>();
            if (depth > IndexPath.MaxDepth) throw new MemoryPackSerializationException($"Index depth {depth} > {IndexPath.MaxDepth}.");
            for (int i = 0; i < depth; i++) indices[i] = reader.ReadUnmanaged<int>();
            var path = template is null ? FieldPath.Empty : FieldPath.Of(template, IndexPath.Of(indices[..depth]));
            string? code = reader.ReadString();
            var severity = (Severity)reader.ReadUnmanaged<byte>();
            int argCount = reader.ReadUnmanaged<byte>();
            var args = argCount switch
            {
                0 => RuleArgs.None,
                1 => new RuleArgs(ReadRaw(ref reader, version)),
                _ => new RuleArgs(ReadRaw(ref reader, version), ReadRaw(ref reader, version)),
            };
            var origin = new ViolationOrigin((ViolationSource)reader.ReadUnmanaged<byte>(), reader.ReadUnmanaged<ushort>());
            string? detail = reader.ReadString();
            items[n] = new Violation(in path, code is null ? default : new RuleCode(code), severity, in args, origin, detail);
        }
        return ViolationList.FromOwnedArray(items);
    }

    private static void WriteRaw<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in RawValue value)
        where TBuffer : IBufferWriter<byte>
    {
        value.GetParts(out var kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);
        writer.WriteUnmanaged((byte)kind);
        if (kind == ValueKind.None) return;
        writer.WriteUnmanaged(low, high, aux, type);
        writer.WriteString(text);
    }

    private static RawValue ReadRaw(ref MemoryPackReader reader, byte version)
    {
        byte kind = reader.ReadUnmanaged<byte>();
        if (kind == 0) return RawValue.None;
        if (version < 3)
        {
            reader.ReadUnmanaged(out ulong l1, out ulong h1, out byte a1);
            return RawValueLegacy.FromV1Parts(kind, l1, h1, reader.ReadString(), a1);
        }
        reader.ReadUnmanaged(out ulong low, out ulong high, out byte aux, out uint type);
        return RawValue.FromParts((ValueKind)kind, low, high, reader.ReadString(), aux, type);
    }

    internal static byte CheckVersion(ref MemoryPackReader reader)
    {
        byte version = reader.ReadUnmanaged<byte>();
        if (version is 0 or > Version) throw new MemoryPackSerializationException($"Unsupported YFex result format version {version}.");
        return version;
    }
}

/// <summary>
/// <see cref="Error"/> → typed result of an RPC method returning <typeparamref name="T"/> — how
/// <see cref="CallContextMiddleware"/> rejects a call (stage 2) with the method's own result type, outside the method.
/// Filled for <see cref="Result"/>/<see cref="QueueableResult"/> and, per result type, by <see cref="RpcResultFormatters.Register{T}"/>
/// (emitted by the CQRS generator), so no reflection is needed.
/// </summary>
public static class RpcErrorResults<T>
{
    public static Func<Error, T>? Factory { get; set; }

    /// <summary>The typed error result; throws <see cref="ErrorResultException"/> (an RPC error) when <typeparamref name="T"/> is unknown.</summary>
    public static T From(Error error) => Factory is { } factory ? factory(error) : throw new ErrorResultException(error);
}

public sealed class ErrorFormatter : MemoryPackFormatter<Error>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref Error value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        RpcResultFormatters.WriteError(ref writer, value);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Error value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = RpcResultFormatters.ReadError(ref reader, version);
    }
}

/// <summary>Standalone <see cref="ValidationResult"/> (e.g. inside app payloads, sync-failure records).</summary>
public sealed class ValidationResultFormatter : MemoryPackFormatter<ValidationResult>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref ValidationResult value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        RpcResultFormatters.WriteViolations(ref writer, value.Violations);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref ValidationResult value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = ValidationResult.Of(RpcResultFormatters.ReadViolations(ref reader, version));
    }
}

public sealed class ResultFormatter : MemoryPackFormatter<Result>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref Result value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        if (value.TryGetValue(out Error error)) { writer.WriteUnmanaged((byte)2); RpcResultFormatters.WriteError(ref writer, error); }
        else writer.WriteUnmanaged(value.HasValue ? (byte)1 : (byte)0);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Result value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = reader.ReadUnmanaged<byte>() switch
        {
            1 => Result.Ok(),
            2 => RpcResultFormatters.ReadError(ref reader, version),
            _ => default,
        };
    }
}

public sealed class ResultFormatter<T> : MemoryPackFormatter<Result<T>>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref Result<T> value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        if (value.TryGetValue(out T? ok)) { writer.WriteUnmanaged((byte)1); writer.WriteValue(ok); }
        else if (value.TryGetValue(out Error error)) { writer.WriteUnmanaged((byte)2); RpcResultFormatters.WriteError(ref writer, error); }
        else writer.WriteUnmanaged((byte)0);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Result<T> value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = reader.ReadUnmanaged<byte>() switch
        {
            1 => Result<T>.Ok(reader.ReadValue<T>()!),
            2 => RpcResultFormatters.ReadError(ref reader, version),
            _ => default,
        };
    }
}

public sealed class QueueableResultFormatter : MemoryPackFormatter<QueueableResult>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref QueueableResult value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        if (value.IsSuccess) writer.WriteUnmanaged((byte)1);
        else if (value.TryGetError(out var error)) { writer.WriteUnmanaged((byte)2); RpcResultFormatters.WriteError(ref writer, error); }
        else if (value.TryGetQueued(out var queued)) { writer.WriteUnmanaged((byte)3); writer.WriteUnmanaged(queued.IdempotencyKey); }
        else writer.WriteUnmanaged((byte)0);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref QueueableResult value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = reader.ReadUnmanaged<byte>() switch
        {
            1 => QueueableResult.Ok(),
            2 => RpcResultFormatters.ReadError(ref reader, version),
            3 => QueueableResult.Queue(reader.ReadUnmanaged<Guid>()),
            _ => default,
        };
    }
}

public sealed class QueueableResultFormatter<T> : MemoryPackFormatter<QueueableResult<T>>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref QueueableResult<T> value)
    {
        writer.WriteUnmanaged(RpcResultFormatters.Version);
        if (value.TryGetValue(out T? ok)) { writer.WriteUnmanaged((byte)1); writer.WriteValue(ok); }
        else if (value.TryGetError(out var error)) { writer.WriteUnmanaged((byte)2); RpcResultFormatters.WriteError(ref writer, error); }
        else if (value.TryGetQueued(out var queued)) { writer.WriteUnmanaged((byte)3); writer.WriteUnmanaged(queued.IdempotencyKey); }
        else writer.WriteUnmanaged((byte)0);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref QueueableResult<T> value)
    {
        var version = RpcResultFormatters.CheckVersion(ref reader);
        value = reader.ReadUnmanaged<byte>() switch
        {
            1 => QueueableResult<T>.Ok(reader.ReadValue<T>()!),
            2 => RpcResultFormatters.ReadError(ref reader, version),
            3 => QueueableResult<T>.Queue(reader.ReadUnmanaged<Guid>()),
            _ => default,
        };
    }
}
