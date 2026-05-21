using System;

namespace YFex.Messaging.Generator;

/// <summary>
/// Represents one [Live] decorated method in a class.
/// PropertyId range: base + 0 = value, base + 1 = IsLoading, base + 2 = Error.
/// </summary>
internal readonly struct LiveMethodModel : IEquatable<LiveMethodModel>
{
    /// <summary>Name of the user-written method (e.g. "TotalCustomersAsync").</summary>
    public string MethodName { get; }

    /// <summary>Derived property name — "Async" suffix stripped (e.g. "TotalCustomers").</summary>
    public string PropertyName { get; }

    /// <summary>Fully-qualified return type T in Task&lt;T&gt; (e.g. "global::System.Int32").</summary>
    public string ValueTypeFqn { get; }

    /// <summary>
    /// The base PropertyId for this live property. Three consecutive IDs are consumed:
    ///   base+0 = Value, base+1 = IsLoading, base+2 = Error.
    /// Assigned by the emitter starting at 100, incrementing by 3 per method.
    /// </summary>
    public uint BasePropertyId { get; }

    /// <summary>PollMs attribute value (0 = no polling).</summary>
    public int PollMs { get; }

    /// <summary>Cache tier from <c>[Live(Cache = ...)]</c>.</summary>
    public int Cache { get; }

    /// <summary>
    /// True when the value type crosses a process boundary (Cache != Local)
    /// but lacks <c>[MemoryPackable]</c> — diagnostic YFRPC0001 should fire.
    /// </summary>
    public bool NeedsMemoryPackWarning { get; }

    /// <summary>
    /// Explicit dependency property names from <c>[Live(DependsOn = [...])]</c>.
    /// The emitter subscribes to each named property's change notifications and
    /// calls <c>RecomputeAsync</c> when any fires.
    /// </summary>
    public EquatableArray<string> ExplicitDependencies { get; }

    /// <summary>
    /// True when PollMs == 0 and DependsOn is empty — YFLIV0002 should fire.
    /// </summary>
    public bool NeedsRefreshTriggerWarning { get; }

    /// <summary>
    /// Suspend behavior from <c>[Live(SuspendBehavior = ...)]</c>.
    /// Maps to <c>LiveSuspendBehavior</c> enum values:
    ///   0 = PauseAndRefreshOnResume (default),
    ///   1 = StayLive,
    ///   2 = AlwaysRefetchOnResume,
    ///   3 = FreezeOnSuspend.
    /// Only meaningful when the host class inherits PageViewModel.
    /// </summary>
    public int SuspendBehavior { get; }

    public LiveMethodModel(
        string methodName,
        string propertyName,
        string valueTypeFqn,
        uint basePropertyId,
        int pollMs,
        int cache,
        bool needsMemoryPackWarning,
        EquatableArray<string> explicitDependencies,
        bool needsRefreshTriggerWarning,
        int suspendBehavior)
    {
        MethodName                  = methodName;
        PropertyName                = propertyName;
        ValueTypeFqn                = valueTypeFqn;
        BasePropertyId              = basePropertyId;
        PollMs                      = pollMs;
        Cache                       = cache;
        NeedsMemoryPackWarning      = needsMemoryPackWarning;
        ExplicitDependencies        = explicitDependencies;
        NeedsRefreshTriggerWarning  = needsRefreshTriggerWarning;
        SuspendBehavior             = suspendBehavior;
    }

    public bool Equals(LiveMethodModel other) =>
        MethodName                  == other.MethodName                  &&
        PropertyName                == other.PropertyName                &&
        ValueTypeFqn                == other.ValueTypeFqn                &&
        BasePropertyId              == other.BasePropertyId              &&
        PollMs                      == other.PollMs                      &&
        Cache                       == other.Cache                       &&
        NeedsMemoryPackWarning      == other.NeedsMemoryPackWarning      &&
        ExplicitDependencies.Equals(other.ExplicitDependencies)          &&
        NeedsRefreshTriggerWarning  == other.NeedsRefreshTriggerWarning  &&
        SuspendBehavior             == other.SuspendBehavior;

    public override bool Equals(object? obj) => obj is LiveMethodModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (MethodName?.GetHashCode()   ?? 0);
            h = h * 31 + (PropertyName?.GetHashCode() ?? 0);
            h = h * 31 + (ValueTypeFqn?.GetHashCode() ?? 0);
            h = h * 31 + (int)BasePropertyId;
            h = h * 31 + PollMs;
            h = h * 31 + Cache;
            h = h * 31 + NeedsMemoryPackWarning.GetHashCode();
            h = h * 31 + ExplicitDependencies.GetHashCode();
            h = h * 31 + NeedsRefreshTriggerWarning.GetHashCode();
            h = h * 31 + SuspendBehavior;
            return h;
        }
    }
}
