using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace YFex.Extensions;

public static class StringExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> when the string is null, empty, or
    /// whitespace-only — a fluent alias for
    /// <see cref="string.IsNullOrWhiteSpace"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty([NotNullWhen(false)] this string? content)
        => string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(content);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNotEmpty([NotNullWhen(true)] this string? content)
        => !IsEmpty(content);

    public static DateTime? ToDateTime(this string? content)
    => DateTime.TryParse(content, out var date) ? date : null;

    public static DateTime ParseDateTime(this string content)
        => DateTime.Parse(content);


    public static StringBuilder AppendIfNotNull<T>(this StringBuilder builder, T? obj, string content)
    {
        if (obj is not null)
        {
            builder.Append(content);
        }

        return builder;
    }

    public static StringBuilder AppendLineIfNotNull<T>(this StringBuilder builder, T? obj, string content)
    {
        if (obj is not null)
        {
            builder.AppendLine(content);
        }

        return builder;
    }
}
