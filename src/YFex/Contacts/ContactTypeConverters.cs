using System.ComponentModel;
using System.Globalization;

namespace YFex;

/// <summary>XAML string conversion for canonical email addresses.</summary>
public sealed class EmailAddressTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? EmailAddress.Parse(s, culture) : base.ConvertFrom(context, culture, value);
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is EmailAddress address
            ? address.Value : base.ConvertTo(context, culture, value, destinationType);
}

/// <summary>XAML string conversion for international E.164 phone numbers.</summary>
public sealed class PhoneNumberTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? PhoneNumber.Parse(s, culture) : base.ConvertFrom(context, culture, value);
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is PhoneNumber number
            ? number.E164 : base.ConvertTo(context, culture, value, destinationType);
}
