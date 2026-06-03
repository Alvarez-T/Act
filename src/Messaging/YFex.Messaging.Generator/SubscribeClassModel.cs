using System;

namespace YFex.Messaging.Generator;

/// <summary>
/// Represents one class that contains [Subscribe&lt;T&gt;] decorated methods.
/// One source file is emitted per SubscribeClassModel.
/// </summary>
internal readonly struct SubscribeClassModel : IEquatable<SubscribeClassModel>
{
    public string Namespace { get; }
    public string ClassName { get; }
    public EquatableArray<SubscribeMethodModel> Methods { get; }

    /// <summary>
    /// True when the class inherits <c>MessagingHost</c>. The emitter generates
    /// <c>OnHostStarting</c> instead of <c>OnActivateCascading</c>/<c>OnDeactivateCascading</c>,
    /// and uses <c>RegisterSubscription</c> instead of per-instance token fields.
    /// False means the class inherits <c>StateObject</c> (or one of its subclasses).
    /// </summary>
    public bool IsMessagingHost { get; }

    /// <summary>False → emitter reports YFSUB001 (class is not partial).</summary>
    public bool IsPartial { get; }

    /// <summary>False → emitter reports YFSUB002 (class doesn't inherit a supported base).</summary>
    public bool ValidBase { get; }

    public bool HasAnySubscriptions => !Methods.IsEmpty;

    public SubscribeClassModel(
        string @namespace,
        string className,
        EquatableArray<SubscribeMethodModel> methods,
        bool isMessagingHost = false,
        bool isPartial = true,
        bool validBase = true)
    {
        Namespace       = @namespace;
        ClassName       = className;
        Methods         = methods;
        IsMessagingHost = isMessagingHost;
        IsPartial       = isPartial;
        ValidBase       = validBase;
    }

    public bool Equals(SubscribeClassModel other) =>
        Namespace       == other.Namespace       &&
        ClassName       == other.ClassName       &&
        Methods         == other.Methods         &&
        IsMessagingHost == other.IsMessagingHost &&
        IsPartial       == other.IsPartial       &&
        ValidBase       == other.ValidBase;

    public override bool Equals(object? obj) => obj is SubscribeClassModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (Namespace?.GetHashCode() ?? 0);
            h = h * 31 + (ClassName?.GetHashCode() ?? 0);
            h = h * 31 + Methods.GetHashCode();
            h = h * 31 + IsMessagingHost.GetHashCode();
            h = h * 31 + IsPartial.GetHashCode();
            h = h * 31 + ValidBase.GetHashCode();
            return h;
        }
    }
}
