using System.Collections.Generic;
using System.Runtime.CompilerServices;
using YFex.State.Internal;
using YFex.State.Notification;

namespace YFex.State;

/// <summary>
/// Non-generic base class for all observable state objects. CRTP removed: the source generator
/// emits all type-specific helpers (MVVM args cache, DispatchPending override) directly into each
/// concrete partial class, so TSelf is not needed for correctness or type safety.
/// </summary>
public abstract class StateObject : INotifyChanged, IActivatable
{
    internal InlineHandlerList _handlers;
    internal PropertyBitmap64 _pendingMask;
    internal int _updateDepth;
#pragma warning disable CS0649 // set by generated DispatchPending overrides during reentrant notification
    internal int _reentryDepth;
#pragma warning restore CS0649

    private bool _isActive;
    private YFex.State.Validation.ValidationBag? _validation;

#if DEBUG
    private readonly int _ownerThreadId = System.Environment.CurrentManagedThreadId;
#endif

    // -----------------------------------------------------------------------
    // IActivatable
    // -----------------------------------------------------------------------

    public bool IsActive => _isActive;

    /// <summary>
    /// Activates this object. Cascades to child <see cref="StateObject"/> properties (emitted by
    /// generator for each <c>[Observable]</c> property whose type inherits <see cref="StateObject"/>)
    /// and fires the <c>OnActivated</c> partial hook.
    /// </summary>
    public void Activate()
    {
        if (_isActive) return;
        AssertOwnerThread();
        _isActive = true;
        OnActivateCascading();
        OnActivated();
    }

    /// <summary>
    /// Deactivates this object. Children are deactivated before the parent, then
    /// <c>OnDeactivated</c> fires.
    /// </summary>
    public void Deactivate()
    {
        if (!_isActive) return;
        AssertOwnerThread();
        OnDeactivateCascading();
        _isActive = false;
        OnDeactivated();
    }

    /// <summary>
    /// Overridden by generated code to forward <see cref="Activate"/> to child
    /// <see cref="StateObject"/> properties. Each override calls <c>base.OnActivateCascading()</c>
    /// so the entire hierarchy is covered.
    /// </summary>
    protected virtual void OnActivateCascading() { }

    /// <summary>
    /// Overridden by generated code to forward <see cref="Deactivate"/> to child
    /// <see cref="StateObject"/> properties (children deactivate before the parent).
    /// </summary>
    protected virtual void OnDeactivateCascading() { }

    /// <summary>User hook — fires after this object transitions to active.</summary>
    protected virtual void OnActivated() { }

    /// <summary>User hook — fires after this object transitions to inactive.</summary>
    protected virtual void OnDeactivated() { }

    /// <summary>
    /// Lazy-initialized validation bag. Created on first access so objects without validation
    /// incur zero overhead. Generated <c>Validate_X</c> helpers access this to store results.
    /// </summary>
    public YFex.State.Validation.ValidationBag Validation
        => _validation ??= new YFex.State.Validation.ValidationBag();

    /// <summary>
    /// Returns the validation bag if already created, or null. Allows INDEI.HasErrors to
    /// avoid allocating the bag just to return false.
    /// </summary>
    protected YFex.State.Validation.ValidationBag? ValidationIfCreated => _validation;

    public void Subscribe(IChangedHandler handler) => _handlers.Add(handler);
    public void Unsubscribe(IChangedHandler handler) => _handlers.Remove(handler);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnterUpdate() => _updateDepth++;

    internal void ExitUpdate()
    {
        if (--_updateDepth > 0) return;
        DrainPending();
    }

    /// <summary>
    /// Fires a pre-change notification to all handlers. Always immediate — never deferred by
    /// <see cref="BeginUpdate"/>. No-op when
    /// <c>YFex.State.EnableINotifyPropertyChangingSupport</c> is disabled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void NotifyChanging(in ChangedNotification descriptor)
    {
        if (!FeatureSwitches.EnableINotifyPropertyChangingSupport) return;
        AssertOwnerThread();
        _handlers.NotifyChangingAll(this, in descriptor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void NotifyChanged(in ChangedNotification descriptor)
    {
        AssertOwnerThread();
        if (_updateDepth > 0)
        {
            _pendingMask.Set(descriptor.PropertyId);
            return;
        }
        _handlers.NotifyAll(this, in descriptor);
    }

    private void DrainPending()
    {
        _handlers.NotifyBatchFlushStarting(this);
        while (!_pendingMask.IsEmpty())
        {
            var snapshot = _pendingMask;
            _pendingMask.Clear();
            DispatchPending(in snapshot);
        }
        _handlers.NotifyBatchFlushCompleted(this);
    }

    /// <summary>
    /// Overridden by generated code to translate pending bit positions back to descriptors and fire
    /// notifications. Each generated override handles only its own class's property IDs and then
    /// calls <c>base.DispatchPending(mask)</c> so parent-class IDs are handled up the chain.
    /// Base implementation is a no-op for hand-written classes that don't use the generator.
    /// </summary>
    protected virtual void DispatchPending(in PropertyBitmap64 mask) { }

    /// <summary>
    /// Fires a notification directly to all handlers, bypassing the update-depth check.
    /// Called from generated <see cref="DispatchPending"/> overrides which run only after
    /// <see cref="ExitUpdate"/> has confirmed we are outside any batch scope.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void FireNotification(in ChangedNotification descriptor) =>
        _handlers.NotifyAll(this, in descriptor);

    // -----------------------------------------------------------------------
    // SetField — ref-field overloads
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compares <paramref name="field"/> to <paramref name="value"/> using the default equality
    /// comparer. If different, fires pre-change, updates the field, fires post-change.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool SetField<T>(ref T field, T value, in ChangedNotification descriptor)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        NotifyChanging(in descriptor);
        field = value;
        NotifyChanged(in descriptor);
        return true;
    }

    /// <summary>
    /// Compares using a custom <paramref name="comparer"/>. If different, fires pre-change,
    /// updates the field, fires post-change.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected bool SetField<T>(ref T field, T value, IEqualityComparer<T> comparer, in ChangedNotification descriptor)
    {
        if (comparer.Equals(field, value)) return false;
        NotifyChanging(in descriptor);
        field = value;
        NotifyChanged(in descriptor);
        return true;
    }

    // -----------------------------------------------------------------------
    // SetField — callback overloads (non-field-backed / model relay)
    // -----------------------------------------------------------------------

    /// <summary>
    /// For properties without a direct backing field. Compares old and new values; if different,
    /// fires pre-change, invokes <paramref name="setter"/> to store the value, fires post-change.
    /// </summary>
    protected bool SetField<T>(T oldValue, T newValue, Action<T> setter, in ChangedNotification descriptor)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return false;
        NotifyChanging(in descriptor);
        setter(newValue);
        NotifyChanged(in descriptor);
        return true;
    }

