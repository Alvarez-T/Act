using System.Collections.Immutable;
using System.Globalization;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Cqrs.Runtime;
using YFex.Messaging;
using YFex.Messaging.Rpc;
using YFex.Persistence;
using static YFex.Rules.Admin.RuleAdmin;

namespace YFex.Rules.Admin;

/// <summary>Baseline (client + server): everything requires an authenticated caller; reads are cacheable (offline clients).</summary>
public sealed class RuleAdminBaseline : IAggregateConfiguration<RuleAdmin>
{
    public void Configure(AggregateConfigurationBuilder<RuleAdmin> b)
    {
        b.Command<Commands.PublishTable, long>().RequireAuthenticated()
            .Validation(v =>
            {
                v.Field(c => c.RuleSet).Required();
                v.Field(c => c.TableCode).Required();
                v.Field(c => c.TierPath).Required();
            })
            .Invalidates<Queries.GetRuleData, RuleDataPayload[]>()
            .Invalidates<Queries.GetDataVersions, RuleDataHeader[]>();
        b.Command<Commands.PublishParameters, long>().RequireAuthenticated()
            .Validation(v =>
            {
                v.Field(c => c.RuleSet).Required();
                v.Field(c => c.RuleCode).Required();
                v.Field(c => c.TierPath).Required();
            })
            .Invalidates<Queries.GetRuleData, RuleDataPayload[]>()
            .Invalidates<Queries.GetDataVersions, RuleDataHeader[]>();
        b.Command<Commands.SetRuleOverride, long>().RequireAuthenticated()
            .Validation(v =>
            {
                v.Field(c => c.RuleSet).Required();
                v.Field(c => c.RuleCode).Required();
                v.Field(c => c.Action).Required();
            })
            .Invalidates<Queries.GetRuleData, RuleDataPayload[]>()
            .Invalidates<Queries.GetDataVersions, RuleDataHeader[]>();
        b.Command<Commands.PublishRule, long>().RequireAuthenticated()
            .Validation(v =>
            {
                v.Field(c => c.RuleSet).Required();
                v.Field(c => c.RuleCode).Required();
                v.Field(c => c.TierPath).Required();
            })
            .Invalidates<Queries.GetRuleData, RuleDataPayload[]>()
            .Invalidates<Queries.GetDataVersions, RuleDataHeader[]>();
        b.Command<Commands.ApproveDecision, ApprovalToken>().RequireAuthenticated()
            .Validation(v => v.Field(c => c.Permission).Required());

        b.Query<Queries.GetRuleData, RuleDataPayload[]>().RequireAuthenticated().Cacheable();
        b.Query<Queries.GetDataVersions, RuleDataHeader[]>().RequireAuthenticated().Cacheable();
        b.Query<Queries.GetDataVersion, RuleDataPayload[]>().RequireAuthenticated().Cacheable();
        b.Query<Queries.GetRuleDefinition, RuleDefinitionView>().RequireAuthenticated().Cacheable();
        b.Query<Queries.GetDataChanges, RuleDataChanges>().RequireAuthenticated().Cacheable();   // versions are immutable
        b.Query<Queries.Simulate, SimulationResult>().RequireAuthenticated();
    }
}

/// <summary>
/// Device-side mirror of published rule data (05 §4.4, 11 §2.3): survives restarts so an offline device evaluates with the
/// versions it had, and tells the sync where to resume. Keeps the last N versions per stream (+ the one in force today).
/// </summary>
public interface IRuleDataCache
{
    /// <summary>Cached records of <paramref name="ruleSet"/>, ascending by version.</summary>
    ValueTask<ImmutableArray<RuleDataRecord>> LoadAsync(string ruleSet, CancellationToken ct);

    /// <summary>Stores <paramref name="records"/> (idempotent per version) and prunes old versions.</summary>
    ValueTask SaveAsync(string ruleSet, IReadOnlyList<RuleDataRecord> records, CancellationToken ct);
}

