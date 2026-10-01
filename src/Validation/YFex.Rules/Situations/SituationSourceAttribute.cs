namespace YFex.Rules;

/// <summary>
/// Generates <c>public TSituation BuildSituation()</c> on a view model (D31, 07 §9.2). For each situation field, in order:
/// <list type="number">
/// <item>a user method <c>__Situation_&lt;Field&gt;()</c> when the VM declares one (non-partial, or <c>private partial</c> — then the
///   generator emits the declaration part);</item>
/// <item>else the VM member with the same name (implicit conversion required; mismatch = YFR0031);</item>
/// <item>else the generator declares <c>private partial T __Situation_&lt;Field&gt;();</c> — the VM must implement it
///   (the compiler lists every missing one, so a new situation field is never silently ignored).</item>
/// </list>
/// Lines: a same-named collection of element VMs that themselves carry <c>[SituationSource&lt;TLine&gt;]</c>.
/// <code>
/// [SituationSource&lt;SaleSituation&gt;]
/// public partial class SaleVm : MvvmStateObject
/// {
///     [Observable] public partial PaymentMethod Payment { get; set; }          // matched by name
///     private partial TenantId __Situation_Tenant() => session.Tenant;         // context the VM doesn't hold
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SituationSourceAttribute<TSituation> : Attribute where TSituation : ISituation<TSituation>;
