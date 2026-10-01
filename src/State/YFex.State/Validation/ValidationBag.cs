using System.Collections.Generic;
using System.Threading;
using YFex.Validation;

namespace YFex.State.Validation;

/// <summary>
/// Per-<see cref="StateObject"/> store of violations (07 §2). Lazy: created on first use, so view models
/// without validation pay nothing. Keyed by VM property path (<c>"Name"</c>, <c>"Lines[2].Quantity"</c>;
/// <c>""</c> = form level). Generated code passes compile-time constant paths, so lookups never allocate.
/// <para>
/// Every stored violation carries its <see cref="ViolationOrigin"/>. A producer only ever REPLACES its own
/// previous violations (<see cref="Replace"/>, <see cref="ReplaceAll(ViolationSource, ushort, ReadOnlySpan{Violation})"/>):
/// re-running an invariant clears the invariant's old errors, not the field's own.
/// </para>
/// <para>
/// Owner ids used by generated code under <see cref="ViolationSource.Field"/>: <see cref="FieldOwner"/>
/// (sync rules, own + borrowed), <see cref="AsyncOwner"/> (async rules). Ids at or above
/// <see cref="SubmitOwnerBase"/> hold submit-time results (contract validation in <c>ValidateAll</c>);
/// they and <see cref="ViolationSource.Server"/> violations are cleared by <see cref="ClearStale"/> when the
/// user edits the path.
/// </para>
/// Thread model: mutations take a lock (async completions may arrive on the thread pool when no
/// SynchronizationContext exists); events are raised after the lock is released. Entries are only
/// added on the owner thread, so reads are lock-free.
/// </summary>
public sealed class ValidationBag
{
    /// <summary>Owner id of sync field rules (own and borrowed).</summary>
    public const ushort FieldOwner = 0;

    /// <summary>Owner id of async field rules (<see cref="IAsyncValueRule{T}"/>, legacy async validators).</summary>
    public const ushort AsyncOwner = 1;

    /// <summary>First owner id reserved for submit-time results (<c>ValidateAll</c> contract checks).</summary>
    public const ushort SubmitOwnerBase = 0x8000;

    private static readonly Violation[] s_noViolations = [];
    private static readonly ErrorItem[] s_noItems = [];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;
    private int _errorCount;
    private bool _submitAttempted;
    private bool _anyTouched;
    private ShowErrors _show = ShowErrors.AfterTouched;

    public ValidationBag() : this(string.Empty)
    {
    }

