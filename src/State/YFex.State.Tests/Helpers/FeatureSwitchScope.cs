using System;
using YFex.State.Internal;

namespace YFex.State.Tests.Helpers;

/// <summary>
/// Saves and restores an <see cref="AppContext"/> switch around a test, also resetting
/// <see cref="FeatureSwitches"/> cached value so the next read re-evaluates the switch.
/// Use with <c>using var _ = new FeatureSwitchScope(name, value);</c>.
/// </summary>
public sealed class FeatureSwitchScope : IDisposable
{
    private readonly string _name;
    private readonly bool _hadPrevious;
    private readonly bool _previous;

    public FeatureSwitchScope(string switchName, bool value)
    {
        _name = switchName;
        _hadPrevious = AppContext.TryGetSwitch(switchName, out _previous);
        AppContext.SetSwitch(switchName, value);
        FeatureSwitches.ResetForTesting();
    }

    public void Dispose()
    {
        if (_hadPrevious) AppContext.SetSwitch(_name, _previous);
        else
        {
            // No clean way to "remove" a switch — flip it to its built-in default (true here)
            // and reset the cache so the next test reads the default state.
            AppContext.SetSwitch(_name, true);
        }
        FeatureSwitches.ResetForTesting();
    }
}
