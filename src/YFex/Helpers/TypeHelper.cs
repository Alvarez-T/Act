using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YFex.Helpers;

public static class TypeHelper
{
    public static string GetPropertyName<TObject, TProperty>(Expression<Func<TObject, TProperty>> propertyExpression) => propertyExpression.Body switch
    {
        MemberExpression member => member.Member.Name,
        UnaryExpression { NodeType: ExpressionType.Convert, Operand: MemberExpression member } => member.Member.Name,
        _ => throw new ArgumentException("Expression must be a member access expression.", nameof(propertyExpression))
    };

    /// <summary>Copies the public properties of the statically known type <typeparamref name="T"/> into an <see cref="ExpandoObject"/>. Trim-safe.</summary>
    public static ExpandoObject ToExpando<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T obj)
        => ToExpandoCore(typeof(T), obj!);

    /// <summary>Copies the public properties of the runtime type of <paramref name="obj"/> into an <see cref="ExpandoObject"/>.</summary>
    [RequiresUnreferencedCode("Reflects over the runtime type of the instance; its properties may be trimmed. Prefer ToExpando<T>.")]
    public static ExpandoObject ToExpando(object obj)
        => ToExpandoCore(obj.GetType(), obj);

    private static ExpandoObject ToExpandoCore([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type, object obj)
    {
        IDictionary<string, object?> expando = new ExpandoObject();
        foreach (var property in type.GetProperties())
            expando.Add(property.Name, property.GetValue(obj));
        return (ExpandoObject)expando;
    }

    public static bool IsPrimitiveNullOrDefaultValue(object? value)
    {
        if (value is null) return true;

        var isDefault = value switch
        {
            int i => i == 0,
            long l => l == 0L,
            short s => s == 0,
            byte b => b == 0,
            float f => f == 0f,
            double d => d == 0d,
            decimal m => m == 0m,
            bool b => b == false,
            char c => c == '\0',
            string s => string.IsNullOrEmpty(s),
            DateTime dt => dt == default,
            Guid g => g == default,
            TimeSpan ts => ts == default,
            _ => false
        };

        if (isDefault) return true;

        var type = value.GetType();
        if (type is { IsValueType: true, IsPrimitive: false })
            return value.Equals(BoxDefault(type.TypeHandle));

        return false;
    }

    // default(T) of a value type is its all-zero instance. Boxing it from a zeroed buffer needs no
    // constructor reflection (unlike Activator/GetUninitializedObject), so it is trim/AOT-safe.
    private static object? BoxDefault(RuntimeTypeHandle type)
    {
        int size = RuntimeHelpers.SizeOf(type);
        Span<byte> zero = size <= 256 ? stackalloc byte[size] : new byte[size];
        zero.Clear();
        return RuntimeHelpers.Box(ref MemoryMarshal.GetReference(zero), type);
    }
}