    /// <param name="typeKey">Owner type name — prefix of field display-name keys (<c>"CustomerFormVm.Name"</c>, 10 §5).</param>
    public ValidationBag(string typeKey)
    {
        TypeKey = typeKey;
        _spanLookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Raised with the path (<c>""</c> = form level / everything) when violations, status or visibility change.</summary>
    public event Action<string>? ValidationChanged;

    /// <summary>Owner type name used for field display-name keys.</summary>
    public string TypeKey { get; }

    /// <summary>Any ERROR-severity violation exists (visible or not). Use for Gates / CanSave.</summary>
    public bool HasErrors => Volatile.Read(ref _errorCount) > 0;

    public int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>When violations become visible (07 §6.2). Set by generated code from <c>[ValidationOptions]</c>.</summary>
    public ShowErrors Show
    {
        get => _show;
        set
        {
            if (_show == value) return;
            _show = value;
            ValidationChanged?.Invoke(string.Empty);
        }
    }

    /// <summary>
    /// Active profile for per-keystroke validation (07 §4.3). Generated code sets it to the first declared
    /// <c>[ValidatesAs(Profile = …)]</c>; <c>default</c> = every rule.
    /// </summary>
    public ProfileId Profile { get; set; }

    /// <summary>Save was attempted → <see cref="ShowErrors.AfterSubmit"/> / <see cref="ShowErrors.AfterTouched"/> errors become visible.</summary>
    public bool SubmitAttempted => _submitAttempted;

    /// <summary>Form-level violations (empty path).</summary>
    public ViolationList FormLevel => Get(string.Empty);

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the violations for <paramref name="path"/> produced by (<paramref name="source"/>, <paramref name="ownerId"/>)
    /// with the writer's content; other origins on the path are kept. Every violation is stored under
    /// <paramref name="path"/>'s entry with that origin. No allocation and no event when nothing changed.
    /// </summary>
    public void Replace(string path, ViolationSource source, ushort ownerId, ref ViolationWriter w)
        => Replace(path, source, ownerId, w.AsSpan());

    /// <inheritdoc cref="Replace(string, ViolationSource, ushort, ref ViolationWriter)"/>
    public void Replace(string path, ViolationSource source, ushort ownerId, ReadOnlySpan<Violation> violations)
    {
        var origin = new ViolationOrigin(source, ownerId);
        bool changed;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out Entry? entry))
            {
                // A Field result for a path we never saw: remember that it was validated (status Valid)
                // even when there is nothing to store. Other sources create entries only when needed.
                if (violations.IsEmpty && source != ViolationSource.Field) return;
                entry = new Entry(path);
                _entries.Add(path, entry);
            }
            changed = !entry.Validated && source == ViolationSource.Field;
            if (source == ViolationSource.Field) entry.Validated = true;
            changed |= ReplaceOrigin(entry, origin, violations);
        }
        if (changed) ValidationChanged?.Invoke(path);
    }

    /// <summary>
    /// Replaces every violation of (<paramref name="source"/>, <paramref name="ownerId"/>) — on any path — with
    /// <paramref name="violations"/>, distributed by their own paths (object-level → <c>""</c>). Used for
    /// invariants, rule decisions and server results. Map contract paths to VM paths first
    /// (<see cref="global::YFex.Validation.ValidationResult.MapPaths{TMap}"/>). No allocation when the origin had nothing and gets nothing.
    /// </summary>
    public void ReplaceAll(ViolationSource source, ushort ownerId, ReadOnlySpan<Violation> violations)
    {
        var origin = new ViolationOrigin(source, ownerId);
        // Keys once (indexed paths format a string); only when there is something to store.
        string[]? keys = violations.IsEmpty ? null : new string[violations.Length];
        for (int i = 0; i < violations.Length; i++) keys![i] = KeyOf(in violations[i]);

        List<string>? changedPaths = null;
        lock (_lock)
        {
            // 1. Paths that currently hold this origin but receive nothing → cleared (no allocation when none).
            foreach (Entry entry in _entries.Values)
            {
                if (!entry.HasOrigin(origin)) continue;
                if (keys is not null && Array.IndexOf(keys, entry.Path) >= 0) continue;
                if (ReplaceOrigin(entry, origin, default)) (changedPaths ??= new()).Add(entry.Path);
            }

            // 2. Paths that receive violations (each key handled once, at its first occurrence).
            for (int i = 0; i < violations.Length; i++)
            {
                string key = keys![i];
                if (Array.IndexOf(keys, key) != i) continue;
                int count = 0;
                for (int j = i; j < keys.Length; j++) if (keys[j] == key) count++;
                var subset = new Violation[count];
                for (int j = i, n = 0; j < keys.Length; j++) if (keys[j] == key) subset[n++] = violations[j];

                if (!_entries.TryGetValue(key, out Entry? entry))
                {
                    entry = new Entry(key);
                    _entries.Add(key, entry);
                }
                if (ReplaceOrigin(entry, origin, subset)) (changedPaths ??= new()).Add(key);
            }
        }
        RaiseChanged(changedPaths);
    }

    /// <inheritdoc cref="ReplaceAll(ViolationSource, ushort, ReadOnlySpan{Violation})"/>
    public void ReplaceAll(ViolationSource source, ushort ownerId, ref ViolationWriter w)
        => ReplaceAll(source, ownerId, w.AsSpan());

    /// <inheritdoc cref="ReplaceAll(ViolationSource, ushort, ReadOnlySpan{Violation})"/>
    public void ReplaceAll(ViolationSource source, ushort ownerId, in global::YFex.Validation.ValidationResult result)
        => ReplaceAll(source, ownerId, result.Violations.AsSpan());

    /// <summary>
    /// Stores a server result (<see cref="ViolationSource.Server"/>, always visible) replacing the previous one.
    /// Paths must already be VM paths — generated <c>ApplyServer&lt;TContract&gt;</c> maps contract paths first.
    /// </summary>
    public ServerOutcome ApplyServer(in global::YFex.Validation.ValidationResult result)
    {
        ReplaceAll(ViolationSource.Server, FieldOwner, result.Violations.AsSpan());
        return result.IsValid ? ServerOutcome.Ok : ServerOutcome.Invalid;
    }

    /// <summary>Clears <paramref name="path"/> (every origin, or only <paramref name="source"/>).</summary>
    public void Clear(string path, ViolationSource? source = null)
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out Entry? entry)) return;
            changed = RemoveWhere(entry, source is { } s ? v => v.Origin.Source == s : static _ => true);
        }
        if (changed) ValidationChanged?.Invoke(path);
    }

    /// <summary>
    /// Clears what an edit of <paramref name="path"/> makes stale: server verdicts and submit-time results.
    /// Called by generated setter hooks; no allocation when there is nothing to clear.
    /// </summary>
    public void ClearStale(string path)
    {
        if (!_entries.TryGetValue(path, out Entry? entry) || entry.Items.Length == 0) return;
        bool changed;
        lock (_lock)
        {
            changed = RemoveWhere(entry, static v => v.Origin.Source == ViolationSource.Server || v.Origin.OwnerId >= SubmitOwnerBase);
        }
        if (changed) ValidationChanged?.Invoke(path);
    }

    /// <summary>Removes every violation and interaction state.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.AsyncCts?.Cancel();
                entry.AsyncCts = null;
                entry.AsyncVersion++;
            }
            _entries.Clear();
            _errorCount = 0;
            _submitAttempted = false;
            _anyTouched = false;
        }
        ValidationChanged?.Invoke(string.Empty);
    }

    // ── Interaction state (display policy) ───────────────────────────────────

    /// <summary>The control for <paramref name="path"/> lost focus / committed once.</summary>
    public void Touch(string path)
    {
        bool raise;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out Entry? entry))
            {
                entry = new Entry(path);
                _entries.Add(path, entry);
            }
            if (entry.Touched) return;
            entry.Touched = true;
            bool firstTouch = !_anyTouched;
            _anyTouched = true;
            raise = entry.Items.Length > 0 || firstTouch;
        }
        if (raise) ValidationChanged?.Invoke(path);
    }

    /// <inheritdoc cref="Touch(string)"/>
    public void Touch(ReadOnlySpan<char> path)
    {
        if (_spanLookup.TryGetValue(path, out Entry? entry)) Touch(entry.Path);
        else Touch(path.ToString());
    }

    public bool IsTouched(string path) => _entries.TryGetValue(path, out Entry? e) && e.Touched;

    /// <summary>Save was clicked: hidden errors become visible (raises <see cref="ValidationChanged"/> with <c>""</c>).</summary>
    public void MarkSubmitAttempted()
    {
        if (_submitAttempted) return;
        _submitAttempted = true;
        ValidationChanged?.Invoke(string.Empty);
    }

    /// <summary>Forgets touched/submitted state (e.g. after a successful save, to start a new record).</summary>
    public void ResetInteraction()
    {
        lock (_lock)
        {
            foreach (Entry entry in _entries.Values) entry.Touched = false;
            _submitAttempted = false;
            _anyTouched = false;
        }
        ValidationChanged?.Invoke(string.Empty);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>Any Error on <paramref name="path"/> (any origin). Used to skip invariants whose inputs failed.</summary>
    public bool HasErrorsFor(string path) => _entries.TryGetValue(path, out Entry? e) && e.ErrorCount > 0;

    /// <summary>
    /// Any Error from FIELD rules on <paramref name="path"/> (own, borrowed, async). Generated code skips an invariant
    /// whose inputs fail their own rules — its own previous output must not count, or it could never re-run.
    /// </summary>
    public bool HasFieldErrorsFor(string path)
    {
        if (!_entries.TryGetValue(path, out Entry? e) || e.ErrorCount == 0) return false;
        foreach (ref readonly Violation v in e.Items.AsSpan())
            if (v.IsError && v.Origin.Source == ViolationSource.Field && v.Origin.OwnerId < SubmitOwnerBase) return true;
        return false;
    }

    /// <summary>
    /// Stores a submit-time contract result (<c>ValidateAll</c>) under (<see cref="ViolationSource.Field"/>,
    /// <paramref name="ownerId"/> ≥ <see cref="SubmitOwnerBase"/>): contract paths are mapped to VM paths (unknown →
    /// form level) and field violations the per-field rules already reported (same path and code) are dropped.
    /// </summary>
    public void ReplaceSubmitResult<TMap>(ushort ownerId, ReadOnlySpan<Violation> contractViolations, ref TMap map)
        where TMap : struct, IPathMap, allows ref struct
    {
        if (contractViolations.IsEmpty)
        {
            ReplaceAll(ViolationSource.Field, ownerId, default(ReadOnlySpan<Violation>));
            return;
        }
        var kept = new Violation[contractViolations.Length];
        int n = 0;
        foreach (ref readonly Violation v in contractViolations)
        {
            Violation mapped = map.TryMap(v.Path, out FieldPath to) ? v.WithPath(in to) : v.WithPath(FieldPath.Empty);
            if (v.Origin.Source == ViolationSource.Field && IsReportedElsewhere(in mapped, ownerId)) continue;
            kept[n++] = mapped;
        }
        ReplaceAll(ViolationSource.Field, ownerId, kept.AsSpan(0, n));
    }

    private bool IsReportedElsewhere(in Violation v, ushort ownerId)
    {
        if (!_entries.TryGetValue(KeyOf(in v), out Entry? e)) return false;
        foreach (ref readonly Violation existing in e.Items.AsSpan())
            if (existing.Code == v.Code && existing.Origin.OwnerId != ownerId && existing.Origin.Source == ViolationSource.Field) return true;
        return false;
    }

    /// <summary>All violations for <paramref name="path"/>, regardless of visibility. No allocation.</summary>
    public ViolationList Get(string path)
        => _entries.TryGetValue(path, out Entry? e) ? ViolationList.FromOwnedArray(e.Items) : default;

    /// <inheritdoc cref="Get(string)"/>
    public ViolationList Get(ReadOnlySpan<char> path)
        => _spanLookup.TryGetValue(path, out Entry? e) ? ViolationList.FromOwnedArray(e.Items) : default;

    /// <summary>
    /// Violations the user should see for <paramref name="path"/> (07 §6.2): all of them when the display policy
    /// allows, otherwise only <see cref="ViolationSource.Server"/> / <see cref="ViolationSource.Rule"/> /
    /// <see cref="ViolationSource.Manual"/> ones (always visible). Allocates only for a mixed subset.
    /// </summary>
    public ViolationList GetVisible(string path)
        => _entries.TryGetValue(path, out Entry? e) ? Visible(e) : default;

    /// <inheritdoc cref="GetVisible(string)"/>
    public ViolationList GetVisible(ReadOnlySpan<char> path)
        => _spanLookup.TryGetValue(path, out Entry? e) ? Visible(e) : default;

    /// <summary>
    /// Stable <see cref="ErrorItem"/> objects for the visible violations of <paramref name="path"/> (INDEI).
    /// The same objects are returned until the violations change — no re-boxing per call.
    /// </summary>
    public IReadOnlyList<ErrorItem> GetVisibleItems(string path)
    {
        if (!_entries.TryGetValue(path, out Entry? e) || e.Items.Length == 0) return s_noItems;
        ErrorItem[] items = e.ErrorItems;
        if (IsPolicyVisible(e)) return items;
        int n = 0;
        foreach (ErrorItem item in items) if (IsAlwaysVisible(item.Violation.Origin.Source)) n++;
        if (n == 0) return s_noItems;
        if (n == items.Length) return items;
        var result = new ErrorItem[n];
        n = 0;
        foreach (ErrorItem item in items) if (IsAlwaysVisible(item.Violation.Origin.Source)) result[n++] = item;
        return result;
    }

    /// <summary>Every visible <see cref="ErrorItem"/> (form level first). For INDEI <c>GetErrors(null)</c>.</summary>
    public List<ErrorItem> GetAllVisibleItems()
    {
        var result = new List<ErrorItem>();
        result.AddRange(GetVisibleItems(string.Empty));
        foreach (Entry e in _entries.Values)
            if (e.Path.Length > 0) result.AddRange(GetVisibleItems(e.Path));
        return result;
    }

    /// <summary>Any VISIBLE Error (INDEI <c>HasErrors</c>: controls turn red only when the policy allows).</summary>
    public bool HasVisibleErrors
    {
        get
        {
            if (Volatile.Read(ref _errorCount) == 0) return false;
            foreach (Entry e in _entries.Values)
            {
                if (e.ErrorCount == 0) continue;
                if (IsPolicyVisible(e)) return true;
                foreach (ref readonly Violation v in e.Items.AsSpan())
                    if (v.IsError && IsAlwaysVisible(v.Origin.Source)) return true;
            }
            return false;
        }
    }

    /// <summary>Pristine | Validating | Valid | Invalid | Warned — ignores visibility.</summary>
    public FieldStatus GetStatus(string path)
        => _entries.TryGetValue(path, out Entry? e) ? StatusOf(e) : FieldStatus.Pristine;

    /// <inheritdoc cref="GetStatus(string)"/>
    public FieldStatus GetStatus(ReadOnlySpan<char> path)
        => _spanLookup.TryGetValue(path, out Entry? e) ? StatusOf(e) : FieldStatus.Pristine;

    /// <summary>Rich per-field state (<see cref="FieldState"/> union) for code that switches on it.</summary>
    public FieldState GetState(string path)
    {
        if (!_entries.TryGetValue(path, out Entry? e)) return FieldState.Pristine;
        return StatusOf(e) switch
        {
            FieldStatus.Validating => FieldState.Validating,
            FieldStatus.Valid => FieldState.Valid,
            FieldStatus.Invalid => new FieldState(new FieldState.InvalidCase(ViolationList.FromOwnedArray(e.Items))),
            FieldStatus.Warned => new FieldState(new FieldState.WarnedCase(ViolationList.FromOwnedArray(e.Items))),
            _ => FieldState.Pristine,
        };
    }

    /// <summary>Snapshot of every stored violation (form level first). Allocates one array when non-empty.</summary>
    public global::YFex.Validation.ValidationResult ToResult()
    {
        int count = 0;
        foreach (Entry e in _entries.Values) count += e.Items.Length;
        if (count == 0) return default;
        var all = new Violation[count];
        int n = 0;
        if (_entries.TryGetValue(string.Empty, out Entry? form))
        {
            form.Items.CopyTo(all, 0);
            n = form.Items.Length;
        }
        foreach (Entry e in _entries.Values)
        {
            if (e.Path.Length == 0) continue;
            e.Items.CopyTo(all, n);
            n += e.Items.Length;
        }
        return new global::YFex.Validation.ValidationResult(ViolationList.FromOwnedArray(all));
    }

    // ── Async bookkeeping (replaces the per-property fields of the Phase 0 fix) ─

    /// <summary>
    /// Starts an async check for <paramref name="path"/>: cancels the previous ticket, clears the previous async
    /// result (it belonged to an older value) and sets the status to Validating.
    /// </summary>
    public AsyncValidationTicket BeginAsync(string path)
    {
        AsyncValidationTicket ticket;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out Entry? entry))
            {
                entry = new Entry(path);
                _entries.Add(path, entry);
            }
            entry.AsyncCts?.Cancel();
            var cts = new CancellationTokenSource();
            entry.AsyncCts = cts;
            entry.Validating = true;
            ReplaceOrigin(entry, new ViolationOrigin(ViolationSource.Field, AsyncOwner), default);
            ticket = new AsyncValidationTicket(path, ++entry.AsyncVersion, cts.Token);
        }
        ValidationChanged?.Invoke(path);
        return ticket;
    }

    /// <summary>True while <paramref name="ticket"/> is the latest check of its path (not superseded/cancelled).</summary>
    public bool IsCurrent(in AsyncValidationTicket ticket)
        => ticket.Path is not null && _entries.TryGetValue(ticket.Path, out Entry? e)
           && Volatile.Read(ref e.AsyncVersion) == ticket.Version;

    /// <summary>
    /// Stores the async result. Ignored (returns false) when a newer ticket exists or the check was cancelled —
    /// a slow older check can never overwrite a newer one.
    /// </summary>
    public bool CompleteAsync(in AsyncValidationTicket ticket, ReadOnlySpan<Violation> violations)
    {
        if (ticket.Path is null) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(ticket.Path, out Entry? entry) || entry.AsyncVersion != ticket.Version) return false;
            entry.Validating = false;
            entry.AsyncCts?.Dispose();
            entry.AsyncCts = null;
            ReplaceOrigin(entry, new ViolationOrigin(ViolationSource.Field, AsyncOwner), violations);
        }
        ValidationChanged?.Invoke(ticket.Path);
        return true;
    }

    /// <inheritdoc cref="CompleteAsync(in AsyncValidationTicket, ReadOnlySpan{Violation})"/>
    public bool CompleteAsync(in AsyncValidationTicket ticket, in global::YFex.Validation.ValidationResult result)
        => CompleteAsync(in ticket, result.Violations.AsSpan());

    /// <summary>
    /// Invalidates any in-flight async check for <paramref name="path"/> and clears its result (used when the
    /// sync rules fail: the async verdict about the value is irrelevant).
    /// </summary>
    public void CancelAsync(string path)
    {
        if (!_entries.TryGetValue(path, out Entry? entry)) return;
        bool changed;
        lock (_lock)
        {
            changed = entry.Validating;
            entry.AsyncCts?.Cancel();
            entry.AsyncCts = null;
            if (entry.Validating) entry.AsyncVersion++;
            entry.Validating = false;
            changed |= ReplaceOrigin(entry, new ViolationOrigin(ViolationSource.Field, AsyncOwner), default);
        }
        if (changed) ValidationChanged?.Invoke(path);
    }

    // ── Legacy (Phase 0) string API ─────────────────────────────────────────

    /// <summary>
    /// Stores one message for <paramref name="propertyName"/> (replacing the previous manual one) as a
    /// <see cref="ViolationSource.Manual"/> violation (always visible) whose text bypasses the catalog.
    /// </summary>
