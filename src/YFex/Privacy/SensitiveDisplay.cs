namespace YFex.Privacy;

/// <summary>
/// Where a <c>[Sensitive]</c> value is shown (forms ⚑FM63). The same value reads differently by place: a list or an export
/// shows many people's data at once, one record is looked at on purpose, a log is read by people who should not see it.
/// </summary>
public enum DisplayContext : byte
{
    /// <summary>One record shown on purpose (a details screen, a form).</summary>
    Record = 0,

    /// <summary>Many records at once (a grid, a list, a search result).</summary>
    List = 1,

    /// <summary>Data leaving the app (a file, a report, a copy to another system).</summary>
    Export = 2,

    /// <summary>Logs, traces and messages to AI models (YFex.Privacy, YFex.Logging): always masked.</summary>
    Log = 3,
}

/// <summary>How a <c>[Sensitive]</c> value shows in one <see cref="DisplayContext"/> (forms ⚑FM63).</summary>
public enum SensitiveDisplay : byte
{
    /// <summary>The value's own default for the place (<see cref="SensitiveDisplays.Default"/>).</summary>
    Default = 0,

    /// <summary>Its display mask (<c>***.456.789-**</c>); a permitted user may reveal it where the screen offers that.</summary>
    Masked = 1,

    /// <summary>In clear.</summary>
    Clear = 2,

    /// <summary>Not shown at all (<c>******</c>).</summary>
    Hidden = 3,
}

/// <summary>
/// The defaults and the rules every layer follows (forms ⚑FM63, changed 2026-11-27 to opt-in; ⚑FM69). On screen a
/// <c>[Sensitive]</c> value shows <b>in clear by default</b> (<see cref="DisplayContext.Record"/>, <see cref="DisplayContext.List"/>,
/// <see cref="DisplayContext.Export"/>); an app, a value type, a model or a field turns masking on (<see cref="Masking"/> holds
/// the usual masked set). Two rules stay whatever is set, because they are YFex.Privacy's protection and not a screen choice:
/// logs (and traces, AI messages) are never clearer than masked, and secrets are always hidden.
/// </summary>
public static class SensitiveDisplays
{
    /// <summary>The default for a value of <paramref name="sensitivity"/> shown in <paramref name="context"/>: clear on screen,
    /// masked in logs, hidden for secrets.</summary>
    public static SensitiveDisplay Default(DataSensitivity sensitivity, DisplayContext context)
    {
        if (sensitivity < DataSensitivity.Personal) return SensitiveDisplay.Clear;
        if (sensitivity == DataSensitivity.Secret) return SensitiveDisplay.Hidden;
        return context == DisplayContext.Log ? SensitiveDisplay.Masked : SensitiveDisplay.Clear;
    }

    /// <summary>
    /// The masked set an app opts into (forms <c>MaskSensitive()</c>): lists and exports masked, one record masked (a screen may
    /// offer a reveal to permitted users), logs masked, secrets hidden. Values below <see cref="DataSensitivity.Personal"/>
    /// stay clear.
    /// </summary>
    public static SensitiveDisplay Masking(DataSensitivity sensitivity, DisplayContext context)
    {
        if (sensitivity < DataSensitivity.Personal) return SensitiveDisplay.Clear;
        return sensitivity == DataSensitivity.Secret ? SensitiveDisplay.Hidden : SensitiveDisplay.Masked;
    }

    /// <summary>
    /// What is shown when <paramref name="wanted"/> is asked for a value of <paramref name="sensitivity"/> in
    /// <paramref name="context"/>: <see cref="SensitiveDisplay.Default"/> resolves to <see cref="Default"/>; a log is never
    /// clearer than masked and a secret is always hidden.
    /// </summary>
    public static SensitiveDisplay Resolve(SensitiveDisplay wanted, DataSensitivity sensitivity, DisplayContext context)
    {
        if (sensitivity == DataSensitivity.Secret) return SensitiveDisplay.Hidden;
        var shown = wanted == SensitiveDisplay.Default ? Default(sensitivity, context) : wanted;
        return context == DisplayContext.Log && shown == SensitiveDisplay.Clear && sensitivity >= DataSensitivity.Personal
            ? SensitiveDisplay.Masked
            : shown;
    }

    /// <summary>The stricter of two choices (<c>Hidden</c> &gt; <c>Masked</c> &gt; <c>Clear</c>); <c>Default</c> yields to the other.
    /// Later configuration layers combine with this: they may hide or mask more, never show more.</summary>
    public static SensitiveDisplay Stricter(SensitiveDisplay a, SensitiveDisplay b)
    {
        if (a == SensitiveDisplay.Default) return b;
        if (b == SensitiveDisplay.Default) return a;
        return Rank(a) >= Rank(b) ? a : b;

        static int Rank(SensitiveDisplay d) => d switch { SensitiveDisplay.Hidden => 3, SensitiveDisplay.Masked => 2, _ => 1 };
    }
}
