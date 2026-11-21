using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace YFex.Localization;

/// <summary>
/// Identity of a text in the message catalogs: a namespaced key (<c>"stock.out"</c>, <c>"sale.boleto-needs-customer"</c>).
/// Declared once on a <see cref="MessagesAttribute"/> class and referenced through the generated property, never typed by
/// hand; its texts live in the module's language files (<c>texts.&lt;culture&gt;.json</c>).
/// <para>
/// A generated key also carries its module and index, so the catalog reads the module's text by array index instead of by
/// key. Equality and hashing use <see cref="Value"/> only: a key parsed from stored text equals the generated one.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [Messages]
/// public static partial class SaleText
/// {
///     [Message("sale.boleto-needs-customer")] public static partial MessageKey BoletoNeedsCustomer { get; }
///     [Message("stock.out")]                  public static partial MessageKey&lt;decimal&gt; StockOut { get; }
/// }
///
/// catalog.Format(SaleText.StockOut, 3m, culture);
/// </code>
/// </example>
public readonly struct MessageKey : IEquatable<MessageKey>, ISpanFormattable, Diagnostics.ILogValue<MessageKey>
{
    private readonly string? _value;
    private readonly int _indexPlusOne;   // 0 = no module, so default(MessageKey) reports -1

    /// <summary>Creates a key from text that arrived as data (database, wire, tenant edit). Throws when malformed.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or contains white space or braces.</exception>
    public MessageKey(string value)
    {
        if (!IsValid(value)) throw new ArgumentException($"'{value}' is not a valid message key.", nameof(value));
        _value = value;
    }

    /// <summary>Generated keys: <paramref name="module"/> answers <paramref name="index"/> without a key lookup.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public MessageKey(string value, IMessageModule module, int index)
        : this(value)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Module = module;
        _indexPlusOne = index + 1;
    }

    /// <summary>The key text; <c>""</c> for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(MessageKey)</c>.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>The generated module that declares this key, or <see langword="null"/> for a key created from text.</summary>
    public IMessageModule? Module { get; }

    /// <summary>Index of the key inside <see cref="Module"/>; <c>-1</c> without a module.</summary>
    public int Index => _indexPlusOne - 1;

    /// <summary>Parses text that arrived as data; false when it is not a valid key.</summary>
    public static bool TryCreate([NotNullWhen(true)] string? value, out MessageKey key)
    {
        if (!IsValid(value))
        {
            key = default;
            return false;
        }
        key = new MessageKey(value);
        return true;
    }

    /// <summary>
    /// Grammar: non-empty, no white space, no <c>{</c> / <c>}</c> (they are template syntax). Everything else is the module's
    /// own naming (<c>"rules.cond.eq"</c>, <c>"sale.stage.items"</c>, <c>"perm.sales.close"</c>).
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
            if (char.IsWhiteSpace(c) || c is '{' or '}') return false;
        return true;
    }

    public bool Equals(MessageKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MessageKey other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public static bool operator ==(MessageKey left, MessageKey right) => left.Equals(right);

    public static bool operator !=(MessageKey left, MessageKey right) => !left.Equals(right);

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (Value.AsSpan().TryCopyTo(destination))
        {
            charsWritten = Value.Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    static void Diagnostics.ILogValue<MessageKey>.Write(in MessageKey value, ref Diagnostics.LogWriter writer, ushort field)
    {
        if (value._value is null) writer.WriteNull(field);
        else writer.Write(field, value._value);
    }
}

/// <summary>A <see cref="MessageKey"/> whose template takes one argument of type <typeparamref name="T0"/> (<c>{0}</c>).</summary>
/// <example><code>[Message("stock.out")] public static partial MessageKey&lt;decimal&gt; StockOut { get; }</code></example>
public readonly struct MessageKey<T0> : IEquatable<MessageKey<T0>>
{
    public MessageKey(MessageKey key) => Key = key;

    /// <summary>The untyped key.</summary>
    public MessageKey Key { get; }

    /// <summary>The key text.</summary>
    public string Value => Key.Value;

    public static implicit operator MessageKey(MessageKey<T0> key) => key.Key;

    public bool Equals(MessageKey<T0> other) => Key.Equals(other.Key);

    public override bool Equals(object? obj) => obj is MessageKey<T0> other && Equals(other);

    public override int GetHashCode() => Key.GetHashCode();

    public static bool operator ==(MessageKey<T0> left, MessageKey<T0> right) => left.Equals(right);

    public static bool operator !=(MessageKey<T0> left, MessageKey<T0> right) => !left.Equals(right);

    public override string ToString() => Key.Value;
}

/// <summary>A <see cref="MessageKey"/> whose template takes two typed arguments (<c>{0}</c>, <c>{1}</c>).</summary>
public readonly struct MessageKey<T0, T1> : IEquatable<MessageKey<T0, T1>>
{
    public MessageKey(MessageKey key) => Key = key;

    /// <summary>The untyped key.</summary>
    public MessageKey Key { get; }

    /// <summary>The key text.</summary>
    public string Value => Key.Value;

    public static implicit operator MessageKey(MessageKey<T0, T1> key) => key.Key;

    public bool Equals(MessageKey<T0, T1> other) => Key.Equals(other.Key);

    public override bool Equals(object? obj) => obj is MessageKey<T0, T1> other && Equals(other);

    public override int GetHashCode() => Key.GetHashCode();

    public static bool operator ==(MessageKey<T0, T1> left, MessageKey<T0, T1> right) => left.Equals(right);

    public static bool operator !=(MessageKey<T0, T1> left, MessageKey<T0, T1> right) => !left.Equals(right);

    public override string ToString() => Key.Value;
}

/// <summary>A <see cref="MessageKey"/> whose template takes three typed arguments (<c>{0}</c>, <c>{1}</c>, <c>{2}</c>).</summary>
public readonly struct MessageKey<T0, T1, T2> : IEquatable<MessageKey<T0, T1, T2>>
{
    public MessageKey(MessageKey key) => Key = key;

    /// <summary>The untyped key.</summary>
    public MessageKey Key { get; }

    /// <summary>The key text.</summary>
    public string Value => Key.Value;

    public static implicit operator MessageKey(MessageKey<T0, T1, T2> key) => key.Key;

    public bool Equals(MessageKey<T0, T1, T2> other) => Key.Equals(other.Key);

    public override bool Equals(object? obj) => obj is MessageKey<T0, T1, T2> other && Equals(other);

    public override int GetHashCode() => Key.GetHashCode();

    public static bool operator ==(MessageKey<T0, T1, T2> left, MessageKey<T0, T1, T2> right) => left.Equals(right);

    public static bool operator !=(MessageKey<T0, T1, T2> left, MessageKey<T0, T1, T2> right) => !left.Equals(right);

    public override string ToString() => Key.Value;
}
