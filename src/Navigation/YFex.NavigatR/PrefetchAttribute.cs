namespace YFex.NavigatR;

/// <summary>
/// Marks a method as a prefetch loader for the source generator.
/// <para>
/// Rules:
/// <list type="bullet">
/// <item>Method must return <c>Task</c> or <c>Task&lt;T&gt;</c></item>
/// <item>Method must accept the same route parameter type as <c>[Route(Parameter = typeof(T))]</c></item>
/// <item>When return type is <c>Task</c> — warms cache only, no parameter injected into <c>OnNavigation</c></item>
/// <item>When return type is <c>Task&lt;T&gt;</c> — result injected as parameter into generated <c>OnNavigation</c></item>
/// </list>
/// </para>
/// <para>
/// The generator renames the original method to <c>{MethodName}Core</c> and produces
/// an intercepting override that returns the prefetched value if available,
/// awaits the in-flight task if still running, or calls the original fresh if no prefetch happened.
/// </para>
/// <para>
/// Multiple <c>[Prefetch]</c> methods on the same ViewModel run in parallel.
/// Each <c>Task&lt;T&gt;</c> return type adds one parameter to the generated <c>OnNavigation</c> partial.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PrefetchAttribute : Attribute { }