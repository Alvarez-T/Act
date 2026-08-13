using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using YFex.Extensions;
using YFex.Helpers;

namespace YFex;

public static class Guard
{
    public static void ThrowIfNull<T>([NotNull] T? content, string? message = null)
    {
        if (content is null) throw new NullReferenceException(message);
    }

    public static void ThrowIfAllNull(params object?[] values)
    {
        if (values.All(v => v is null))
            throw new ArgumentNullException(null, "At least one argument must not be null.");
    }

    public static void ThrowIfAllNullOrDefault(params object?[] values)
    {
        if (values.All(v => v is null || TypeHelper.IsPrimitiveNullOrDefaultValue(v)))
            throw new NullReferenceException("At least one argument must not be null or empty.");
    }

    public static void ThrowIfAllNull<T>(T value)
    {
        if (!HasAnyPropertyFilled(value))
            throw new ArgumentNullException(null, "At least one property must not be null.");
    }

    public static void ThrowIfZeroOrLess(long? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value is <= 0) throw new ArgumentException(message ?? $"Parameter {name} must be greater than zero.");
    }

    public static void ThrowIfZeroOrLess(decimal? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value is <= 0) throw new ArgumentException(message ?? $"Parameter {name} must be greater than zero.");
    }

    public static bool IfAllNull(params object?[] values) => values.All(v => v is null);

    public static void ThrowIfNullOrDefault<T>([NotNull] T? content, string? message = null)
    {
        if (content is null || content.Equals(default(T))) throw new NullReferenceException(message);
    }

    public static void ThrowIfNullOrEmpty(params string?[] content)
    {
        if (content.Any(c => c.IsEmpty())) throw new ArgumentNullException();
    }

    public static void ThrowIfDefault<T>(T? value)
    {
        if (value is not null && value.Equals(default(T))) throw new ArgumentException();
    }

    public static void ThrowIfKeyIsMissing(string key, string? message = null)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException(message ?? "Key is missing.");
    }

    public static void MaxLength(int content, int maxLength, string? message = null)
    {
        if (content > maxLength) throw new ArgumentException(message ?? "Field length exceeded.");
    }

    public static void EnsureMaxLength(this string content, int maxLength, string? message = null)
    {
        if (content.Length > maxLength) throw new ArgumentException(message ?? "Field length exceeded.");
    }

    public static void EnsureSameTypeMemberAccessExpression(this LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression.Body is not MemberExpression member)
            throw new ArgumentException("Expression must be a member access.", nameof(expression));

        var returnType = expression.Type.GetGenericArguments().Last();
        if (!returnType.IsValueType)
            throw new ArgumentException("The member must be a value type.");

        var memberType = GetMemberType(member.Member);
        if (memberType != returnType)
            throw new ArgumentException($"Member type '{memberType}' does not match the expression's return type '{returnType}'.", nameof(expression));
    }

    public static bool HasAnyPropertyFilled<T>(T instance)
    {
        if (instance is null) return false;

        return typeof(T).GetProperties().Any(prop => prop.GetValue(instance) switch
        {
            null => false,
            string str => !string.IsNullOrWhiteSpace(str),
            int i => i != 0,
            long l => l != 0,
            DateTime dt => dt != default,
            _ => true
        });
    }

    /// <summary>Validates an object's data annotations, throwing <see cref="ValidationException"/> if invalid.</summary>
    public static void ThrowIfNotValid(this object obj)
        => Validator.ValidateObject(obj, new ValidationContext(obj, null, null), validateAllProperties: true);

    private static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo prop => prop.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new NotSupportedException($"Unsupported member type: {member.GetType().Name}")
    };
}
