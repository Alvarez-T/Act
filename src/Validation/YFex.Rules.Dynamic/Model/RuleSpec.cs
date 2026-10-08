using System.Collections.Immutable;

namespace YFex.Rules.Dynamic;

/// <summary>Directive families of a dynamic rule's <c>then</c> list.</summary>
public enum DirectiveSpecKind : byte { Deny, Warn, Limit, Grant, Decrease, Increase, Approval }

/// <summary>
/// One <c>then</c> entry (05 §8 DirectiveSpec, values may be expressions — <see cref="Expr"/> from YFex.Expressions):
/// <list type="bullet">
/// <item><c>{"deny":{"path":"Installments","code":"tenant.x.low","args":[expr]}}</c> / <c>warn</c></item>
/// <item><c>{"limit":{"field":"Installments","min":expr,"max":expr,"exceed":"deny|warn|approval","permission":"…"}}</c></item>
/// <item><c>{"grant":{"field":"Payment","options":["Pix","Cash"]}}</c></item>
/// <item><c>{"decrease":{"target":"total|line","percent":expr | "amount":expr,"max":expr}}</c> / <c>increase</c></item>
/// <item><c>{"approval":{"permission":"…","path":"…","code":"…"}}</c></item>
/// </list>
/// </summary>
public sealed record DirectiveSpec(
    DirectiveSpecKind Kind,
    string? Field = null,
    string? Path = null,
    string? Code = null,
    ImmutableArray<Expr> Args = default,
    Expr? Min = null,
    Expr? Max = null,
    Exceed Exceed = Exceed.Deny,
    string? Permission = null,
    ImmutableArray<RawValue> Options = default,
    bool LineTarget = false,
    Expr? Percent = null,
    Expr? Amount = null);

/// <summary>
/// A runtime-authored rule (05 §8). <see cref="When"/> is a YFex.Expressions <see cref="Expr"/>. <see cref="Messages"/>: culture
/// (<c>""</c>, <c>"en"</c>, <c>"pt"</c>…) → message key → template; key <c>"name"</c> is the rule's display name (message key =
/// <see cref="Code"/>), other keys are the codes its deny/warn/approval directives use. Versioned with the rule (10 §4).
/// </summary>
public sealed record RuleSpec(
    string Code,
    string Group,
    int Priority,
    Expr? When,
    ImmutableArray<DirectiveSpec> Then,
    ImmutableDictionary<string, ImmutableDictionary<string, string>> Messages,
    string? Description = null);
