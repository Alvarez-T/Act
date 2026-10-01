using YFex.Localization;

namespace YFex.Rules;

/// <summary>Rule codes emitted by the engine itself.</summary>
public static class RuleEngineCodes
{
    /// <summary>Requested value above the limit. Args: {0} limit, {1} requested.</summary>
    public const string LimitMax = "rules.limit.max";
    /// <summary>Requested value below the limit. Args: {0} limit, {1} requested.</summary>
    public const string LimitMin = "rules.limit.min";
    /// <summary>Requested value not among the granted options. Args: {0} option count, {1} requested.</summary>
    public const string LimitOptions = "rules.limit.options";
    /// <summary>Publish: rows overlap (Unique / equal-priority). Arg: {0} other row index.</summary>
    public const string TableOverlap = "rules.table.overlap";
    /// <summary>Publish: row shadowed by an earlier row (First). Arg: {0} shadowing row index.</summary>
    public const string TableUnreachable = "rules.table.unreachable";
    /// <summary>Publish: a <c>Complete</c> table has an input no row matches. Args: {0} the input combination, {1} total gaps.</summary>
    public const string TableGap = "rules.table.gap";
    /// <summary>Publish (warning): a <c>Complete</c> table could not be analysed (detail says why).</summary>
    public const string TableCompleteUnverifiable = "rules.table.complete-unverifiable";
    /// <summary>Publish: override of a sealed rule.</summary>
    public const string Sealed = "rules.sealed";
}

/// <summary>
/// Built-in messages of the engine codes, condition templates (06 §4) and explanation phrases. Invariant = English,
/// <c>pt</c> covers pt-BR. Chain it below the module catalogs:
/// <c>new MessageCatalog(appMessages, SalesMessages.Instance, RuleMessages.Instance, BuiltInMessages.Instance)</c>.
/// </summary>
public sealed class RuleMessages : IMessageSource
{
    public static readonly RuleMessages Instance = new();

    private RuleMessages() { }

    public bool TryGetTemplate(string code, string cultureName, out string template)
    {
        template = cultureName switch
        {
            "" or "en" => code switch
            {
                RuleEngineCodes.LimitMax => "{field} must be at most {0}",
                RuleEngineCodes.LimitMin => "{field} must be at least {0}",
                RuleEngineCodes.LimitOptions => "{field}: {1} is not an allowed option",
                RuleEngineCodes.TableOverlap => "row overlaps row {0}",
                RuleEngineCodes.TableUnreachable => "row is never reached (row {0} matches first)",
                RuleEngineCodes.TableGap => "no row covers {0}",
                RuleEngineCodes.TableCompleteUnverifiable => "the table could not be checked for gaps",
                RuleEngineCodes.Sealed => "sealed rules cannot be overridden",
                "rules.cond.eq" => "requires {field} = {0}",
                "rules.cond.neq" => "requires {field} ≠ {0}",
                "rules.cond.lt" => "requires {field} < {0}",
                "rules.cond.lte" => "requires {field} ≤ {0}",
                "rules.cond.gt" => "requires {field} > {0}",
                "rules.cond.gte" => "requires {field} ≥ {0}",
                "rules.cond.true" => "requires {field}",
                "rules.cond.false" => "requires not {field}",
                "rules.cond.column" => "requires another {field}",
                "rules.cond.expr" => "requires {0}",
                "rules.trace.superseded" => "exclusive with {0}",
                "rules.trace.capped" => "capped from {0} to {1}",
                "rules.trace.overridden" => "disabled by {0}",
                "rules.trace.not-effective" => "not in force on {0}",
                "rules.explain.limit.max" => "{field}: up to {0}",
                "rules.explain.limit.min" => "{field}: at least {0}",
                "rules.explain.limit.range" => "{field}: {0} to {1}",
                _ => "",
            },
            "pt" => code switch
            {
                RuleEngineCodes.LimitMax => "{field} deve ser no máximo {0}",
                RuleEngineCodes.LimitMin => "{field} deve ser no mínimo {0}",
                RuleEngineCodes.LimitOptions => "{field}: {1} não é uma opção permitida",
                RuleEngineCodes.TableOverlap => "linha sobrepõe a linha {0}",
                RuleEngineCodes.TableUnreachable => "linha nunca é alcançada (a linha {0} atende antes)",
                RuleEngineCodes.TableGap => "nenhuma linha cobre {0}",
                RuleEngineCodes.TableCompleteUnverifiable => "não foi possível verificar lacunas na tabela",
                RuleEngineCodes.Sealed => "regras seladas não podem ser alteradas",
                "rules.cond.eq" => "requer {field} = {0}",
                "rules.cond.neq" => "requer {field} ≠ {0}",
                "rules.cond.lt" => "requer {field} < {0}",
                "rules.cond.lte" => "requer {field} ≤ {0}",
                "rules.cond.gt" => "requer {field} > {0}",
                "rules.cond.gte" => "requer {field} ≥ {0}",
                "rules.cond.true" => "requer {field}",
                "rules.cond.false" => "requer que não {field}",
                "rules.cond.column" => "requer outro {field}",
                "rules.cond.expr" => "requer {0}",
                "rules.trace.superseded" => "exclusiva com {0}",
                "rules.trace.capped" => "limitada de {0} para {1}",
                "rules.trace.overridden" => "desativada por {0}",
                "rules.trace.not-effective" => "fora de vigência em {0}",
                "rules.explain.limit.max" => "{field}: até {0}",
                "rules.explain.limit.min" => "{field}: no mínimo {0}",
                "rules.explain.limit.range" => "{field}: de {0} a {1}",
                _ => "",
            },
            _ => "",
        };
        return template.Length > 0;
    }

    public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
    {
        name = "";
        return false;
    }
}
