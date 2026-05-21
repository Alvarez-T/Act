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

    bool INotifyDataErrorInfo.HasErrors => ValidationIfCreated?.HasErrors ?? false;

    IEnumerable INotifyDataErrorInfo.GetErrors(string? propertyName)
        => ValidationIfCreated?.GetErrors(propertyName) ?? System.Array.Empty<string>();

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

    /// <summary>
    /// Wires the <see cref="YFex.State.Validation.ValidationBag.ValidationChanged"/> event
    /// to <see cref="ErrorsChanged"/>. Called lazily on first access of <see cref="StateObject.Validation"/>
    /// or explicitly if you need INDEI before the first validation fires.
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
