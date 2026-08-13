namespace YFex.Extensions;

public static class MathExtensions
{
    //
    // Summary:
    //     AreClose - Returns whether or not two doubles are "close". That is, whether or
    //     not they are within epsilon of each other.
    //
    // Parameters:
    //   value1:
    //     The first double to compare.
    //
    //   value2:
    //     The second double to compare.
    public static bool AreClose(double value1, double value2)
    {
        if (value1 == value2)
        {
            return true;
        }

        double num = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * 2.2204460492503131E-16;
        double num2 = value1 - value2;
        if (0.0 - num < num2)
        {
            return num > num2;
        }

        return false;
    }
}
