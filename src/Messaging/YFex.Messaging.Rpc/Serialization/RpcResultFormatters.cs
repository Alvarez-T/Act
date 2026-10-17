using YFex.Authorization.Public;
using System.Buffers;
using System.Buffers.Binary;
using YFex.Cryptography;
using YFex.Installations;
using System.Collections.Immutable;
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
    // v4 (authorization-consolidation Phase 2b): Error = [Union] kind tag + case payload (Forbidden carries the Denial).
    // v5 (authorization-consolidation Phase 4b, decision A5): ApprovalRequired also carries the decision digest (32 bytes).
    internal const byte Version = 5;
    private static int s_registered;

    /// <summary>Registers the non-generic formatters (idempotent; also run by the module initializer).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        MemoryPackFormatterProvider.Register(new ErrorFormatter());
        MemoryPackFormatterProvider.Register(new ValidationResultFormatter());
        MemoryPackFormatterProvider.Register(new ResultFormatter());
        MemoryPackFormatterProvider.Register(new QueueableResultFormatter());
        MemoryPackFormatterProvider.Register(new PermissionSnapshotFormatter());
        MemoryPackFormatterProvider.Register(new ApprovalTokenFormatter());
        MemoryPackFormatterProvider.Register(new PermissionFormatter());
        MemoryPackFormatterProvider.Register(new Hash256Formatter());
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

    // v4 (authorization-consolidation Phase 2b, ⚑Z11): Error is a [Union] — kind tag, display message, then ONLY the case payload.
    internal static void WriteError<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in Error error)
        where TBuffer : IBufferWriter<byte>
    {
        writer.WriteUnmanaged((byte)error.Kind);
        writer.WriteString(error.Message);
        switch (error.Kind)
        {
            case ErrorKind.ValidationProblem or ErrorKind.Conflict:
                WriteViolations(ref writer, error.Violations);
                break;
            case ErrorKind.Forbidden:
                error.TryGetValue(out Forbidden forbidden);
                var denial = forbidden.Denial;
                writer.WriteUnmanaged((byte)denial.Kind);
                writer.WriteString(denial.Missing.Value.Length == 0 ? null : denial.Missing.Value);   // ids, never ordinals
                writer.WriteString(denial.Policy.IsEmpty ? null : denial.Policy.Value);
                writer.WriteString(denial.Code);
                break;
            case ErrorKind.ApprovalRequired:
                error.TryGetValue(out ApprovalRequired approval);
                WriteViolations(ref writer, approval.Violations);
                WriteApprovals(ref writer, approval.Approvals);
                writer.WriteUnmanaged(approval.DecisionFingerprint);
                Hash256Formatter.Write(ref writer, approval.DecisionDigest);
                break;
        }
    }

    internal static Error ReadError(ref MemoryPackReader reader, byte version = Version)
    {
        if (version < 4) return ReadLegacyError(ref reader, version);
        var kind = (ErrorKind)reader.ReadUnmanaged<byte>();
        string message = reader.ReadString() ?? "";
        Error error;
        switch (kind)
        {
            case ErrorKind.NotFound: error = new NotFound(message); break;
            case ErrorKind.Failed: error = new Failed(message); break;
            case ErrorKind.Unauthenticated: error = new Unauthenticated(message); break;
            case ErrorKind.ValidationProblem: error = new ValidationProblem(ReadViolations(ref reader, version)); break;
            case ErrorKind.Conflict: error = new Conflict(message, ReadViolations(ref reader, version)); break;
            case ErrorKind.Forbidden:
                var denialKind = (DenialKind)reader.ReadUnmanaged<byte>();
                string? missing = reader.ReadString();
                string? policy = reader.ReadString();
                string? code = reader.ReadString();
                error = new Forbidden(new Denial(denialKind,
                    PermissionKey.TryParse(missing, null, out var key) ? new Permission(key) : default,
                    PolicyName.TryParse(policy, null, out var name) ? name : default,
                    code));
                break;
            case ErrorKind.ApprovalRequired:
                var violations = ReadViolations(ref reader, version);
                var approvals = ReadApprovals(ref reader, version);
                ulong fingerprint = reader.ReadUnmanaged<ulong>();
                Hash256 digest = version >= 5 ? Hash256Formatter.Read(ref reader) : default;
                error = Error.ApprovalRequired(approvals, fingerprint, in digest, violations);
                break;
            default: return default;
        }
        return error.WithMessage(message);   // the sender's display text wins (it may be custom)
    }

    // v1–v3: flat Error (ErrorType byte: 0 None, 1 NotFound, 2 Fail, 3 Unauthorized, 4 ValidationProblem, 5 Conflict, 6 ApprovalRequired).
    private static Error ReadLegacyError(ref MemoryPackReader reader, byte version)
    {
        byte type = reader.ReadUnmanaged<byte>();
        string message = reader.ReadString() ?? "";
        var violations = ReadViolations(ref reader, version);
        ApprovalRequestList approvals = default;
        ulong fingerprint = 0;
        if (version >= 2)
        {
            approvals = ReadApprovals(ref reader, version);
            fingerprint = reader.ReadUnmanaged<ulong>();
        }
        Error error = type switch
        {
            1 => new NotFound(message),
            2 => new Failed(message),
            3 => new Forbidden(new Denial(DenialKind.None)),
            4 => new ValidationProblem(violations),
            5 => new Conflict(message, violations),
            6 => Error.ApprovalRequired(approvals, fingerprint, violations),
            _ => default,
        };
        return error.HasValue ? error.WithMessage(message) : error;
    }

    private static void WriteApprovals<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in ApprovalRequestList approvals)
        where TBuffer : IBufferWriter<byte>
    {
        writer.WriteUnmanaged(approvals.Count);
        for (int i = 0; i < approvals.Count; i++)
        {
            ref readonly var a = ref approvals[i];
            writer.WriteString(a.Permission.Value);                         // the id (unchanged wire)
            WriteViolations(ref writer, ViolationList.From([a.Reason]));
            writer.WriteString(a.Rule.Code);
            writer.WriteUnmanaged(a.Rule.Version, (byte)a.Rule.Tier, a.Rule.DataVersion, a.Rule.RowId);
        }
    }

    private static ApprovalRequestList ReadApprovals(ref MemoryPackReader reader, byte version)
    {
        int count = reader.ReadUnmanaged<int>();
        if (count == 0) return default;
        var approvals = new ApprovalRequest[count];
        for (int i = 0; i < count; i++)
        {
            string permission = reader.ReadString() ?? "";
            var reason = ReadViolations(ref reader, version);
            string code = reader.ReadString() ?? "";
            reader.ReadUnmanaged(out int ruleVersion, out byte tier, out long dataVersion, out Guid rowId);
            approvals[i] = new ApprovalRequest(PermissionKey.TryParse(permission, null, out var key) ? new Permission(key) : default,
                reason.IsEmpty ? default : reason[0],
                new RuleRef(code, ruleVersion, tier, dataVersion, rowId));
        }
        return ApprovalRequestList.From(approvals);
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

/// <summary>
/// <see cref="ApprovalToken"/> over Fusion RPC (commands carry <c>IReadOnlyList&lt;ApprovalToken&gt;</c>; <c>Approve</c> returns one):
/// YFex.Authorization.Public is a leaf and must not reference MemoryPack. Layout v1: version byte, kind, approver, approver scope,
/// permission id, digest (32 bytes), validity (from UTC ticks, has-until flag, until UTC ticks), key id, installation id, signature.
/// </summary>
public sealed class ApprovalTokenFormatter : MemoryPackFormatter<ApprovalToken>
{
    internal const byte Version = 1;
    private const int MaximumSignatureLength = 512;

    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref ApprovalToken? value)
    {
        if (value is null) { writer.WriteNullObjectHeader(); return; }
        writer.WriteObjectHeader(1);
        writer.WriteUnmanaged(Version, (byte)value.Kind);
        writer.WriteString(value.Approver);
        writer.WriteString(value.ApproverScope);
        writer.WriteString(value.Permission.Value);
        Hash256Formatter.Write(ref writer, value.Digest);
        writer.WriteUnmanaged(value.Validity.From.UtcTicks, value.Validity.Until.HasValue, value.Validity.Until?.UtcTicks ?? 0L);
        writer.WriteUnmanaged(value.Key.Value, value.Installation.Value);
        writer.WriteUnmanagedSpan(value.Signature.Span);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref ApprovalToken? value)
    {
        if (!reader.TryReadObjectHeader(out _)) { value = null; return; }
        reader.ReadUnmanaged(out byte version, out byte kind);
        if (version != Version)
            throw new MemoryPackSerializationException($"Unsupported ApprovalToken wire version {version} (expected {Version}).");
        string approver = reader.ReadString() ?? "";
        string? approverScope = reader.ReadString();
        string? permission = reader.ReadString();
        Hash256 digest = Hash256Formatter.Read(ref reader);
        reader.ReadUnmanaged(out long from, out bool hasUntil, out long until);
        reader.ReadUnmanaged(out Guid key, out Guid installation);
        byte[] signature = reader.ReadUnmanagedArray<byte>() ?? [];
        if (signature.Length > MaximumSignatureLength) throw new MemoryPackSerializationException("ApprovalToken signature too long.");
        var validity = new ValidityPeriod(new DateTimeOffset(from, TimeSpan.Zero), hasUntil ? new DateTimeOffset(until, TimeSpan.Zero) : null);
        value = new ApprovalToken((ApprovalTokenKind)kind, approver, approverScope,
            Permission.TryParse(permission, null, out var parsed) ? parsed : default, digest, validity, new KeyId(key),
            new InstallationId(installation), signature);
    }
}

