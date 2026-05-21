namespace YFex.Extensions;

public static class TaskExtensions
{
    public static ValueTask<T> ToValueTask<T>(this Task<T> task) => new ValueTask<T>(task);
}