/// <summary>
/// <see cref="IRuleDataCache"/> over the device's <see cref="IKeyValueStore"/> (SQLite / IndexedDB / file / encrypted —
/// whatever the app registered). One entry per version: <c>yfex.rules/{ruleSet}/{version:D19}</c> → MemoryPack
/// <see cref="RuleDataPayload"/>.
/// </summary>
public sealed class KeyValueRuleDataCache(IKeyValueStore store, int keepVersions = 16, TimeProvider? clock = null) : IRuleDataCache
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public int KeepVersions { get; } = keepVersions > 0 ? keepVersions : throw new ArgumentOutOfRangeException(nameof(keepVersions));

    private static string Prefix(string ruleSet) => "yfex.rules/" + ruleSet + "/";

    public async ValueTask<ImmutableArray<RuleDataRecord>> LoadAsync(string ruleSet, CancellationToken ct)
    {
        var keys = await store.GetKeysWithPrefixAsync(Prefix(ruleSet), ct).ConfigureAwait(false);
        var records = new List<RuleDataRecord>(keys.Count);
        foreach (var key in keys)
        {
            var bytes = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null) continue;
            var payload = MemoryPackSerializer.Deserialize<RuleDataPayload>(bytes);
            if (payload is not null) records.Add(payload.ToRecord());
        }
        records.Sort(static (a, b) => a.Info.Version.CompareTo(b.Info.Version));
        return [.. records];
    }

    public async ValueTask SaveAsync(string ruleSet, IReadOnlyList<RuleDataRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        string prefix = Prefix(ruleSet);
        foreach (var r in records)
            await store.SetAsync(Key(prefix, r.Info.Version), MemoryPackSerializer.Serialize(RuleDataPayload.From(r)), ct: ct).ConfigureAwait(false);

        // Prune per stream: keep the newest N versions and, whatever N is, the version in force today (a long run of
        // future-dated versions must not evict it).
        var all = await LoadAsync(ruleSet, ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var streams = new Dictionary<RuleDataKey, List<RuleDataRecord>>();
        foreach (var r in all)
        {
            if (!streams.TryGetValue(r.Info.Key, out var list)) streams[r.Info.Key] = list = [];
            list.Add(r);
        }
        foreach (var list in streams.Values)
        {
            if (list.Count <= KeepVersions) continue;
            long inForce = -1;
            DateOnly best = DateOnly.MinValue;
            foreach (var r in list)
                if (r.Info.EffectiveFrom <= today && (inForce < 0 || r.Info.EffectiveFrom >= best)) { inForce = r.Info.Version; best = r.Info.EffectiveFrom; }
            for (int i = 0; i < list.Count - KeepVersions; i++)                 // ascending → oldest first
                if (list[i].Info.Version != inForce)
                    await store.DeleteAsync(Key(prefix, list[i].Info.Version), ct).ConfigureAwait(false);
        }
    }

    private static string Key(string prefix, long version) => prefix + version.ToString("D19", CultureInfo.InvariantCulture);
}

