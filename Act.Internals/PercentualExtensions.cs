namespace Act.Utils;

public static class PercentualExtensions
{
    public static Percentual AsPercentual(this decimal percentageValue, int decimalPlaces) =>
        new Percentual(percentageValue, decimalPlaces);

    public static Percentual ToPercentual(this decimal fractionValue, int decimalPlaces) =>
        new Percentual(fractionValue * 100m, decimalPlaces);

    public static Percentual ToPercentual(this string s, int? decimalPlaces = null) =>
        Percentual.Parse(s, decimalPlaces);

    public static decimal CalculatePercentage(this Percentual percent, decimal value) =>
        percent.ApplyTo(value);

    public static Percentual PercentageDifferenceFrom(this decimal newValue, decimal originalValue,
        int? decimalPlaces = null)
    {
        if (originalValue == 0)
            throw new DivideByZeroException("Original value cannot be zero when calculating percentage difference");

        decimal difference = newValue - originalValue;
        decimal percentageValue = (difference / originalValue) * 100m;
        int places = decimalPlaces ?? Percentual.DefaultDecimalPlaces;

        return percentageValue.AsPercentual(places);
    }
}