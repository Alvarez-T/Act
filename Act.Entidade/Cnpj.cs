using System.Runtime.CompilerServices;
using System.Text;

namespace Act.Entidade;

public readonly struct Cnpj : IEquatable<Cnpj>, IComparable<Cnpj>
{
    private readonly char[] _chars;

    public Cnpj(string value)
    {
        if (string.IsNullOrEmpty(value))
            ArgumentException.ThrowIfNullOrEmpty("CNPJ cannot be null or empty");

        Span<char> buffer = stackalloc char[value.Length];
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[count++] = char.ToUpper(c);
            }
        }

        if (count != 14)
            throw new ArgumentException("Invalid CNPJ format. CNPJ must be 14 characters.", nameof(value));

        _chars = new char[14];

        for (int i = 0; i < 14; i++)
        {
            _chars[i] = buffer[i];
        }

        if (!IsValidCNPJ(_chars))
            throw new ArgumentException("Invalid CNPJ number");
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
                buffer[count++] = char.ToUpper(c);
            }
        }

        if (count != 14)
        {
            return false;
        }

        char[] chars = new char[14];
        for (int i = 0; i < 14; i++)
        {
            chars[i] = buffer[i];
        }

        return IsValidCNPJ(chars);
    }

    private static bool IsValidCNPJ(char[] chars)
    {
        // Check if all characters are the same (e.g., "11.111.111/1111-11")
        if (chars.All(c => c == chars[0]))
        {
            return false;
        }

        // Calculate the first check digit
        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            sum += (chars[i] - '0') * (i % 8 + 2);
        }

        int remainder = sum % 11;
        int firstCheckDigit = remainder < 2 ? 0 : 11 - remainder;
        if (firstCheckDigit != (chars[12] - '0'))
        {
            return false;
        }

        // Calculate the second check digit
        sum = 0;
        for (int i = 0; i < 13; i++)
        {
            sum += (chars[i] - '0') * ((i + 1) % 9);
        }

        remainder = sum % 11;
        int secondCheckDigit = remainder < 2 ? 0 : 11 - remainder;
        return secondCheckDigit == (chars[13] - '0');
    }

    public static implicit operator string(Cnpj cnpj) => cnpj.ToFormattedString();
    public static explicit operator Cnpj(string value) => new Cnpj(value);

    public static bool operator ==(Cnpj left, Cnpj right) => left.Equals(right);
    public static bool operator !=(Cnpj left, Cnpj right) => !(left == right);
    public override bool Equals(object? obj) => obj is Cnpj cnpj && Equals(cnpj);

    public bool Equals(Cnpj other)
    {
        for (int i = 0; i < 14; i++)
        {
            if (_chars[i] != other._chars[i])
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        // Use a simple hash code calculation for the array of characters
        int hash = 17;
        foreach (char c in _chars)
        {
            hash = hash * 31 + c;
        }
        return hash;
    }

    public int CompareTo(Cnpj other)
    {
        for (int i = 0; i < 14; i++)
        {
            int comparison = _chars[i].CompareTo(other._chars[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    public override string ToString()
    {
        return new string(_chars);
    }

    public string ToFormattedString()
    {
        return $"{_chars[0]}{_chars[1]}.{_chars[2]}{_chars[3]}{_chars[4]}.{_chars[5]}{_chars[6]}{_chars[7]}/{_chars[8]}{_chars[9]}{_chars[10]}{_chars[11]}-{_chars[12]}{_chars[13]}";
    }
}