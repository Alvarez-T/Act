using System.Dynamic;
using System.Linq.Expressions;

namespace YFex.Helpers;

public static class TypeHelper
{
    public static string GetPropertyName<TObject, TProperty>(Expression<Func<TObject, TProperty>> propertyExpression) => propertyExpression.Body switch
    {
        MemberExpression member => member.Member.Name,
        UnaryExpression { NodeType: ExpressionType.Convert, Operand: MemberExpression member } => member.Member.Name,
        _ => throw new ArgumentException("Expression must be a member access expression.", nameof(propertyExpression))
    };

    public static ExpandoObject ToExpando(object obj)
    {
        IDictionary<string, object?> expando = new ExpandoObject();
        foreach (var property in obj.GetType().GetProperties())
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
            return value.Equals(Activator.CreateInstance(type));

        return false;
    }
}
