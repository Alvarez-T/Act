using System;

namespace YFex.State
{
    /// <summary>
    /// Marks a partial property or private field as an observable state property.
    /// The generator emits a type-specific equality check, partial
    /// OnXxxChanging/OnXxxChanged hooks, and a cached ChangedNotification descriptor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ObservableAttribute : Attribute { }

    /// <summary>
    /// Marks an expression-bodied partial property as a computed derived value.
    /// The generator walks the expression body, identifies [Observable] dependencies,
    /// and wires up OnXxxChanged partial methods to re-notify when any dependency changes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ComputedAttribute : Attribute
    {
        /// <summary>
        /// Explicit dependency list. Required when the expression body contains method
        /// calls that the analyser cannot resolve to known [Observable] properties.
        /// </summary>
        public string[]? DependsOn { get; init; }
    }

    /// <summary>
    /// Attaches a custom equality comparer type to an [Observable] property.
    /// The comparer must expose a static Default or Instance member.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class EqualityComparerAttribute : Attribute
    {
        public EqualityComparerAttribute(Type comparerType) => ComparerType = comparerType;
        public Type ComparerType { get; }
    }

    /// <summary>
    /// When applied to an [Observable] property of type <see cref="System.Threading.Tasks.Task"/> or
    /// <see cref="System.Threading.Tasks.Task{TResult}"/>, the source generator emits a
    /// <see cref="TaskNotifier"/>/<see cref="TaskNotifier{T}"/> backing field and wires the setter
    /// through <c>SetFieldAndNotifyOnCompletion</c> so that <c>PropertyChanged</c> fires a second time
    /// when the task completes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class NotifyOnTaskCompletionAttribute : Attribute { }

    /// <summary>
    /// Prevents the source generator from including this [Observable] property in the
    /// activation cascade. By default, [Observable] properties whose type inherits from
    /// <see cref="StateObject"/> are automatically activated/deactivated with their parent.
    /// Apply this attribute to opt the property out of that behavior.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class IgnoreActivationAttribute : Attribute { }

    /// <summary>
    /// Marks a method as the body of an auto-implemented command.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class StateCommandAttribute : Attribute
    {
        /// <summary>
        /// When true, the generator emits a companion <c>CancelXxxCommand</c> alongside the
        /// primary command. Requires the method to have a <see cref="System.Threading.CancellationToken"/>
        /// parameter. The cancel command's <c>CanExecute</c> returns true only while the primary
        /// command is executing, and its <c>Execute</c> cancels the primary command's token.
        /// </summary>
        public bool IncludeCancelCommand { get; init; }

        /// <summary>
        /// Overrides the generated name for the cancel command.
        /// Defaults to <c>Cancel{MethodName}Command</c>.
        /// </summary>
        public string? CancelCommandName { get; init; }

        /// <summary>
        /// When set, the generator automatically assigns the async result value to the named
        /// <c>[Observable]</c> property on the ViewModel after successful completion.
        /// <para>
        /// For <c>ValueTask&lt;T&gt;</c> methods this also enables the <b>zero-flicker fast path</b>:
        /// if the ValueTask completes synchronously (e.g., the data was already cached), the result is
        /// assigned without the command ever transitioning through the <c>Executing</c> state,
        /// preventing unnecessary loading-spinner flicker.
        /// </para>
        /// <para>
        /// Only valid when the method returns <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.
        /// Use <c>nameof(YourProperty)</c> to get a compile-time–checked string.
        /// </para>
        /// </summary>
        public string? TargetProperty { get; init; }
    }

    /// <summary>Marks a property or field as trackable for IsModified / MarkAsClean().</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class TrackableAttribute : Attribute { }

    /// <summary>Marks a property or field as part of a named snapshot group.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SnapshotAttribute : Attribute
    {
        public string? Group { get; init; }
    }

    /// <summary>Persists the property value to an IPersistenceStore.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class PersistAttribute : Attribute
    {
        public string? Key { get; init; }
    }