/// <summary>
/// Client-side distribution (05 §4.4, 11 §2.3):
/// <list type="number">
/// <item>start: the device cache (<see cref="IRuleDataCache"/>, optional) is applied FIRST — an offline restart evaluates
///   with the versions the device had;</item>
/// <item>then only what is new is fetched (<c>GetRuleData(set, lastKnownVersion)</c>), cached and applied;</item>
/// <item>each <see cref="Events.RulesetPublished"/> and each reconnect (<see cref="INetworkStatus"/>, optional) repeats the
///   incremental fetch — versions missed while offline are caught up in order.</item>
/// </list>
/// A failed fetch (offline) keeps what the device has; nothing is lost.
/// </summary>
public sealed class RuleSetSync : IAsyncEventRecipient<Events.RulesetPublished>, IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly RuleSetRegistry _registry;
    private readonly IEventBus _events;
    private readonly IRuleDataCache? _cache;
    private readonly INetworkStatus? _network;
    private readonly Dictionary<string, long> _lastVersion = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _subscription;
    private bool _started;

    public RuleSetSync(IDispatcher dispatcher, RuleSetRegistry registry, IEventBus events, IRuleDataCache? cache = null, INetworkStatus? network = null)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _events = events;
        _cache = cache;
        _network = network;
    }

    /// <summary>Records that failed to decode (removed tables/rules) — for logging.</summary>
    public List<(RuleDataRecord Record, Exception Error)> Failures { get; } = [];

    /// <summary>Errors of background syncs (reconnect) — for logging.</summary>
    public List<Exception> SyncErrors { get; } = [];

    /// <summary>Highest version applied per rule set (0 = none).</summary>
    public long LastVersion(string ruleSet)
    {
        lock (_lastVersion) return _lastVersion.TryGetValue(ruleSet, out long v) ? v : 0;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _subscription ??= _events.SubscribeAsync(this);
        if (!_started && _network is not null) _network.Changed += OnNetworkChanged;
        _started = true;
        if (_cache is not null)
            foreach (var host in _registry.All)
                foreach (var record in await _cache.LoadAsync(host.Name, ct).ConfigureAwait(false))
                    Apply(host, record);
        await SyncAllAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Fetches and applies what is new for every hosted rule set.</summary>
    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        foreach (var host in _registry.All) await SyncAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches the versions of <paramref name="host"/>'s rule set after the last applied one. False = fetch failed.</summary>
    public async Task<bool> SyncAsync(IRuleSetHost host, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _dispatcher.QueryAsync<Queries.GetRuleData, RuleDataPayload[]>(new(host.Name, LastVersion(host.Name)), ct)
                .ConfigureAwait(false);
            if (!result.TryGetValue(out RuleDataPayload[]? payloads) || payloads is null) return false;
            var records = new RuleDataRecord[payloads.Length];
            for (int i = 0; i < records.Length; i++) records[i] = payloads[i].ToRecord();
            if (_cache is not null) await _cache.SaveAsync(host.Name, records, ct).ConfigureAwait(false);
            foreach (var r in records) Apply(host, r);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReceiveAsync(Events.RulesetPublished e, CancellationToken ct = default)
    {
        if (!_registry.TryGet(e.RuleSet, out var host) || e.Version <= LastVersion(e.RuleSet)) return;
        await SyncAsync(host, ct).ConfigureAwait(false);
    }

    private async void OnNetworkChanged(SyncState state)
    {
        if (state != SyncState.Connected) return;
        try { await SyncAllAsync().ConfigureAwait(false); }
        catch (Exception e) { lock (SyncErrors) SyncErrors.Add(e); }        // async void: never let it escape
    }

    private void Apply(IRuleSetHost host, RuleDataRecord record)
    {
        try { host.Apply(record); }
        catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or NotSupportedException or FormatException)
        { Failures.Add((record, e)); }
        lock (_lastVersion)
            if (record.Info.Version > (_lastVersion.TryGetValue(host.Name, out long v) ? v : 0)) _lastVersion[host.Name] = record.Info.Version;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        if (_started && _network is not null) _network.Changed -= OnNetworkChanged;
    }
}

public static class RuleAdminServiceCollectionExtensions
{
    /// <summary>
    /// Hosts a rule set: registers the <see cref="RuleSetHost{TSituation}"/> (used by <c>.Enforce(set)</c> for published data),
    /// its <see cref="IRuleSetHost"/> view (admin) and the <see cref="IDecisionAccessor{TSituation}"/>.
    /// </summary>
    public static IServiceCollection AddYFexRuleSet<TSituation>(this IServiceCollection services, RuleSetHost<TSituation> host)
        where TSituation : ISituation<TSituation>
    {
        services.AddSingleton(host);
        services.AddSingleton<IRuleSetHost>(host);
        services.TryAddSingleton<IDecisionAccessor<TSituation>, DecisionAccessor<TSituation>>();
        return services;
    }

