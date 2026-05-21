using System;

namespace YFex.Messaging.Generator;

/// <summary>
/// Represents one [Subscribe&lt;T&gt;] decorated method discovered in a class.
/// Fully equatable for Roslyn incremental pipeline caching.
/// </summary>
internal readonly struct SubscribeMethodModel : IEquatable<SubscribeMethodModel>
{
    /// <summary>Name of the user-written handler method.</summary>
    public string MethodName { get; }

    /// <summary>
    /// Fully-qualified event type (global:: prefix), e.g.
    /// <c>global::Customer.Events.EmailChanged</c>.
    /// </summary>
    public string EventTypeFqn { get; }

    /// <summary>
    /// True when the method returns ValueTask or Task → emit IAsyncEventRecipient.
    /// False → emit IEventRecipient (sync, 'in' parameter).
    /// </summary>
    public bool IsAsync { get; }

    /// <summary>KeepAlive = true → strong ref subscription; default false = weak.</summary>
    public bool KeepAlive { get; }

    /// <summary>
    /// Raw Match string, e.g. "Model.Id" or "Model.OrderId,Model.LineId".
    /// Null when no filter is needed.
    /// </summary>
    public string? FilterBy { get; }

    /// <summary>Property name (from nameof) for target-id filter. Null = no filter.</summary>
    public string? Target { get; }

    /// <summary>Property name (from nameof) for group-id filter. Null = no filter.</summary>
    public string? Group { get; }

    /// <summary>
    /// True when Target or Group is set but the event type lacks <c>[MemoryPackable]</c>.
    /// The emitter fires YFRPC0001 when this is true.
    /// </summary>
    public bool NeedsMemoryPackWarning { get; }

    /// <summary>Debounce delay in ms (0 = disabled).</summary>
    public int DebounceMs { get; }

    /// <summary>Throttle window in ms (0 = disabled).</summary>
    public int ThrottleMs { get; }

    public SubscribeMethodModel(
        string methodName,
        string eventTypeFqn,
        bool isAsync,
        bool keepAlive,
        string? filterBy,
        string? target,
        string? group,
        bool needsMemoryPackWarning,
        int debounceMs,
        int throttleMs)
    {
        MethodName              = methodName;
        EventTypeFqn            = eventTypeFqn;
        IsAsync                 = isAsync;
        KeepAlive               = keepAlive;
        FilterBy                = filterBy;
        Target                  = target;
        Group                   = group;
        NeedsMemoryPackWarning  = needsMemoryPackWarning;
        DebounceMs              = debounceMs;
        ThrottleMs              = throttleMs;
    }

    public bool Equals(SubscribeMethodModel other) =>
        MethodName             == other.MethodName             &&
        EventTypeFqn           == other.EventTypeFqn           &&
        IsAsync                == other.IsAsync                &&
        KeepAlive              == other.KeepAlive              &&
        FilterBy               == other.FilterBy               &&
        Target                 == other.Target                 &&
        Group                  == other.Group                  &&
        NeedsMemoryPackWarning == other.NeedsMemoryPackWarning &&
        DebounceMs             == other.DebounceMs             &&
        ThrottleMs             == other.ThrottleMs;

    public override bool Equals(object? obj) => obj is SubscribeMethodModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (MethodName?.GetHashCode() ?? 0);
            h = h * 31 + (EventTypeFqn?.GetHashCode() ?? 0);
            h = h * 31 + IsAsync.GetHashCode();
            h = h * 31 + KeepAlive.GetHashCode();
            h = h * 31 + (FilterBy?.GetHashCode() ?? 0);
            h = h * 31 + (Target?.GetHashCode() ?? 0);
            h = h * 31 + (Group?.GetHashCode() ?? 0);
            h = h * 31 + NeedsMemoryPackWarning.GetHashCode();
            h = h * 31 + DebounceMs;
            h = h * 31 + ThrottleMs;
            return h;
        }
    }
}