    /// <summary>Subscribes a method to changes on named [Observable] properties.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ReactsToAttribute : Attribute
    {
        public ReactsToAttribute(params string[] propertyNames) => PropertyNames = propertyNames;
        public string[] PropertyNames { get; }
        public bool RunOnMainThread { get; init; }
        public bool CancelPrevious { get; init; }
    }

    /// <summary>Debounces the method call by the specified milliseconds.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class DebounceAttribute : Attribute
    {
        public DebounceAttribute(int milliseconds) => Milliseconds = milliseconds;
        public int Milliseconds { get; }
    }

    /// <summary>Throttles the method call to at most once per interval.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ThrottleAttribute : Attribute
    {
        public ThrottleAttribute(int milliseconds) => Milliseconds = milliseconds;
        public int Milliseconds { get; }
    }

    /// <summary>Polls the method on a recurring interval.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class PollAttribute : Attribute
    {
        public PollAttribute(int intervalMs) => IntervalMs = intervalMs;
        public int IntervalMs { get; }

        /// <summary>
        /// When true, the poll timer pauses automatically when the owner <see cref="StateObject"/>
        /// is deactivated and resumes when it is activated again.
        /// </summary>
        public bool ActiveOnly { get; init; }
    }

    /// <summary>Sets a bool property to true while the command is executing.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class BusyAttribute : Attribute
    {
        public string? PropertyName { get; init; }
    }

    /// <summary>Applies an [N-of-N required] gate before allowing the command to execute.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RequiresAllAttribute : Attribute
    {
        public RequiresAllAttribute(params string[] propertyNames) => PropertyNames = propertyNames;
        public string[] PropertyNames { get; }
    }

    /// <summary>Captures async command exceptions into a named property.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ErrorBucketAttribute : Attribute
    {
        public string? PropertyName { get; init; }
    }

    /// <summary>Enqueues async command invocations into a named or per-command bounded channel.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class QueueAttribute : Attribute
    {
        public string? Name { get; init; }
        public int Capacity { get; init; } = 64;
    }

    /// <summary>Imposes a minimum delay between successive command executions.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CooldownAttribute : Attribute
    {
        public CooldownAttribute(int milliseconds) => Milliseconds = milliseconds;
        public int Milliseconds { get; }
    }

    /// <summary>Emits boxing-free structured log output via [LoggerMessage].</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class LogChangesAttribute : Attribute { }

    /// <summary>Subscribes the owner to item-level change notifications for a StateList property.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ObserveItemsAttribute : Attribute
    {
        public bool Weak { get; init; }

        /// <summary>
        /// When true, item-level observation is detached when the owner is deactivated
        /// and re-attached when activated, preventing background processing for off-screen items.
        /// </summary>
        public bool ActiveOnly { get; init; }
    }

    /// <summary>Propagates change notifications from a child StateObject property.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class PropagateAttribute : Attribute { }

    /// <summary>Increments an int counter whenever this property changes.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class EpochAttribute : Attribute { }

    /// <summary>Validates the property using a synchronous validator.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
    public sealed class ValidateWithAttribute : Attribute
    {
        public ValidateWithAttribute(Type validatorType) => ValidatorType = validatorType;
        public Type ValidatorType { get; }
    }

    /// <summary>Validates the property using an asynchronous validator.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
    public sealed class ValidateAsyncAttribute : Attribute
    {
        public ValidateAsyncAttribute(Type validatorType) => ValidatorType = validatorType;
        public Type ValidatorType { get; }
    }

    /// <summary>Loads the property value asynchronously on first access or initialization.</summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class LoadOnInitAttribute : Attribute { }

    /// <summary>Resets the property to a default value via a generated Reset() method.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ResetToAttribute : Attribute
    {
        public ResetToAttribute(object? defaultValue = null) => DefaultValue = defaultValue;
        public object? DefaultValue { get; }
    }

    /// <summary>Gates the command on a boolean condition property.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class GateAttribute : Attribute
    {
        public GateAttribute(string propertyName) => PropertyName = propertyName;
        public string PropertyName { get; }
    }
}
