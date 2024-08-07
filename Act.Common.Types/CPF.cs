namespace Act.Common.Types;

public readonly struct CPF : IEquatable<CPF>, IComparable<CPF>
{
    private readonly byte[] _digits;

    public CPF(string value)
    {
        if (string.IsNullOrEmpty(value))
            ArgumentException.ThrowIfNullOrEmpty("CPF cannot be null or empty");

        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsDigit(c))
            {
                buffer[count++] = c;
            }
        }

        if (count != 11)
            throw new ArgumentException("Invalid CPF format. CPF must be 11 digits.", nameof(value));

        _digits = new byte[11];

        for (int i = 0; i < 11; i++)
        {
            _digits[i] = (byte)(buffer[i] - '0');
        }

        if (!IsValidCPF(_digits))
            throw new ArgumentException("Invalid CPF number");
    }

    public static bool Validate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Remove non-numeric characters
        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsDigit(c))
            {
                buffer[count++] = c;
            }
        }

        if (count != 11)
        {
            return false;
        }

        byte[] digits = new byte[11];
        for (int i = 0; i < 11; i++)
        {
            digits[i] = (byte)(buffer[i] - '0');
        }

        return IsValidCPF(digits);
    }

    private static bool IsValidCPF(byte[] digits)
    {
        // Check if all digits are the same (e.g., "111.111.111-11")
        if (digits.All(d => d == digits[0]))
        {
            return false;
        }

        // Calculate the first check digit
        int sum = 0;
        for (int i = 0; i < 9; i++)
        {
            sum += digits[i] * (10 - i);
        }

        int remainder = (sum * 10) % 11;
        if (remainder == 10) remainder = 0;
        if (remainder != digits[9]) return false;

        // Calculate the second check digit
        sum = 0;
        for (int i = 0; i < 10; i++)
        {
            sum += digits[i] * (11 - i);
        }

        remainder = (sum * 10) % 11;
        if (remainder == 10) remainder = 0;

        return remainder == digits[10];
    }

    public static implicit operator string(CPF cpf) => cpf.ToFormattedString();
    public static explicit operator CPF(string value) => new CPF(value);

    public static bool operator ==(CPF left, CPF right) => left.Equals(right);
    public static bool operator !=(CPF left, CPF right) => !(left == right);
    public override bool Equals(object? obj) => obj is CPF cpf && Equals(cpf);

    public bool Equals(CPF other)
    {
        for (int i = 0; i < 11; i++)
        {
            if (_digits[i] != other._digits[i])
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        // Use a simple hash code calculation for the array of digits
        int hash = 17;
        foreach (byte digit in _digits)
        {
            hash = hash * 31 + digit;
        }
        return hash;
    }

    public int CompareTo(CPF other)
    {
        for (int i = 0; i < 11; i++)
        {
            int comparison = _digits[i].CompareTo(other._digits[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    public override string ToString()
    {
        return new string(_digits.Select(d => (char)(d + '0')).ToArray());
    }

    public string ToFormattedString()
    {
        return $"{_digits[0]}{_digits[1]}{_digits[2]}.{_digits[3]}{_digits[4]}{_digits[5]}.{_digits[6]}{_digits[7]}{_digits[8]}-{_digits[9]}{_digits[10]}";
    }
}