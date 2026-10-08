using YFex.Authorization.Public;

namespace YFex.Rules.Tests;

/// <summary>Permissions the test rule sets name (authorization-consolidation ⚑Z2: typed in code, <c>Ids</c> in attributes).</summary>
[PermissionCatalog]
public static partial class RulesTestPermissions
{
    [Permission("sales.discount.override", Name = "Aprovar desconto acima do limite")]
    public static partial Permission DiscountOverride { get; }

    [Permission("sales.rules.installments.publish", Name = "Publicar tabela de parcelas")]
    public static partial Permission InstallmentsPublish { get; }

    [Permission("sales.rules.manual-discount.publish", Name = "Publicar tabela de desconto manual")]
    public static partial Permission ManualDiscountPublish { get; }

    [Permission("freight.heavy", Name = "Aprovar carga pesada")]
    public static partial Permission FreightHeavy { get; }
}
