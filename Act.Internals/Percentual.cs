using System.Globalization;

namespace Act.Utils;

public readonly record struct Percentual
{
    private readonly decimal _percentageValue;
    public int DecimalPlaces { get; }
    public static int DefaultDecimalPlaces { get; set; } = 2;

    public Percentual(decimal percentageValue, int decimalPlaces)
    {
        DecimalPlaces = decimalPlaces;
        _percentageValue = Math.Round(percentageValue, decimalPlaces, MidpointRounding.AwayFromZero);
    }

    public static implicit operator Percentual(decimal value) => new(value, DefaultDecimalPlaces);
    public static implicit operator decimal(Percentual percent) => percent._percentageValue / 100m;

    public static Percentual Parse(string s, int? decimalPlaces = null)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var span = s.AsSpan().Trim();
        bool hasPercentage = !span.IsEmpty && span[^1] == '%';
        var numberSpan = hasPercentage ? span[..^1] : span;

        if (!decimal.TryParse(numberSpan, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("Invalid number format");

        decimal percentageValue = hasPercentage ? value : value * 100m;
        int places = decimalPlaces ?? DefaultDecimalPlaces;

        return new Percentual(percentageValue, places);
    }

    public decimal ApplyTo(decimal value) => value * _percentageValue / 100m;

    public override string ToString() =>
        _percentageValue.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);

    public string ToStringWithSymbol() =>
        ToString() + " %";
}