#pragma warning disable CS0618
    [Obsolete("Add coded violations: Replace(path, ViolationSource.Manual, 0, …) with a RuleCode (messages come from the catalog).")]
    public void Set(string propertyName, string message, ValidationSeverity severity = ValidationSeverity.Error)
    {
        var v = new Violation(propertyName.Length == 0 ? FieldPath.Empty : FieldPath.Of(propertyName),
            LegacyValidation.Code, (Severity)(byte)severity, default, default, message);
        Replace(propertyName, ViolationSource.Manual, FieldOwner, new ReadOnlySpan<Violation>(in v));
    }
#pragma warning restore CS0618

    /// <summary>First visible message for <paramref name="propertyName"/>, or null.</summary>
    [Obsolete("Use Get / GetVisible (violations) or GetVisibleItems (ErrorItem.Message).")]
    public string? GetError(string propertyName)
    {
        IReadOnlyList<ErrorItem> items = GetVisibleItems(propertyName);
        return items.Count > 0 ? items[0].Message : null;
    }

    /// <summary>Visible messages for <paramref name="propertyName"/>; null/empty = everything visible.</summary>
    [Obsolete("Use GetVisibleItems (ErrorItem) — messages are resolved from the catalog.")]
    public IEnumerable<string> GetErrors(string? propertyName)
    {
        IReadOnlyList<ErrorItem> items = string.IsNullOrEmpty(propertyName) ? GetAllVisibleItems() : GetVisibleItems(propertyName);
        foreach (ErrorItem item in items) yield return item.Message;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private sealed class Entry(string path)
    {
        public readonly string Path = path;
        public Violation[] Items = s_noViolations;   // immutable once published: replaced, never mutated
        public ErrorItem[] ErrorItems = s_noItems;   // parallel to Items
        public int ErrorCount;
        public bool Touched;
        public bool Validated;
        public bool Validating;
        public int AsyncVersion;
        public CancellationTokenSource? AsyncCts;

        public bool HasOrigin(ViolationOrigin origin)
        {
            foreach (ref readonly Violation v in Items.AsSpan())
                if (v.Origin == origin) return true;
            return false;
        }
    }

    private bool IsPolicyVisible(Entry e) => _show switch
    {
        ShowErrors.Immediately => true,
        ShowErrors.AfterSubmit => _submitAttempted,
        _ => _submitAttempted || (e.Path.Length == 0 ? _anyTouched : e.Touched),
    };

    // Server verdicts, rule decisions and violations app code added on purpose are shown regardless of the policy.
    private static bool IsAlwaysVisible(ViolationSource s) => s is ViolationSource.Server or ViolationSource.Rule or ViolationSource.Manual;

    private ViolationList Visible(Entry e)
    {
        Violation[] items = e.Items;
        if (items.Length == 0 || IsPolicyVisible(e)) return ViolationList.FromOwnedArray(items);
        int n = 0;
        foreach (ref readonly Violation v in items.AsSpan()) if (IsAlwaysVisible(v.Origin.Source)) n++;
        if (n == 0) return default;
        if (n == items.Length) return ViolationList.FromOwnedArray(items);
        var result = new Violation[n];
        n = 0;
        foreach (ref readonly Violation v in items.AsSpan()) if (IsAlwaysVisible(v.Origin.Source)) result[n++] = v;
        return ViolationList.FromOwnedArray(result);
    }

    private static FieldStatus StatusOf(Entry e)
    {
        if (e.Validating) return FieldStatus.Validating;
        if (e.ErrorCount > 0) return FieldStatus.Invalid;
        if (e.Items.Length > 0)
        {
            foreach (ref readonly Violation v in e.Items.AsSpan())
                if (v.Severity == Severity.Warning) return FieldStatus.Warned;
        }
        return e.Validated || e.Items.Length > 0 ? FieldStatus.Valid : FieldStatus.Pristine;
    }

    /// <summary>
    /// Replaces the items of <paramref name="origin"/> in <paramref name="entry"/> with <paramref name="incoming"/>.
    /// Must run under the lock. Returns false — without allocating — when the stored items are already identical.
    /// </summary>
    private bool ReplaceOrigin(Entry entry, ViolationOrigin origin, ReadOnlySpan<Violation> incoming)
    {
        Violation[] old = entry.Items;
        int oldSame = 0;
        foreach (ref readonly Violation v in old.AsSpan()) if (v.Origin == origin) oldSame++;

        if (oldSame == 0 && incoming.IsEmpty) return false;
        if (oldSame == incoming.Length && SameSequence(old, origin, incoming)) return false;

        int total = old.Length - oldSame + incoming.Length;
        var items = total == 0 ? s_noViolations : new Violation[total];
        var errorItems = total == 0 ? s_noItems : new ErrorItem[total];
        int n = 0, errors = 0;
        for (int i = 0; i < old.Length; i++)
        {
            if (old[i].Origin == origin) continue;
            items[n] = old[i];
            errorItems[n++] = entry.ErrorItems[i];
            if (old[i].IsError) errors++;
        }
        foreach (ref readonly Violation raw in incoming)
        {
            Violation v = raw.Origin == origin ? raw : raw.WithOrigin(origin);
            items[n] = v;
            errorItems[n++] = ReuseOrCreate(entry, in v);
            if (v.IsError) errors++;
        }

        _errorCount += errors - entry.ErrorCount;
        entry.ErrorCount = errors;
        entry.Items = items;
        entry.ErrorItems = errorItems;
        return true;
    }

    // Keeps ErrorItem identity for a violation that is re-stored unchanged next to a changed one.
    private ErrorItem ReuseOrCreate(Entry entry, in Violation v)
    {
        for (int i = 0; i < entry.Items.Length; i++)
            if (Same(in entry.Items[i], in v) && entry.Items[i].Origin == v.Origin) return entry.ErrorItems[i];
        return new ErrorItem(v, TypeKey, entry.Path);
    }

    private static bool SameSequence(Violation[] old, ViolationOrigin origin, ReadOnlySpan<Violation> incoming)
    {
        int j = 0;
        for (int i = 0; i < old.Length; i++)
        {
            if (old[i].Origin != origin) continue;
            if (j >= incoming.Length || !Same(in old[i], in incoming[j])) return false;
            j++;
        }
        return true;
    }

    private bool RemoveWhere(Entry entry, Func<Violation, bool> predicate)
    {
        Violation[] old = entry.Items;
        int keep = 0;
        foreach (ref readonly Violation v in old.AsSpan()) if (!predicate(v)) keep++;
        if (keep == old.Length) return false;

        var items = keep == 0 ? s_noViolations : new Violation[keep];
        var errorItems = keep == 0 ? s_noItems : new ErrorItem[keep];
        int n = 0, errors = 0;
        for (int i = 0; i < old.Length; i++)
        {
            if (predicate(old[i])) continue;
            items[n] = old[i];
            errorItems[n++] = entry.ErrorItems[i];
            if (old[i].IsError) errors++;
        }
        _errorCount += errors - entry.ErrorCount;
        entry.ErrorCount = errors;
        entry.Items = items;
        entry.ErrorItems = errorItems;
        return true;
    }

    private static bool Same(in Violation a, in Violation b)
        => a.Code == b.Code && a.Severity == b.Severity && a.Path.Equals(b.Path) && a.Args.Equals(b.Args)
           && string.Equals(a.Detail, b.Detail, StringComparison.Ordinal);

    /// <summary>Bag key of a violation: its concrete path (templates without indices never allocate).</summary>
    private static string KeyOf(in Violation v)
    {
        FieldPath p = v.Path;
        if (p.IsEmpty) return string.Empty;
        return p.Indices.Count == 0 ? p.Template! : p.ToString();
    }

    private void RaiseChanged(List<string>? paths)
    {
        if (paths is null) return;
        var handler = ValidationChanged;
        if (handler is null) return;
        if (paths.Count > 8)
        {
            handler(string.Empty);
            return;
        }
        foreach (string p in paths) handler(p);
    }
}
