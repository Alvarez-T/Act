namespace YFex.Rules;

/// <summary>
/// On a view model's <c>[Observable] Decision&lt;TS&gt;</c> property (07 §9.3): YFex.Validation.Generator merges the decision's
/// limits/options into <c>IValidationSource.TryGetConstraints</c> for VM properties NAMED like situation (or derived) fields,
/// puts the decision's violations into the bag (source = Rule) and raises <c>ConstraintsChanged</c> when it changes.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class ConstraintsFromAttribute : Attribute;
