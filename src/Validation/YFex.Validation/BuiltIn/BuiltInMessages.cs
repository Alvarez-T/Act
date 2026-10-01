using YFex.Localization;

namespace YFex.Validation.BuiltIn;

/// <summary>
/// Messages of the built-in rule codes, lowest layer of every catalog. Invariant (<c>""</c>) = English;
/// <c>pt</c> covers pt-BR through the catalog's culture fallback. Static switch — no resources, AOT-safe.
/// <code>new MessageCatalog(appMessages, SalesMessages.Instance, BuiltInMessages.Instance)</code>
/// </summary>
public sealed class BuiltInMessages : IMessageSource
{
    public static readonly BuiltInMessages Instance = new();

    private BuiltInMessages() { }

    public bool TryGetTemplate(string code, string cultureName, out string template)
    {
        switch (cultureName)
        {
            case "":
            case "en":
                template = code switch
                {
                    RuleCodes.Required => "{field} is required",
                    RuleCodes.NotEmpty => "{field} must not be empty",
                    RuleCodes.Length => "{field} must have between {0} and {1} characters",
                    RuleCodes.LengthMin => "{field} must have at least {0} characters",
                    RuleCodes.LengthMax => "{field} must have at most {0} characters",
                    RuleCodes.Range => "{field} must be between {0} and {1}",
                    RuleCodes.Pattern => "{field} has an invalid format",
                    RuleCodes.Email => "{field} is not a valid e-mail address",
                    "digits" => "{field} must contain only digits",
                    "alnum" => "{field} must contain only letters and digits",
                    _ => "",
                };
                break;
            case "pt":
                template = code switch
                {
                    RuleCodes.Required => "{field} é obrigatório",
                    RuleCodes.NotEmpty => "{field} não pode ficar vazio",
                    RuleCodes.Length => "{field} deve ter entre {0} e {1} caracteres",
                    RuleCodes.LengthMin => "{field} deve ter no mínimo {0} caracteres",
                    RuleCodes.LengthMax => "{field} deve ter no máximo {0} caracteres",
                    RuleCodes.Range => "{field} deve estar entre {0} e {1}",
                    RuleCodes.Pattern => "{field} está em formato inválido",
                    RuleCodes.Email => "{field} não é um e-mail válido",
                    "digits" => "{field} deve conter apenas dígitos",
                    "alnum" => "{field} deve conter apenas letras e dígitos",
                    _ => "",
                };
                break;
            default:
                template = "";
                break;
        }
        return template.Length > 0;
    }

    public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
    {
        name = "";
        return false;
    }
}
