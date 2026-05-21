namespace YFex.State.Internal;

public static class FeatureSwitches
{
    private static int s_enablePropertyChanging; // 0 = unread, 1 = enabled, -1 = disabled

    /// <summary>
    /// Controls whether <see cref="StateObject.NotifyChanging"/> and
    /// <see cref="Mvvm.MvvmStateObject"/>'s <c>INotifyPropertyChanging</c> bridge fire.
    /// Defaults to enabled. Disable for trimming/AOT via:
    /// <code>
    /// &lt;RuntimeHostConfigurationOption
    ///     Include="YFex.State.EnableINotifyPropertyChangingSupport"
    ///     Value="false" Trim="true" /&gt;
    /// </code>
    /// </summary>
    public static bool EnableINotifyPropertyChangingSupport
    {
        get
        {
            int cached = s_enablePropertyChanging;
            if (cached != 0) return cached > 0;
            bool enabled = !AppContext.TryGetSwitch(
                "YFex.State.EnableINotifyPropertyChangingSupport", out bool v) || v;
            s_enablePropertyChanging = enabled ? 1 : -1;
            return enabled;
        }
    }

    /// <summary>
    /// Resets the cached feature-switch value so the next read re-evaluates <see cref="AppContext"/>.
    /// Intended for testing only.
    /// </summary>
    public static void ResetForTesting() => s_enablePropertyChanging = 0;
}
