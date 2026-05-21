namespace YFex.NavigatR;

public enum PrefetchPolicy
{
    /// <summary>
    /// Cancel the previous in-flight prefetch when a new one starts for a different route.
    /// Same route + same params → reuse existing. Default.
    /// </summary>
    CancelPrevious,

    /// <summary>
    /// Allow multiple prefetches to run simultaneously.
    /// </summary>
    AllowMultiple
}