using System.Runtime.Serialization;

namespace Act.Utilities;

public static class IntUtilities
{
    public static bool IsBetween(this int value, int min, int max)
        => value >= min && value <= max;

    public static int ToInt(this Enum enumType) => Convert.ToInt32(enumType);
}

public readonly struct Length : IEquatable<Length>
{
    public Length(Range range)
    {
        
    }
    public bool Equals(Length other)
    {
        throw new NotImplementedException();
    }
}
