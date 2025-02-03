namespace Act.Utils;

public static class EnumExtensions
{
    public static int ToInt(this Enum enumType) => Convert.ToInt32(enumType);
}