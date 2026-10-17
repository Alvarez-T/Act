using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Authorization.Public;
using static YFex.Rules.Admin.RuleAdmin;

namespace YFex.Rules.Admin;

/// <summary>
/// The permissions of the rule administration module (authorization-consolidation 03 §6). Tables may name their own publish
/// permission (<c>[DecisionTable(PublishPermission = Catalog.Ids.X)]</c>); these are the defaults of <see cref="RuleAdminOptions"/>.
/// </summary>
[PermissionCatalog]
public static partial class RuleAdminPermissions
{
    [Permission("rules.tables.publish", Name = "Publicar tabelas de regras",
        Description = "Publish versions of decision tables that do not name their own publish permission")]
    public static partial Permission TablesPublish { get; }

    [Permission("rules.parameters.publish", Name = "Publicar parâmetros de regras")]
    public static partial Permission ParametersPublish { get; }

    [Permission("rules.dynamic.publish", Name = "Publicar regras dinâmicas")]
    public static partial Permission RulesPublish { get; }

    [Permission("rules.override", Name = "Sobrepor regras", Description = "Disable, enable or replace a rule at a tier")]
    public static partial Permission Override { get; }

    [Permission("rules.view", Name = "Consultar histórico de regras",
        Description = "Read rule data history and definitions (only when RuleAdminOptions.ViewPermission is set)")]
    public static partial Permission View { get; }
}

/// <summary>
/// The gate requirements of <see cref="RuleAdminBaseline"/> (authorization-consolidation 04 §2.5): the permission each message needs
/// comes from <see cref="RuleAdminOptions"/> or from the table itself, so they are DI requirements (registered by
/// <see cref="RuleAdminAuthorization.AddYFexRuleAdminRequirements"/>, client and server alike). A denial names the missing permission.
/// </summary>
public sealed class RuleAdminRequirements(RuleSetRegistry registry, RuleAdminOptions? options = null)
    : IResourcePermissionRequirement<Commands.PublishTable>,
      IResourcePermissionRequirement<Commands.PublishParameters>,
      IResourcePermissionRequirement<Commands.PublishRule>,
      IResourcePermissionRequirement<Commands.SetRuleOverride>,
      IResourcePermissionRequirement<Queries.Simulate>
{
    private readonly RuleAdminOptions _options = options ?? new();
    private PermissionSet? _tables;

    /// <summary>The permission publishing <paramref name="kind"/>/<paramref name="code"/> of <paramref name="ruleSet"/> requires.</summary>
    public Permission PermissionFor(string ruleSet, RuleDataKind kind, string code) => kind switch
    {
        RuleDataKind.Table => registry.TryGet(ruleSet, out var host) && host.TryGetTable(code, out var schema)
                              && schema.PublishPermission is { } own ? own : _options.TablePublishPermission,
        RuleDataKind.Parameters => _options.ParameterPublishPermission,
        RuleDataKind.Rule => _options.RulePublishPermission,
        _ => _options.OverridePermission,
    };

    /// <summary>Every table's publish permission plus the fallback: who could publish some table (visibility).</summary>
    public PermissionSet TablePermissions => _tables ??= BuildTablePermissions();

    Permission? IResourcePermissionRequirement<Commands.PublishTable>.PermissionFor(Commands.PublishTable c)
        => PermissionFor(c.RuleSet, RuleDataKind.Table, c.TableCode);
    PermissionSet IAuthorizationRequirement<Commands.PublishTable>.VisibleWith => TablePermissions;

    Permission? IResourcePermissionRequirement<Commands.PublishParameters>.PermissionFor(Commands.PublishParameters c) => _options.ParameterPublishPermission;
    PermissionSet IAuthorizationRequirement<Commands.PublishParameters>.VisibleWith => PermissionSet.Of(_options.ParameterPublishPermission);

    Permission? IResourcePermissionRequirement<Commands.PublishRule>.PermissionFor(Commands.PublishRule c) => _options.RulePublishPermission;
    PermissionSet IAuthorizationRequirement<Commands.PublishRule>.VisibleWith => PermissionSet.Of(_options.RulePublishPermission);

    Permission? IResourcePermissionRequirement<Commands.SetRuleOverride>.PermissionFor(Commands.SetRuleOverride c) => _options.OverridePermission;
    PermissionSet IAuthorizationRequirement<Commands.SetRuleOverride>.VisibleWith => PermissionSet.Of(_options.OverridePermission);

    // What-if needs the permission of what it simulates.
    Permission? IResourcePermissionRequirement<Queries.Simulate>.PermissionFor(Queries.Simulate q) => PermissionFor(q.RuleSet, q.Kind, q.Code);
    PermissionSet IAuthorizationRequirement<Queries.Simulate>.VisibleWith
        => TablePermissions.Union(PermissionSet.Of(_options.ParameterPublishPermission, _options.RulePublishPermission, _options.OverridePermission));

    /// <summary>
    /// Table permissions that no catalog declares (a typo in an attribute, a catalog assembly never loaded). Checked when the
    /// requirements are created so a misconfiguration fails at startup, like an uncatalogued Cqrs permission (D5).
    /// </summary>
    public IReadOnlyList<string> UndeclaredTablePermissions()
    {
        var missing = new List<string>();
        foreach (var (host, table) in Tables())
            if (table.PublishPermission is { } p && !p.Key.IsDeclared) missing.Add($"{host.Name}/{table.Code}: '{p.Value}'");
        if (!_options.TablePublishPermission.Key.IsDeclared) missing.Add($"RuleAdminOptions.TablePublishPermission: '{_options.TablePublishPermission.Value}'");
        return missing;
    }

    private PermissionSet BuildTablePermissions()
    {
        var builder = new PermissionSet.Builder();
        builder.Add(_options.TablePublishPermission);
        foreach (var (_, table) in Tables())
            if (table.PublishPermission is { } p) builder.Add(p);
        return builder.Build();
    }

    private IEnumerable<(IRuleSetHost Host, TableSchema Table)> Tables()
    {
        foreach (var host in registry.All)
        {
            var entries = host.Catalog.Entries.ToArray();
            foreach (var entry in entries)
                if (entry.IsTable && host.TryGetTable(entry.Code, out var schema)) yield return (host, schema);
        }
    }
}

