namespace YFex.UI.Abstractions;


/// <summary>User can proceed or cancel.</summary>
public readonly union OkCancel(OkCancel.Ok, OkCancel.Cancel)
{
    public sealed class Ok;
    public sealed class Cancel;
}

/// <summary>User can proceed, cancel, or notification times out.</summary>
public readonly union OkCancelIgnored(OkCancelIgnored.Ok, OkCancelIgnored.Cancel, OkCancelIgnored.Ignored)
{
    public sealed class Ok;
    public sealed class Cancel;
    public sealed class Ignored;
}

/// <summary>User can confirm or deny.</summary>
public readonly union YesNo(YesNo.Yes, YesNo.No)
{
    public sealed class Yes;
    public sealed class No;
}

/// <summary>User can interact or notification times out.</summary>
public readonly union YesNoIgnored(YesNoIgnored.Yes, YesNoIgnored.No, YesNoIgnored.Ignored)
{
    public sealed class Yes;
    public sealed class No;
    public sealed class Ignored;
}

/// <summary>User can confirm, deny, or dismiss.</summary>
public readonly union YesNoCancel(YesNoCancel.Yes, YesNoCancel.No, YesNoCancel.Cancel)
{
    public sealed class Yes;
    public sealed class No;
    public sealed class Cancel;
}

public readonly union YesNoCancelIgnored(YesNoCancelIgnored.Yes, YesNoCancelIgnored.No, YesNoCancelIgnored.Cancel, YesNoCancelIgnored.Ignored)
{
    public sealed class Yes;
    public sealed class No;
    public sealed class Cancel;
    public sealed class Ignored;
}

public enum MessageIcon
{
    None,
    Success,
    Info,
    Error,
    Warning,
    Question
}