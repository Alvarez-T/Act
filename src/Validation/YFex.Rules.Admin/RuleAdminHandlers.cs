using System.Text;
using System.Text.Json;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Validation;
using static YFex.Rules.Admin.RuleAdmin;

namespace YFex.Rules.Admin;

/// <summary>Permissions not declared on tables (05 §7). Table publishing uses each table's <c>PublishPermission</c>.</summary>
public sealed class RuleAdminOptions
{
    /// <summary>Publishing parameter values (default <c>"rules.parameters.publish"</c>).</summary>
    public string ParameterPublishPermission { get; set; } = "rules.parameters.publish";
    /// <summary>Disabling / replacing rules (default <c>"rules.override"</c>).</summary>
    public string OverridePermission { get; set; } = "rules.override";
    /// <summary>Publishing runtime-authored rules (default <c>"rules.dynamic.publish"</c>).</summary>
    public string RulePublishPermission { get; set; } = "rules.dynamic.publish";
    /// <summary>Reading history / definitions (null = any authenticated user).</summary>
    public string? ViewPermission { get; set; }
    /// <summary>Fallback for tables without <c>PublishPermission</c> (default <c>"rules.tables.publish"</c>).</summary>
    public string TablePublishPermission { get; set; } = "rules.tables.publish";
}

/// <summary>
/// App hook for tier-level publishing (05 §4.1): e.g. the actor must belong to the company/branch/hub named by the
/// tier path (the app's own membership model). Without one, the permission check alone decides.
/// </summary>
public interface IRuleTierGuard
{
    bool CanPublishAt(IAuthContext user, in RuleTierPath tierPath);
}

/// <summary>The rule sets hosted by this process (every registered <see cref="IRuleSetHost"/>).</summary>
public sealed class RuleSetRegistry
{
    private readonly Dictionary<string, IRuleSetHost> _hosts = new(StringComparer.Ordinal);

    public RuleSetRegistry(IEnumerable<IRuleSetHost> hosts)
    {
        foreach (var h in hosts)
            if (!_hosts.TryAdd(h.Name, h)) throw new InvalidOperationException($"Rule set '{h.Name}' is registered twice.");
    }

    public IEnumerable<IRuleSetHost> All => _hosts.Values;

    public bool TryGet(string ruleSet, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IRuleSetHost? host) => _hosts.TryGetValue(ruleSet, out host);

    public IRuleSetHost Get(string ruleSet)
        => _hosts.TryGetValue(ruleSet, out var h)
            ? h
            : throw new ErrorResultException(new Error(ErrorType.NotFound, $"Rule set '{ruleSet}' is not hosted here."));
}

/// <summary>An accepted publish (05 §4 audit record): who/when/note (<see cref="RuleDataInfo"/>) + the diff vs the base version.</summary>
public sealed record RulePublishAudit(RuleDataInfo Info, RuleDataDiff Diff);

/// <summary>
/// App hook for the publish audit trail (05 §4): every accepted version with its diff, and every rejected attempt
/// (unauthorized, invalid, conflict) — e.g. write both to the app's audit log. Optional; failures propagate.
/// </summary>
public interface IRuleAuditSink
{
    ValueTask OnPublishedAsync(RulePublishAudit audit, CancellationToken ct);
    ValueTask OnRejectedAsync(RuleDataKey key, string? actor, Error error, CancellationToken ct);
}