/// <summary>Reading history and definitions: <see cref="RuleAdminOptions.ViewPermission"/> when set, else any authenticated caller.</summary>
public sealed class RuleAdminViewRequirement(RuleAdminOptions? options = null)
    : IResourcePermissionRequirement<Queries.GetRuleData>,
      IResourcePermissionRequirement<Queries.GetSignedRuleData>,
      IResourcePermissionRequirement<Queries.GetDataVersions>,
      IResourcePermissionRequirement<Queries.GetDataVersion>,
      IResourcePermissionRequirement<Queries.GetRuleDefinition>,
      IResourcePermissionRequirement<Queries.GetDataChanges>
{
    private readonly Permission? _view = (options ?? new()).ViewPermission;
    private PermissionSet Visible => _view is { } view ? PermissionSet.Of(view) : PermissionSet.Empty;

    Permission? IResourcePermissionRequirement<Queries.GetRuleData>.PermissionFor(Queries.GetRuleData q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetRuleData>.VisibleWith => Visible;
    Permission? IResourcePermissionRequirement<Queries.GetSignedRuleData>.PermissionFor(Queries.GetSignedRuleData q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetSignedRuleData>.VisibleWith => Visible;
    Permission? IResourcePermissionRequirement<Queries.GetDataVersions>.PermissionFor(Queries.GetDataVersions q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetDataVersions>.VisibleWith => Visible;
    Permission? IResourcePermissionRequirement<Queries.GetDataVersion>.PermissionFor(Queries.GetDataVersion q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetDataVersion>.VisibleWith => Visible;
    Permission? IResourcePermissionRequirement<Queries.GetRuleDefinition>.PermissionFor(Queries.GetRuleDefinition q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetRuleDefinition>.VisibleWith => Visible;
    Permission? IResourcePermissionRequirement<Queries.GetDataChanges>.PermissionFor(Queries.GetDataChanges q) => _view;
    PermissionSet IAuthorizationRequirement<Queries.GetDataChanges>.VisibleWith => Visible;
}

/// <summary>Registration of the RuleAdmin gate requirements.</summary>
public static class RuleAdminAuthorization
{
    /// <summary>
    /// Registers <see cref="RuleAdminRequirements"/> and <see cref="RuleAdminViewRequirement"/>
    /// (idempotent). Called by <c>AddYFexRulesAdmin</c> (server) and <c>AddYFexRuleSetSync</c> (client); any other process that builds
    /// a registry with <see cref="RuleAdminBaseline"/> calls it too (the registry build fails, naming them, when they are missing).
    /// Fails at first resolution when a table names an undeclared permission.
    /// </summary>
    public static IServiceCollection AddYFexRuleAdminRequirements(this IServiceCollection services)
    {
        services.TryAddSingleton<RuleSetRegistry>();
        services.TryAddSingleton(sp =>
        {
            var requirements = new RuleAdminRequirements(sp.GetRequiredService<RuleSetRegistry>(), sp.GetService<RuleAdminOptions>());
            if (requirements.UndeclaredTablePermissions() is { Count: > 0 } missing)
                throw new InvalidOperationException(
                    "Rule publish permissions are not declared by any permission catalog (declare them with [PermissionCatalog]): "
                    + string.Join("; ", missing));
            return requirements;
        });
        services.TryAddSingleton(sp => new RuleAdminViewRequirement(sp.GetService<RuleAdminOptions>()));
        return services;
    }
}
