using System.Globalization;
using YFex.Localization;
using YFex.Validation;

namespace YFex.State.Validation;

/// <summary>
/// Reference-type wrapper created ONCE when a violation is stored (not per <c>GetErrors</c> call), so INDEI
/// consumers (Avalonia <c>DataValidationErrors</c>) get stable objects without repeated boxing (07 §2).
/// <see cref="ToString"/> returns the formatted message; templates can also bind <see cref="Severity"/>.
/// </summary>
public sealed class ErrorItem
{
    private readonly string _typeKey;
    private readonly string _path;
    private string? _message;
    private string? _messageCulture;

    internal ErrorItem(in Violation violation, string typeKey, string path)
    {
        Violation = violation;
        _typeKey = typeKey;
        _path = path;
    }

    public Violation Violation { get; }

    public Severity Severity => Violation.Severity;

    /// <summary>
    /// Message resolved through <see cref="ValidationMessages.Catalog"/> for the current UI culture — formatted
    /// once per culture and cached. Field display-name key: <c>"&lt;TypeName&gt;.&lt;Path&gt;"</c> (10 §5).
    /// </summary>
    public string Message
    {
        get
        {
            CultureInfo culture = CultureInfo.CurrentUICulture;
            if (_message is { } m && string.Equals(_messageCulture, culture.Name, StringComparison.Ordinal)) return m;
            string? fieldKey = _path.Length == 0 ? null
                : _typeKey.Length == 0 ? _path : string.Concat(_typeKey, ".", _path);
            m = ValidationMessages.Catalog.Format(Violation, culture, fieldKey);
            _messageCulture = culture.Name;
            _message = m;
            return m;
        }
    }

    public override string ToString() => Message;
}

/// <summary>Message catalog used by <see cref="ErrorItem.Message"/> (compose app/module/tenant layers at startup, 10).</summary>
public static class ValidationMessages
{
    private static IMessageCatalog s_catalog = new MessageCatalog(YFex.Validation.BuiltIn.BuiltInMessages.Instance);

    /// <summary>Default: built-in messages only. Replace with <c>new MessageCatalog(app, module…, BuiltInMessages.Instance)</c>.</summary>
    public static IMessageCatalog Catalog
    {
        get => s_catalog;
        set => s_catalog = value ?? throw new ArgumentNullException(nameof(value));
    }
}