/// <summary><see cref="Permission"/> over Fusion RPC: its id text (never the process-local ordinal). An unparsable id reads as default.</summary>
public sealed class PermissionFormatter : MemoryPackFormatter<Permission>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref Permission value)
        => writer.WriteString(value.Value);

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Permission value)
        => value = Permission.TryParse(reader.ReadString(), null, out var parsed) ? parsed : default;
}

/// <summary><see cref="Hash256"/> over Fusion RPC: 32 raw bytes.</summary>
public sealed class Hash256Formatter : MemoryPackFormatter<Hash256>
{
    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref Hash256 value) => Write(ref writer, value);

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Hash256 value) => value = Read(ref reader);

    internal static void Write<TBuffer>(ref MemoryPackWriter<TBuffer> writer, in Hash256 value) where TBuffer : IBufferWriter<byte>
    {
        ReadOnlySpan<byte> bytes = value.AsSpan();
        writer.WriteUnmanaged(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
    }

    internal static Hash256 Read(ref MemoryPackReader reader)
    {
        reader.ReadUnmanaged(out ulong a, out ulong b, out ulong c, out ulong d);
        Span<byte> bytes = stackalloc byte[Hash256.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, a);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], b);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], c);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], d);
        return new Hash256(bytes);
    }
}

