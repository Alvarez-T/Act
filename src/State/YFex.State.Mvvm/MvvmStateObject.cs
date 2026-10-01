using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using YFex.State.Internal;
using YFex.State.Notification;

namespace YFex.State.Mvvm;

/// <summary>
/// Base class for MVVM view-models. Non-generic: the source generator emits
/// <c>__mvvmArgsCache</c>, <c>__mvvmChangingArgsCache</c>, and corresponding
/// <c>GetPropertyChangedArgs</c>/<c>GetPropertyChangingArgs</c> overrides directly into each
/// concrete partial class, eliminating the <c>MvvmArgsCache&lt;TSelf&gt;</c> pattern that
/// crashed with IndexOutOfRangeException when a subclass added new [Observable] properties
/// (TSelf was locked to the base type, so the array was too short for the subclass IDs).
/// </summary>
public abstract class MvvmStateObject : StateObject,
    INotifyPropertyChanged, INotifyPropertyChanging, INotifyDataErrorInfo, IChangedHandler
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event PropertyChangingEventHandler? PropertyChanging;

    private readonly SynchronizationContext? _syncContext;

    /// <summary>Shared fallback — returned when an ID has no registered cache entry.</summary>
    protected static readonly PropertyChangedEventArgs UnknownPropertyArgs = new(string.Empty);
    protected static readonly PropertyChangingEventArgs UnknownChangingArgs = new(string.Empty);

    // ------------------------------------------------------------------
    // Post-change marshaling (single per-property)
    // ------------------------------------------------------------------
    private static readonly SendOrPostCallback s_postCallback = static state =>
    {
        var (vm, args) = ((MvvmStateObject, PropertyChangedEventArgs))state!;
        vm.PropertyChanged?.Invoke(vm, args);
    };

    // ------------------------------------------------------------------
    // Pre-change marshaling
    // ------------------------------------------------------------------
    private static readonly SendOrPostCallback s_changingPostCallback = static state =>
    {
        var (vm, args) = ((MvvmStateObject, PropertyChangingEventArgs))state!;
        vm.PropertyChanging?.Invoke(vm, args);
    };

    // ------------------------------------------------------------------
    // Coalesced batch-flush marshaling (one Post for the whole batch)
    // ------------------------------------------------------------------
    private List<PropertyChangedEventArgs>? _flushQueue;
    private bool _flushingOffThread;

    private static readonly SendOrPostCallback s_batchPostCallback = static state =>
    {
        var (vm, args) = ((MvvmStateObject, PropertyChangedEventArgs[]))state!;
        var handler = vm.PropertyChanged;
        if (handler is null) return;
        foreach (var a in args) handler(vm, a);
    };

    // ------------------------------------------------------------------
    // INotifyDataErrorInfo
    // ------------------------------------------------------------------
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    // 07 §8: INDEI reflects what the user may SEE (display policy: touched / submitted). Gates and CanSave use
    // Validation.HasErrors, which ignores visibility.
    bool INotifyDataErrorInfo.HasErrors => ValidationIfCreated?.HasVisibleErrors ?? false;

    /// <summary>
    /// Stable <see cref="YFex.State.Validation.ErrorItem"/> objects (created once per stored violation; ToString() =
    /// message). Null/empty name = form-level + every visible violation. Warnings/infos are included — templates
    /// color by <c>ErrorItem.Severity</c>.
    /// </summary>
    IEnumerable INotifyDataErrorInfo.GetErrors(string? propertyName)
    {
        var bag = ValidationIfCreated;
        if (bag is null) return System.Array.Empty<YFex.State.Validation.ErrorItem>();
        return string.IsNullOrEmpty(propertyName) ? bag.GetAllVisibleItems() : bag.GetVisibleItems(propertyName);
    }

    private static readonly SendOrPostCallback s_errorsChangedCallback = static state =>
    {
        var (vm, args) = ((MvvmStateObject, DataErrorsChangedEventArgs))state!;
        vm.ErrorsChanged?.Invoke(vm, args);
    };

    private void OnValidationChanged(string propertyName)
    {
        var args = new DataErrorsChangedEventArgs(propertyName.Length == 0 ? null : propertyName);
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
            ErrorsChanged?.Invoke(this, args);
        else
            _syncContext.Post(s_errorsChangedCallback, (this, args));
    }

    protected MvvmStateObject()
    {
        _syncContext = SynchronizationContext.Current;
        Subscribe(this);
    }

    /// <inheritdoc/>
    protected override void OnValidationCreated(YFex.State.Validation.ValidationBag bag)
    {
        base.OnValidationCreated(bag);
        bag.ValidationChanged += OnValidationChanged;
    }

    /// <summary>
    /// Wires the <see cref="YFex.State.Validation.ValidationBag.ValidationChanged"/> event
    /// to <see cref="ErrorsChanged"/>. Done automatically when <see cref="StateObject.Validation"/>
    /// is first created; calling it again is harmless (idempotent).
    /// </summary>
    protected void EnsureValidationWired()
    {
        Validation.ValidationChanged -= OnValidationChanged;
        Validation.ValidationChanged += OnValidationChanged;
    }

    // ------------------------------------------------------------------
    // IChangedHandler.OnChanging — pre-change, always immediate
    // ------------------------------------------------------------------
    void IChangedHandler.OnChanging(object source, in ChangedNotification n)
    {
        if (!FeatureSwitches.EnableINotifyPropertyChangingSupport) return;

        var args = GetPropertyChangingArgs(n.PropertyId);
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
            PropertyChanging?.Invoke(this, args);
        else
            _syncContext.Post(s_changingPostCallback, (this, args));
    }

    // ------------------------------------------------------------------
    // IChangedHandler.OnChanged — post-change, coalesced when off UI thread
    // ------------------------------------------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnChanged(object source, in ChangedNotification n)
    {
        var args = GetPropertyChangedArgs(n.PropertyId);

        if (_flushingOffThread)
        {
            _flushQueue!.Add(args);
            return;
        }

        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
            PropertyChanged?.Invoke(this, args);
        else
            _syncContext.Post(s_postCallback, (this, args));
    }

    // ------------------------------------------------------------------
    // IChangedHandler.OnBatchFlushStarting / OnBatchFlushCompleted
    // ------------------------------------------------------------------
    void IChangedHandler.OnBatchFlushStarting(object source)
    {
        if (_syncContext is null || _syncContext == SynchronizationContext.Current) return;
        _flushingOffThread = true;
        _flushQueue ??= new List<PropertyChangedEventArgs>(8);
    }

    void IChangedHandler.OnBatchFlushCompleted(object source)
    {
        if (!_flushingOffThread) return;
        _flushingOffThread = false;

        if (_flushQueue is null || _flushQueue.Count == 0) return;

        var snapshot = _flushQueue.ToArray();
        _flushQueue.Clear();
        _syncContext!.Post(s_batchPostCallback, (this, snapshot));
    }

    // ------------------------------------------------------------------
    // Args lookup — overridden by generated code per concrete class
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the <see cref="PropertyChangedEventArgs"/> for the given property ID.
    /// Overridden by generated code in each concrete class. Each override handles only
    /// its own property ID range and delegates to <c>base.GetPropertyChangedArgs(id)</c>
    /// for inherited IDs — forming a chain from the most-derived class up to this base,
    /// which returns <see cref="UnknownPropertyArgs"/> as the terminal fallback.
    /// </summary>
    protected virtual PropertyChangedEventArgs GetPropertyChangedArgs(uint id) => UnknownPropertyArgs;

    /// <summary>
    /// Returns the <see cref="PropertyChangingEventArgs"/> for the given property ID.
    /// Same chain pattern as <see cref="GetPropertyChangedArgs"/>.
    /// </summary>
    protected virtual PropertyChangingEventArgs GetPropertyChangingArgs(uint id) => UnknownChangingArgs;
}