/// <summary>
/// Shared publish pipeline (05 §4): authorize → validate → canonicalize → append (optimistic) → audit → apply → notify.
/// </summary>
public sealed class RulePublisher(RuleSetRegistry registry, IRuleStore store, IPrincipalAccessor principal, RuleAdminOptions options,
    IEventBus? events = null, IRuleTierGuard? tierGuard = null, IRuleAuditSink? audit = null)
{
    internal RuleSetRegistry Registry => registry;
    internal IRuleStore Store => store;

    public async Task<long> PublishAsync(string ruleSet, RuleDataKind kind, string code, string tierPathText, long baseVersion, DateOnly effectiveFrom,
        byte[] payload, string? note, string permission, Func<IRuleSetHost, RuleTierPath, ValidationResult> validate, CancellationToken ct)
    {
        var host = registry.Get(ruleSet);
        var tierPath = ParseTierPath(tierPathText);
        var user = principal.Current;
        string? actor = user.Principal.Identity?.Name ?? CallContextSubject();
        var key = new RuleDataKey(ruleSet, kind, code, tierPath);

        RuleDataInfo info;
        try
        {
            Demand(user, permission);
            if (tierGuard is not null && !tierGuard.CanPublishAt(user, tierPath))
                throw new ErrorResultException(new Error(ErrorType.Unauthorized, $"Not allowed to publish at {tierPath}."));

            var check = validate(host, tierPath);
            if (!check.IsValid) throw new ErrorResultException(Error.Validation(check));

            // Stored form: stable row ids (assigned once, here) + codec formatting → decodes identically everywhere, diffs cleanly.
            payload = host.Canonicalize(kind, code, payload);
            try
            {
                info = await store.AppendAsync(key, baseVersion, effectiveFrom, payload, actor, note, ct).ConfigureAwait(false);
            }
            catch (RuleStoreConflictException conflict)
            {
                throw new ErrorResultException(new Error(ErrorType.Conflict,
                    $"'{code}' was published as version {conflict.ActualVersion} meanwhile (base {conflict.ExpectedVersion}); reload and retry."));
            }
        }
        catch (ErrorResultException rejected) when (audit is not null)
        {
            await audit.OnRejectedAsync(key, actor, rejected.TypedError, ct).ConfigureAwait(false);
            throw;
        }

        if (audit is not null)
        {
            var before = await PreviousPayloadAsync(host, key, baseVersion, ct).ConfigureAwait(false);
            await audit.OnPublishedAsync(new RulePublishAudit(info, RuleDataDiff.Compute(kind, before.Span, payload, baseVersion, info.Version)), ct)
                .ConfigureAwait(false);
        }

        host.Apply(new RuleDataRecord(info, payload));
        if (events is not null)
            await events.PublishAsync(new Events.RulesetPublished(ruleSet, kind, code, tierPathText, info.Version, effectiveFrom), ct: ct).ConfigureAwait(false);
        return info.Version;
    }

    /// <summary>Payload a version replaced: the stored <paramref name="version"/>, else the code rows (tables) / nothing.</summary>
    internal async ValueTask<ReadOnlyMemory<byte>> PreviousPayloadAsync(IRuleSetHost host, RuleDataKey key, long version, CancellationToken ct)
    {
        if (version > 0 && await store.GetAsync(key, version, ct).ConfigureAwait(false) is { } record) return record.Payload;
        return key.Kind == RuleDataKind.Table ? host.DefaultRowsJson(key.Code) : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>The permission that publishing <paramref name="kind"/>/<paramref name="code"/> requires (simulation reuses it).</summary>
    internal string PermissionFor(IRuleSetHost host, RuleDataKind kind, string code) => kind switch
    {
        RuleDataKind.Table => host.TryGetTable(code, out var schema) && schema.PublishPermission is { } p ? p : options.TablePublishPermission,
        RuleDataKind.Parameters => options.ParameterPublishPermission,
        RuleDataKind.Rule => options.RulePublishPermission,
        _ => options.OverridePermission,
    };

    internal void Demand(IAuthContext user, string permission)
    {
        if (!user.IsAuthenticated || !Permission.TryParse(permission, null, out var p) || !user.HasPermission(in p))
            throw new ErrorResultException(new Error(ErrorType.Unauthorized, $"Requires permission '{permission}'."));
    }

    internal void DemandView()
    {
        var user = principal.Current;
        if (!user.IsAuthenticated) throw new ErrorResultException(new Error(ErrorType.Unauthorized, "Authentication required."));
        if (options.ViewPermission is { } view) Demand(user, view);
    }

    internal static RuleTierPath ParseTierPath(string text)
    {
        try { return RuleTierPaths.Parse(text); }
        catch (FormatException e) { throw new ErrorResultException(Error.Validation(ValidationResult.Of(new Violation(FieldPath.Of("TierPath"), RulePublishCodes.Malformed, detail: e.Message)))); }
    }

    private static string? CallContextSubject() => CallContext.Current?.Subject;
}

public sealed class PublishTableHandler(RulePublisher publisher) : ICommandHandler<Commands.PublishTable, long>
{
    public Task<long> HandleAsync(Commands.PublishTable c, CancellationToken ct)
        => publisher.PublishAsync(c.RuleSet, RuleDataKind.Table, c.TableCode, c.TierPath, c.BaseVersion, c.EffectiveFrom, c.RowsJson, c.Note,
            publisher.PermissionFor(publisher.Registry.Get(c.RuleSet), RuleDataKind.Table, c.TableCode),
            (h, scope) => h.ValidateTable(c.TableCode, scope, c.RowsJson), ct);
}

public sealed class PublishParametersHandler(RulePublisher publisher, RuleAdminOptions options) : ICommandHandler<Commands.PublishParameters, long>
{
    public Task<long> HandleAsync(Commands.PublishParameters c, CancellationToken ct)
        => publisher.PublishAsync(c.RuleSet, RuleDataKind.Parameters, c.RuleCode, c.TierPath, c.BaseVersion, c.EffectiveFrom, c.ValuesJson, c.Note,
            options.ParameterPublishPermission, (h, scope) => h.ValidateParameters(c.RuleCode, scope, c.ValuesJson), ct);
}

public sealed class PublishRuleHandler(RulePublisher publisher, RuleAdminOptions options) : ICommandHandler<Commands.PublishRule, long>
{
    public Task<long> HandleAsync(Commands.PublishRule c, CancellationToken ct)
        => publisher.PublishAsync(c.RuleSet, RuleDataKind.Rule, c.RuleCode, c.TierPath, c.BaseVersion, c.EffectiveFrom, c.Spec, c.Note,
            options.RulePublishPermission, (h, tier) => h.ValidateRule(c.RuleCode, tier, c.Spec), ct);
}

public sealed class SetRuleOverrideHandler(RulePublisher publisher, RuleAdminOptions options) : ICommandHandler<Commands.SetRuleOverride, long>
{
    public Task<long> HandleAsync(Commands.SetRuleOverride c, CancellationToken ct)
    {
        RuleOverrideAction action = c.Action switch
        {
            "disable" => RuleOverrideAction.Disable,
            "enable" => RuleOverrideAction.Enable,
            "replace" when c.ReplacementCode is { } r => RuleOverrideAction.ReplaceWith(r),
            _ => throw new ErrorResultException(Error.Validation(ValidationResult.Of(
                new Violation(FieldPath.Of("Action"), RulePublishCodes.Malformed, detail: "disable | enable | replace (+ReplacementCode)")))),
        };
        return publisher.PublishAsync(c.RuleSet, RuleDataKind.Override, c.RuleCode, c.TierPath, c.BaseVersion, c.EffectiveFrom,
            RuleDataCodec.WriteOverride(action), c.Note, options.OverridePermission, (h, scope) => h.ValidateOverride(c.RuleCode, scope, action), ct);
    }
}

public sealed class ApproveDecisionHandler(IOverrideTokenIssuer issuer, IPrincipalAccessor principal, RulePublisher publisher, TimeProvider? clock = null)
    : ICommandHandler<Commands.ApproveDecision, ApprovalToken>
{
    public Task<ApprovalToken> HandleAsync(Commands.ApproveDecision c, CancellationToken ct)
    {
        var user = principal.Current;
        publisher.Demand(user, c.Permission);                                  // the APPROVER must hold the permission
        string approver = user.Principal.Identity?.Name ?? CallContext.Current?.Subject
            ?? throw new ErrorResultException(new Error(ErrorType.Unauthorized, "Approver identity unknown."));
        var token = issuer.Issue(approver, c.Permission, c.Fingerprint, (clock ?? TimeProvider.System).GetUtcNow());
        return Task.FromResult(ApprovalToken.From(token));
    }
}

public sealed class GetRuleDataHandler(RulePublisher publisher) : IQueryHandler<Queries.GetRuleData, RuleDataPayload[]>
{
    public async Task<RuleDataPayload[]> HandleAsync(Queries.GetRuleData q, CancellationToken ct)
    {
        publisher.DemandView();
        publisher.Registry.Get(q.RuleSet);
        var records = await publisher.Store.LoadAsync(q.RuleSet, q.AfterVersion, ct).ConfigureAwait(false);
        var result = new RuleDataPayload[records.Length];
        for (int i = 0; i < result.Length; i++) result[i] = RuleDataPayload.From(records[i]);
        return result;
    }
}

/// <summary>Diff of one version vs the version before it in the same stream (or the code rows for a table's first version).</summary>
public sealed class GetDataChangesHandler(RulePublisher publisher) : IQueryHandler<Queries.GetDataChanges, RuleDataChanges>
{
    public async Task<RuleDataChanges> HandleAsync(Queries.GetDataChanges q, CancellationToken ct)
    {
        publisher.DemandView();
        var host = publisher.Registry.Get(q.RuleSet);
        var key = new RuleDataKey(q.RuleSet, q.Kind, q.Code, RulePublisher.ParseTierPath(q.TierPath));
        var record = await publisher.Store.GetAsync(key, q.Version, ct).ConfigureAwait(false)
            ?? throw new ErrorResultException(new Error(ErrorType.NotFound, $"'{key}' has no version {q.Version}."));

        long previous = 0;
        foreach (var h in await publisher.Store.GetVersionsAsync(key, ct).ConfigureAwait(false))   // newest first
            if (h.Version < q.Version) { previous = h.Version; break; }
        var before = await publisher.PreviousPayloadAsync(host, key, previous, ct).ConfigureAwait(false);
        return RuleDataChanges.From(record.Info, RuleDataDiff.Compute(q.Kind, before.Span, record.Payload.Span, previous, q.Version));
    }
}

/// <summary>What-if (05 §4.0): validates the draft like a publish would, then replays <see cref="ISituationSamples"/> with it.</summary>
public sealed class SimulateHandler(RulePublisher publisher, IPrincipalAccessor principal, ISituationSamples? samples = null)
    : IQueryHandler<Queries.Simulate, SimulationResult>
{
    public async Task<SimulationResult> HandleAsync(Queries.Simulate q, CancellationToken ct)
    {
        var host = publisher.Registry.Get(q.RuleSet);
        publisher.Demand(principal.Current, publisher.PermissionFor(host, q.Kind, q.Code));
        if (samples is null)
            throw new ErrorResultException(new Error(ErrorType.NotFound, "No ISituationSamples registered — the app stores no situations to replay."));

        var tierPath = RulePublisher.ParseTierPath(q.TierPath);
        var check = q.Kind switch
        {
            RuleDataKind.Table => host.ValidateTable(q.Code, tierPath, q.Payload),
            RuleDataKind.Parameters => host.ValidateParameters(q.Code, tierPath, q.Payload),
            RuleDataKind.Rule => host.ValidateRule(q.Code, tierPath, q.Payload),
            _ => ValidateOverride(host, q.Code, tierPath, q.Payload),
        };
        if (!check.IsValid) throw new ErrorResultException(Error.Validation(check));

        int max = q.MaxSamples > 0 ? q.MaxSamples : 1000;
        var situations = new List<ReadOnlyMemory<byte>>();
        await foreach (var s in samples.GetAsync(q.RuleSet, tierPath, q.SampleFrom, q.SampleTo, max, ct).ConfigureAwait(false))
        {
            situations.Add(s);
            if (situations.Count >= max) break;
        }
        return SimulationResult.From(host.Simulate(q.Kind, q.Code, tierPath, q.Payload, situations, Math.Max(q.MaxExamples, 0)));
    }

    private static ValidationResult ValidateOverride(IRuleSetHost host, string code, in RuleTierPath tierPath, byte[] payload)
    {
        try { return host.ValidateOverride(code, tierPath, RuleDataCodec.ReadOverride(payload)); }
        catch (JsonException e) { return ValidationResult.Of(new Violation(FieldPath.Of("Payload"), RulePublishCodes.Malformed, detail: e.Message)); }
    }
}

public sealed class GetDataVersionsHandler(RulePublisher publisher) : IQueryHandler<Queries.GetDataVersions, RuleDataHeader[]>
{
    public async Task<RuleDataHeader[]> HandleAsync(Queries.GetDataVersions q, CancellationToken ct)
    {
        publisher.DemandView();
        var infos = await publisher.Store.GetVersionsAsync(new(q.RuleSet, q.Kind, q.Code, RulePublisher.ParseTierPath(q.TierPath)), ct).ConfigureAwait(false);
        var result = new RuleDataHeader[infos.Length];
        for (int i = 0; i < result.Length; i++) result[i] = RuleDataHeader.From(infos[i]);
        return result;
    }
}

public sealed class GetDataVersionHandler(RulePublisher publisher) : IQueryHandler<Queries.GetDataVersion, RuleDataPayload[]>
{
    public async Task<RuleDataPayload[]> HandleAsync(Queries.GetDataVersion q, CancellationToken ct)
    {
        publisher.DemandView();
        var record = await publisher.Store.GetAsync(new(q.RuleSet, q.Kind, q.Code, RulePublisher.ParseTierPath(q.TierPath)), q.Version, ct).ConfigureAwait(false);
        return record is null ? [] : [RuleDataPayload.From(record)];
    }
}

/// <summary>06 §6: table ref → the row as stored in that version; code rule → catalog + parameter values of that data version.</summary>
public sealed class GetRuleDefinitionHandler(RulePublisher publisher) : IQueryHandler<Queries.GetRuleDefinition, RuleDefinitionView>
{
    public async Task<RuleDefinitionView> HandleAsync(Queries.GetRuleDefinition q, CancellationToken ct)
    {
        publisher.DemandView();
        var host = publisher.Registry.Get(q.RuleSet);
        if (!host.Catalog.TryGet(q.Code, out var entry))
        {
            // A runtime-authored rule (05 §8): its definition IS the stored spec of that data version.
            if (q.DataVersion > 0 && await publisher.Store.GetByVersionAsync(q.RuleSet, q.DataVersion, ct).ConfigureAwait(false) is
                { Info.Key.Kind: RuleDataKind.Rule } spec && spec.Info.Key.Code == q.Code)
            {
                using var doc = JsonDocument.Parse(spec.Payload);
                string group = doc.RootElement.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
                return new RuleDefinitionView(q.RuleSet, q.Code, group, "", false, false, q.Version, q.Tier, q.DataVersion, default,
                    Encoding.UTF8.GetString(spec.Payload.Span), spec.Info.PublishedBy, spec.Info.PublishedAt, spec.Info.Note);
            }
            throw new ErrorResultException(new Error(ErrorType.NotFound, $"'{q.Code}' is not part of rule set '{q.RuleSet}'."));
        }

        string? json = null, by = null, note = null;
        DateTimeOffset? at = null;
        if (q.DataVersion > 0
            && await publisher.Store.GetByVersionAsync(q.RuleSet, q.DataVersion, ct).ConfigureAwait(false) is { } r
            && r.Info.Key.Code == q.Code)
        {
            json = entry.IsTable && q.RowId != Guid.Empty ? ExtractRow(r.Payload.Span, q.RowId) : Encoding.UTF8.GetString(r.Payload.Span);
            (by, at, note) = (r.Info.PublishedBy, r.Info.PublishedAt, r.Info.Note);
        }
        else if (entry.IsTable && q.RowId != Guid.Empty)
        {
            json = ExtractRow(host.DefaultRowsJson(q.Code), q.RowId);                // a code (default) row
        }
        return new RuleDefinitionView(q.RuleSet, entry.Code, entry.Group, entry.Stage.Code, entry.IsTable, entry.IsSealed, entry.Version,
            q.Tier, q.DataVersion, q.RowId, json, by, at, note);
    }

    private static string? ExtractRow(ReadOnlySpan<byte> document, Guid rowId)
    {
        using var doc = JsonDocument.Parse(document.ToArray());
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            if (row.TryGetProperty("id", out var id) && id.GetGuid() == rowId) return row.GetRawText();
        return null;
    }
}