/// <summary>
/// <see cref="PermissionSnapshot"/> over Fusion RPC (<see cref="ICallPermissions"/>): YFex.Authorization.Public is a leaf and
/// must not reference MemoryPack. Layout v1: version byte, subject, authenticated flag, scope, version, permission ids, role
/// names, relation byte. Ids and names only — never process-local ordinals.
/// </summary>
public sealed class PermissionSnapshotFormatter : MemoryPackFormatter<PermissionSnapshot>
{
    internal const byte Version = 1;

    public override void Serialize<TBuffer>(ref MemoryPackWriter<TBuffer> writer, scoped ref PermissionSnapshot? value)
    {
        if (value is null) { writer.WriteNullObjectHeader(); return; }
        writer.WriteObjectHeader(1);
        writer.WriteUnmanaged(Version);
        writer.WriteString(value.Subject);
        writer.WriteUnmanaged(value.IsAuthenticated);
        writer.WriteString(value.Scope);
        writer.WriteUnmanaged(value.Version);
        WriteStrings(ref writer, value.Permissions);
        WriteStrings(ref writer, value.Roles);
        writer.WriteUnmanaged((byte)value.Relation);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref PermissionSnapshot? value)
    {
        if (!reader.TryReadObjectHeader(out _)) { value = null; return; }
        var version = reader.ReadUnmanaged<byte>();
        if (version != Version)
            throw new MemoryPackSerializationException($"Unsupported PermissionSnapshot wire version {version} (expected {Version}).");
        var subject = reader.ReadString();
        var authenticated = reader.ReadUnmanaged<bool>();
        var scope = reader.ReadString();
        var snapshotVersion = reader.ReadUnmanaged<long>();
        var permissions = ReadStrings(ref reader);
        var roles = ReadStrings(ref reader);
        var relation = (ActorRelation)reader.ReadUnmanaged<byte>();
        value = new PermissionSnapshot(subject, authenticated, scope, snapshotVersion, permissions, roles, relation);
    }

    private static void WriteStrings<TBuffer>(ref MemoryPackWriter<TBuffer> writer, ImmutableArray<string> values)
        where TBuffer : IBufferWriter<byte>
    {
        writer.WriteUnmanaged(values.IsDefault ? 0 : values.Length);
        if (values.IsDefault) return;
        foreach (var item in values) writer.WriteString(item);
    }

    private static ImmutableArray<string> ReadStrings(ref MemoryPackReader reader)
    {
        var count = reader.ReadUnmanaged<int>();
        if (count < 0 || count > 1 << 20) throw new MemoryPackSerializationException($"Invalid PermissionSnapshot list length {count}.");
        var builder = ImmutableArray.CreateBuilder<string>(count);
        for (int i = 0; i < count; i++) builder.Add(reader.ReadString() ?? "");
        return builder.MoveToImmutable();
    }
}
