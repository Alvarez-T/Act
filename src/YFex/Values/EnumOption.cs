namespace YFex;

/// <summary>
/// One enum member offered by a schema (editors show <see cref="Name"/>; wire/storage use <see cref="Code"/>). Shared by
/// rule situations and tables (YFex.Rules), expression schemas (YFex.Expressions) and editors/pickers (Composer).
/// </summary>
/// <param name="Value">The underlying enum value.</param>
/// <param name="Code">Stable code (<c>[EnumCode]</c> when declared, else the member name) — what JSON, storage and literals use.</param>
/// <param name="Name">Display name.</param>
public readonly record struct EnumOption(long Value, string Code, string Name);
