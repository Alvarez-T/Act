namespace YFex.Localization;

/// <summary>
/// A field message kept as its template plus the field it is about (⚑FM61), so the screen that shows it can name the field
/// the way that screen does. A form fills <c>{field}</c> with its own label (<c>"Nome do cliente é obrigatório"</c>); without a
/// label, <see cref="object.ToString"/> gives the sentence with the app catalog's field name. YFex.State's <c>ErrorItem</c>
/// implements it; UI controls read it from <c>INotifyDataErrorInfo.GetErrors</c> without referencing State.
/// </summary>
/// <example><code>
/// foreach (object error in source.GetErrors("Name"))
///     text = error is IFieldMessage m &amp;&amp; label is not null ? m.WithField(label) : error.ToString();
/// </code></example>
public interface IFieldMessage
{
    /// <summary>The field path inside its model (<c>"Name"</c>, <c>"Address.City"</c>); empty for a model-level message.</summary>
    string FieldPath { get; }

    /// <summary>The field's title key (<c>"CustomerEditModel.Name"</c>), the same key forms use for labels.</summary>
    string FieldKey { get; }

    /// <summary>The message for the current UI culture with <c>{field}</c> = <paramref name="fieldLabel"/>.</summary>
    string WithField(string fieldLabel);
}

/// <summary>
/// Field keys with a fixed display name. A key made by <see cref="Literal"/> is shown as written: <see cref="IMessageCatalog.FieldName"/>
/// returns the label itself, so a template's <c>{field}</c> becomes that label (⚑FM61: the label a form shows).
/// </summary>
/// <example><code>
/// catalog.Format(violation, culture, FieldKeys.Literal("Nome do cliente"));   // "Nome do cliente é obrigatório"
/// </code></example>
public static class FieldKeys
{
    /// <summary>First character of a literal key.</summary>
    public const char LiteralPrefix = '=';

    /// <summary>A field key whose display name is <paramref name="label"/> itself.</summary>
    public static string Literal(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return string.Concat("=", label);
    }

    /// <summary>True when <paramref name="fieldKey"/> was made by <see cref="Literal"/>; <paramref name="label"/> is its name.</summary>
    public static bool TryGetLiteral(ReadOnlySpan<char> fieldKey, out ReadOnlySpan<char> label)
    {
        if (fieldKey.Length > 0 && fieldKey[0] == LiteralPrefix)
        {
            label = fieldKey[1..];
            return true;
        }
        label = default;
        return false;
    }
}