    /// <summary>
    /// Server: the RuleAdmin handlers + publish pipeline. Register an <see cref="IRuleStore"/> (SQL, in-memory…), and an
    /// <see cref="IOverrideTokenIssuer"/> (e.g. <see cref="HmacOverrideTokens"/>) for <c>ApproveDecision</c>.
    /// </summary>
    public static IServiceCollection AddYFexRulesAdmin(this IServiceCollection services, Action<RuleAdminOptions>? configure = null)
    {
        var options = new RuleAdminOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<RuleSetRegistry>();
        services.TryAddSingleton<RulePublisher>();
        services.TryAddTransient<ICommandHandler<Commands.PublishTable, long>, PublishTableHandler>();
        services.TryAddTransient<ICommandHandler<Commands.PublishParameters, long>, PublishParametersHandler>();
        services.TryAddTransient<ICommandHandler<Commands.SetRuleOverride, long>, SetRuleOverrideHandler>();
        services.TryAddTransient<ICommandHandler<Commands.PublishRule, long>, PublishRuleHandler>();
        services.TryAddTransient<ICommandHandler<Commands.ApproveDecision, ApprovalToken>, ApproveDecisionHandler>();
        services.TryAddTransient<IQueryHandler<Queries.GetRuleData, RuleDataPayload[]>, GetRuleDataHandler>();
        services.TryAddTransient<IQueryHandler<Queries.GetDataVersions, RuleDataHeader[]>, GetDataVersionsHandler>();
        services.TryAddTransient<IQueryHandler<Queries.GetDataVersion, RuleDataPayload[]>, GetDataVersionHandler>();
        services.TryAddTransient<IQueryHandler<Queries.GetRuleDefinition, RuleDefinitionView>, GetRuleDefinitionHandler>();
        services.TryAddTransient<IQueryHandler<Queries.GetDataChanges, RuleDataChanges>, GetDataChangesHandler>();
        services.TryAddTransient<IQueryHandler<Queries.Simulate, SimulationResult>, SimulateHandler>();
        return services;
    }

    /// <summary>
    /// Client: keeps the hosted rule sets in sync with the server (call <see cref="RuleSetSync.StartAsync"/> once). With
    /// <paramref name="deviceCache"/> the received versions are mirrored into the registered <see cref="IKeyValueStore"/>
    /// (<see cref="KeyValueRuleDataCache"/>, last <paramref name="keepVersions"/> per stream) so offline restarts keep them.
    /// </summary>
    public static IServiceCollection AddYFexRuleSetSync(this IServiceCollection services, bool deviceCache = true, int keepVersions = 16)
    {
        services.TryAddSingleton<RuleSetRegistry>();
        if (deviceCache)
            services.TryAddSingleton<IRuleDataCache>(sp => new KeyValueRuleDataCache(sp.GetRequiredService<IKeyValueStore>(), keepVersions,
                sp.GetService<TimeProvider>()));
        services.TryAddSingleton<RuleSetSync>();
        return services;
    }

    /// <summary>In-process invokers of the RuleAdmin messages (LocalDispatcher hosts).</summary>
    public static LocalHandlerInvokerRegistryBuilder AddRuleAdmin(this LocalHandlerInvokerRegistryBuilder builder)
        => builder
            .AddCommand<Commands.PublishTable, long>()
            .AddCommand<Commands.PublishParameters, long>()
            .AddCommand<Commands.SetRuleOverride, long>()
            .AddCommand<Commands.PublishRule, long>()
            .AddCommand<Commands.ApproveDecision, ApprovalToken>()
            .AddQuery<Queries.GetRuleData, RuleDataPayload[]>()
            .AddQuery<Queries.GetDataVersions, RuleDataHeader[]>()
            .AddQuery<Queries.GetDataVersion, RuleDataPayload[]>()
            .AddQuery<Queries.GetRuleDefinition, RuleDefinitionView>()
            .AddQuery<Queries.GetDataChanges, RuleDataChanges>()
            .AddQuery<Queries.Simulate, SimulationResult>();
}
