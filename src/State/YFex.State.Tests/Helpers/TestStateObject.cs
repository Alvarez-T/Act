using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YFex.State.Internal;
using YFex.State.Notification;

namespace YFex.State.Tests.Helpers;

/// <summary>
/// Hand-written <see cref="StateObject"/> subclass that exposes protected members for direct
/// testing without going through codegen. Each property has a fixed PropertyId so tests can
/// reason about bitmap behaviour.
/// </summary>
public sealed class TestStateObject : StateObject
{
    public static readonly ChangedNotification AlphaDescriptor =
        new() { PropertyName = "Alpha", PropertyId = 0u };
    public static readonly ChangedNotification BetaDescriptor =
        new() { PropertyName = "Beta", PropertyId = 1u };
    public static readonly ChangedNotification GammaDescriptor =
        new() { PropertyName = "Gamma", PropertyId = 2u };

    private int _alpha;
    private string? _beta;
    private double _gamma;
    private global::YFex.State.TaskNotifier? _delta;
    private global::YFex.State.TaskNotifier<int>? _epsilon;

    public int Alpha
    {
        get => _alpha;
        set => SetField(ref _alpha, value, in AlphaDescriptor);
    }

    public string? Beta
    {
        get => _beta;
        set => SetField(ref _beta, value, in BetaDescriptor);
    }

    public double Gamma
    {
        get => _gamma;
        set => SetField(ref _gamma, value, in GammaDescriptor);
    }

    public bool SetAlphaWithComparer(int value, IEqualityComparer<int> comparer) =>
        SetField(ref _alpha, value, comparer, in AlphaDescriptor);

    public bool SetAlphaViaCallback(int value) =>
        SetField(_alpha, value, v => _alpha = v, in AlphaDescriptor);

    public bool SetAlphaViaCallback(int value, IEqualityComparer<int> comparer) =>
        SetField(_alpha, value, comparer, v => _alpha = v, in AlphaDescriptor);

    public bool SetAlphaViaModelRelay<TModel>(TModel model, int value, Action<TModel, int> setter)
        where TModel : class =>
        SetField(_alpha, value, model, setter, in AlphaDescriptor);

    public bool SetAlphaViaModelRelay<TModel>(
        TModel model, int value, IEqualityComparer<int> comparer, Action<TModel, int> setter)
        where TModel : class =>
        SetField(_alpha, value, comparer, model, setter, in AlphaDescriptor);

    public bool SetDelta(Task? task, Action<Task?>? callback = null) =>
        SetFieldAndNotifyOnCompletion(ref _delta, task, in BetaDescriptor, callback);

    public bool SetEpsilon(Task<int>? task, Action<Task<int>?>? callback = null) =>
        SetFieldAndNotifyOnCompletion(ref _epsilon, task, in GammaDescriptor, callback);

    public Task? DeltaTask => _delta;
    public Task<int>? EpsilonTask => _epsilon;

    public void FireDirect(in ChangedNotification descriptor) => FireNotification(in descriptor);
    public void NotifyChangingDirect(in ChangedNotification descriptor) => NotifyChanging(in descriptor);
    public void NotifyChangedDirect(in ChangedNotification descriptor) => NotifyChanged(in descriptor);

    /// <summary>
    /// Hand-written DispatchPending override that maps property IDs back to descriptors
    /// and fires <see cref="StateObject.FireNotification"/>. Codegen does this automatically;
    /// hand-written StateObjects must implement it themselves to receive batch flushes.
    /// </summary>
    protected override void DispatchPending(in PropertyBitmap64 mask)
    {
        var local = mask;
        while (!local.IsEmpty())
        {
            uint id = local.PopLowest();
            switch (id)
            {
                case 0u: FireNotification(in AlphaDescriptor); break;
                case 1u: FireNotification(in BetaDescriptor); break;
                case 2u: FireNotification(in GammaDescriptor); break;
            }
        }
    }
}
