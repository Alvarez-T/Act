namespace Act.Common.Types;

public readonly struct RG : IEquatable<RG>, IComparable<RG>
{
    private readonly char[] _digits;

    public RG(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("RG cannot be null or empty.", nameof(value));

        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[count++] = c;
        }

        if (count < 7 || count > 12)
            throw new ArgumentException("Invalid RG format. RG must be between 7 and 12 alphanumeric characters.", nameof(value));

        _digits = new char[count];
        buffer.Slice(0, count).CopyTo(_digits);

        if (!IsValidRG(_digits))
            throw new ArgumentException("Invalid RG number.", nameof(value));
    }

    private static bool IsValidRG(Span<char> digits)
    {
        // Basic validation: Check length and format
        return digits.Length >= 7 && digits.Length <= 12;
        // Additional state-specific or format checks can be added here if needed
    }

    public static bool Validate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Remove non-alphanumeric characters
        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[count++] = c;
            }
        }

        if (count < 7 || count > 12)
        {
            return false;
        }

        // Use a temporary array for validation
        char[] cleanedValue = new char[count];
        buffer.Slice(0, count).CopyTo(cleanedValue);

        return IsValidRG(cleanedValue);
    }

    public static implicit operator string(RG rg) => new string(rg._digits);

    public static explicit operator RG(string value) => new RG(value);

    public override bool Equals(object obj) 
        => obj is RG rg && Equals(rg);

    public bool Equals(RG other)
        => _digits.AsSpan().SequenceEqual(other._digits.AsSpan());

    public override int GetHashCode() 
        => _digits.Aggregate(17, (hash, c) => hash * 31 + c.GetHashCode());

    public int CompareTo(RG other)
        => string.Compare(new string(_digits), new string(other._digits), StringComparison.OrdinalIgnoreCase);

}