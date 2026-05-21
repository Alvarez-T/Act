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

    public bool HasAnySubscriptions => !Methods.IsEmpty;

    public SubscribeClassModel(
        string @namespace,
        string className,
        EquatableArray<SubscribeMethodModel> methods)
    {
        Namespace = @namespace;
        ClassName = className;
        Methods   = methods;
    }

    public bool Equals(SubscribeClassModel other) =>
        Namespace == other.Namespace &&
        ClassName == other.ClassName &&
        Methods   == other.Methods;

    public override bool Equals(object? obj) => obj is SubscribeClassModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (Namespace?.GetHashCode() ?? 0);
            h = h * 31 + (ClassName?.GetHashCode() ?? 0);
            h = h * 31 + Methods.GetHashCode();
            return h;
        }
    }
}