    /// <inheritdoc cref="SetField{T}(T,T,Action{T},in ChangedNotification)"/>
    protected bool SetField<T>(T oldValue, T newValue, IEqualityComparer<T> comparer, Action<T> setter, in ChangedNotification descriptor)
    {
        if (comparer.Equals(oldValue, newValue)) return false;
        NotifyChanging(in descriptor);
        setter(newValue);
        NotifyChanged(in descriptor);
        return true;
    }

    /// <summary>
    /// Model-relay variant. Passes <paramref name="model"/> to the stateless <paramref name="setter"/>
    /// callback so the C# compiler can cache the delegate and avoid per-call allocation.
    /// </summary>
    protected bool SetField<TModel, T>(T oldValue, T newValue, TModel model, Action<TModel, T> setter, in ChangedNotification descriptor)
        where TModel : class
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return false;
        NotifyChanging(in descriptor);
        setter(model, newValue);
        NotifyChanged(in descriptor);
        return true;
    }

    /// <inheritdoc cref="SetField{TModel,T}(T,T,TModel,Action{TModel,T},in ChangedNotification)"/>
    protected bool SetField<TModel, T>(T oldValue, T newValue, IEqualityComparer<T> comparer, TModel model, Action<TModel, T> setter, in ChangedNotification descriptor)
        where TModel : class
    {
        if (comparer.Equals(oldValue, newValue)) return false;
        NotifyChanging(in descriptor);
        setter(model, newValue);
        NotifyChanged(in descriptor);
        return true;
    }

    // -----------------------------------------------------------------------
    // SetFieldAndNotifyOnCompletion — Task / Task<T>
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets a <see cref="Task"/>-typed property backed by a <see cref="TaskNotifier"/> and
    /// re-fires <see cref="NotifyChanged"/> when the task completes (if still the current task).
    /// Pre-change fires once on assignment; post-change fires on assignment and again on completion.
    /// </summary>
    protected bool SetFieldAndNotifyOnCompletion(
        ref TaskNotifier? notifier,
        Task? newValue,
        in ChangedNotification descriptor,
        Action<Task?>? callback = null)
    {
        return SetFieldAndNotifyOnCompletion(
            notifier ??= new TaskNotifier(), newValue, callback, in descriptor);
    }

    /// <summary>
    /// Sets a <see cref="Task{T}"/>-typed property backed by a <see cref="TaskNotifier{T}"/> and
    /// re-fires <see cref="NotifyChanged"/> when the task completes.
    /// </summary>
    protected bool SetFieldAndNotifyOnCompletion<T>(
        ref TaskNotifier<T>? notifier,
        Task<T>? newValue,
        in ChangedNotification descriptor,
        Action<Task<T>?>? callback = null)
    {
        return SetFieldAndNotifyOnCompletion(
            notifier ??= new TaskNotifier<T>(), newValue, callback, in descriptor);
    }

    private bool SetFieldAndNotifyOnCompletion<TTask>(
        ITaskNotifier<TTask> notifier,
        TTask? newValue,
        Action<TTask?>? callback,
        in ChangedNotification descriptor)
        where TTask : Task
    {
        if (ReferenceEquals(notifier.Task, newValue)) return false;

        bool isAlreadyCompletedOrNull = newValue?.IsCompleted ?? true;

        NotifyChanging(in descriptor);
        notifier.Task = newValue;
        NotifyChanged(in descriptor);

        if (isAlreadyCompletedOrNull)
        {
            callback?.Invoke(newValue);
            return true;
        }

        // Capture locals for the async void — avoids capturing 'this' descriptor by ref.
        var capturedDescriptor = descriptor;
        async void MonitorTask()
        {
            await newValue!.GetAwaitableWithoutEndValidation();

            if (ReferenceEquals(notifier.Task, newValue))
                NotifyChanged(in capturedDescriptor);

            callback?.Invoke(newValue);
        }

        MonitorTask();
        return true;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOwnerThread()
    {
#if DEBUG
        if (System.Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new System.InvalidOperationException(
                $"StateObject setter called from thread {System.Environment.CurrentManagedThreadId} " +
                $"but was constructed on thread {_ownerThreadId}. " +
                "Cross-thread mutation is a programming error.");
#endif
    }

    public BatchScope BeginUpdate() => new(this);
}
