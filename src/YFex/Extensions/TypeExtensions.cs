using System.Collections.Concurrent;

namespace YFex.Extensions;

public static class TypeExtensions
{
    // 1. THE DICTIONARY CACHE (For runtime reflection scenarios)
    private static readonly ConcurrentDictionary<Type, bool> _validTypeRuntimeCache = new();

    public static bool IsPrimitiveOrValidValueType(this Type type)
    {
        // "static t =>" ensures no memory allocation during cache hits
        return _validTypeRuntimeCache.GetOrAdd(type, static t => DetermineIfValid(t));
    }

    // 2. THE GENERIC WRAPPER (For compile-time generic scenarios)
    // This defers to TypeTraits<T> which is 100x faster than the dictionary.
    public static bool IsPrimitiveOrValidValueType<T>()
        => TypeTraits<T>.IsPrimitiveOrValidValueType;

    // 3. THE CORE LOGIC
    internal static bool DetermineIfValid(Type t)
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
internal static class TypeTraits<T>
{
    public static readonly bool IsPrimitiveOrValidValueType = TypeExtensions.DetermineIfValid(typeof(T));
}