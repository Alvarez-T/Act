using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace YFex.Extensions;

public static class TypeExtensions
{
    // The ctor(string) probe needs the constructors kept by the trimmer, hence the annotations below.
    private const DynamicallyAccessedMemberTypes Ctors = DynamicallyAccessedMemberTypes.PublicConstructors;

    // 1. THE DICTIONARY CACHE (For runtime reflection scenarios)
    private static readonly ConcurrentDictionary<Type, bool> _validTypeRuntimeCache = new();

    public static bool IsPrimitiveOrValidValueType([DynamicallyAccessedMembers(Ctors)] this Type type)
    {
        // Not GetOrAdd(type, factory): the factory's parameter would drop the trim annotation.
        if (_validTypeRuntimeCache.TryGetValue(type, out var valid)) return valid;
        valid = DetermineIfValid(type);
        _validTypeRuntimeCache.TryAdd(type, valid);
        return valid;
    }

    // 2. THE GENERIC WRAPPER (For compile-time generic scenarios)
    // This defers to TypeTraits<T> which is 100x faster than the dictionary.
    public static bool IsPrimitiveOrValidValueType<[DynamicallyAccessedMembers(Ctors)] T>()
        => TypeTraits<T>.IsPrimitiveOrValidValueType;

    // 3. THE CORE LOGIC
    internal static bool DetermineIfValid([DynamicallyAccessedMembers(Ctors)] Type t)
    {
        var underlyingType = Nullable.GetUnderlyingType(t) ?? t;

        if (underlyingType.IsPrimitive || underlyingType.IsEnum) return true;

        if (underlyingType == typeof(string) ||
            underlyingType == typeof(decimal) ||
            underlyingType == typeof(DateTime) ||
            underlyingType == typeof(DateTimeOffset) ||
            underlyingType == typeof(Guid) ||
            underlyingType == typeof(TimeSpan) ||
            underlyingType == typeof(DateOnly) ||
            underlyingType == typeof(TimeOnly))
        {
            return true;
        }

        // We can safely allocate the Type[] here because this logic runs 
        // exactly ONCE per type and is cached forever.
        if (underlyingType.IsValueType)
        {
            return underlyingType.GetConstructor([typeof(string)]) != null;
        }

        return false;
    }
}

// 4. THE GENERIC STATIC CACHE (Internal engine)
internal static class TypeTraits<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>
{
    public static readonly bool IsPrimitiveOrValidValueType = TypeExtensions.DetermineIfValid(typeof(T));
}