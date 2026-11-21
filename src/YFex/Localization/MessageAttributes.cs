namespace YFex.Localization;

/// <summary>
/// Marks a <c>static partial</c> class that declares message keys. YFex.Localization.Generator implements each
/// <see cref="MessageAttribute"/> property, emits a nested <c>Keys</c> class with the keys as constants (for attribute
/// arguments only) and adds the keys to the assembly's module catalog, whose texts come from the language files
/// (<c>texts.&lt;culture&gt;.json</c>, section <c>messages</c>).
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
/// [property: FieldCode(SaleText.Keys.StockOut)] decimal Quantity   // attribute arguments use the constants
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessagesAttribute : Attribute;

/// <summary>
/// Declares one message key on a <c>static partial</c> property of type <see cref="MessageKey"/> or
/// <c>MessageKey&lt;T0…&gt;</c> (the generic arguments are the template's <c>{0}</c>, <c>{1}</c>… types). The key is explicit and
/// never derived from the property name: it is stored as a violation code and sent on the wire, so renaming the property
/// must not change it.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class MessageAttribute(string key) : Attribute
{
    /// <summary>The key (no white space, no braces), namespaced by the module: <c>"stock.out"</c>.</summary>
    public string Key { get; } = key;
}
