using System;

namespace YFex.Persistence.Generator;

/// <summary>
/// One [Observable, Persist] property discovered in a class.
/// The generator emits typed MemoryPack serialization for each entry.
/// </summary>
internal readonly struct PersistencePropertyModel : IEquatable<PersistencePropertyModel>
{
    /// <summary>Property name (C# identifier).</summary>
    public string Name { get; }

    /// <summary>Fully-qualified property type (global:: prefix).</summary>
    public string TypeFqn { get; }

    /// <summary>True when the property type has [MemoryPackable] or is a known primitive.</summary>
    public bool IsKnownSerializable { get; }

    /// <summary>Optional custom key from [Persist(Key = "...")] — null uses property name.</summary>
    public string? CustomKey { get; }

    public PersistencePropertyModel(string name, string typeFqn, bool isKnownSerializable, string? customKey)
    {
        Name                = name;
        TypeFqn             = typeFqn;
        IsKnownSerializable = isKnownSerializable;
        CustomKey           = customKey;
    }

    public bool Equals(PersistencePropertyModel other) =>
        Name                == other.Name                &&
        TypeFqn             == other.TypeFqn             &&
        IsKnownSerializable == other.IsKnownSerializable &&
        CustomKey           == other.CustomKey;

    public override bool Equals(object? obj) => obj is PersistencePropertyModel m && Equals(m);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (Name?.GetHashCode() ?? 0);
            h = h * 31 + (TypeFqn?.GetHashCode() ?? 0);
            h = h * 31 + IsKnownSerializable.GetHashCode();
            h = h * 31 + (CustomKey?.GetHashCode() ?? 0);
            return h;
        }
    }
}
