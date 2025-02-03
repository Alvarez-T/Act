namespace Act.Utils;

public static class IntExtensions
{
    public static bool IsBetween(this int value, int min, int max)
        => value >= min && value <= max;

    
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
