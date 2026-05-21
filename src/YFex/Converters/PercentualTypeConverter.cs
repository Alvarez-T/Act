using Act.Utils;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace YFex.Converters;

public sealed class PercentualTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string)
        || sourceType == typeof(decimal)
        || sourceType == typeof(double)
        || sourceType == typeof(float)
        || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
        => destinationType == typeof(string)
        || destinationType == typeof(decimal)
        || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value switch
        {
            string s => Percentual.Parse(s, culture),
            decimal d => Percentual.FromPercentage(d),
            double dbl => Percentual.FromPercentage((decimal)dbl),
            float f => Percentual.FromPercentage((decimal)f),
            _ => base.ConvertFrom(context, culture, value)
        };

    public override object? ConvertTo(
        ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is Percentual p)
        {
            if (destinationType == typeof(string)) return p.ToStringWithSymbol();
            if (destinationType == typeof(decimal)) return p.ToPercentageValue();
        }
        return base.ConvertTo(context, culture, value, destinationType);
    }
}
