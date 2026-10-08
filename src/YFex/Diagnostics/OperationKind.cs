using System.Diagnostics.CodeAnalysis;

namespace YFex.Diagnostics;

/// <summary>
/// What kind of work an operation is. Drives the default level, span policy, metrics and the viewer icon.
/// <para>
/// Open set: well-known kinds have fixed ids below <see cref="FirstRegisteredId"/>, so per-kind filters are a fixed-size
/// array. Apps and modules add their own with <see cref="Register"/> (<c>OperationKind.Register("fiscal.sefaz")</c>); those
/// ids are process-local, and the <see cref="Name"/> is what leaves the process.
/// </para>
/// </summary>
public readonly record struct OperationKind(ushort Id)
{
    /// <summary>First id handed out by <see cref="Register"/>. Ids below it are reserved for well-known kinds.</summary>
    public const ushort FirstRegisteredId = 64;

    // Messaging / CQRS
    public static readonly OperationKind Command = new(1);
    public static readonly OperationKind Query = new(2);
    public static readonly OperationKind EventPublish = new(3);
    public static readonly OperationKind EventHandle = new(4);

    // Boundaries
    public static readonly OperationKind RpcClient = new(5);
    public static readonly OperationKind RpcServer = new(6);
    public static readonly OperationKind HttpClient = new(7);
    public static readonly OperationKind HttpServer = new(8);

    // UI
    public static readonly OperationKind Navigation = new(9);
    public static readonly OperationKind StateCommand = new(10);
    public static readonly OperationKind UiAction = new(11);

    // Decisions
    public static readonly OperationKind Validation = new(12);
    public static readonly OperationKind RuleEvaluation = new(13);
    public static readonly OperationKind Authorization = new(14);

    // Data
    public static readonly OperationKind Persistence = new(15);
    public static readonly OperationKind Sql = new(16);
    public static readonly OperationKind EventAppend = new(17);
    public static readonly OperationKind EventRead = new(18);
    public static readonly OperationKind Projection = new(19);
    public static readonly OperationKind Cache = new(20);

    // Offline
    public static readonly OperationKind OutboxEnqueue = new(21);
    public static readonly OperationKind OutboxReplay = new(22);
    public static readonly OperationKind Sync = new(23);

    // Long-running
    public static readonly OperationKind Job = new(24);
    public static readonly OperationKind Saga = new(25);
    public static readonly OperationKind SagaStep = new(26);
    public static readonly OperationKind ProcessManager = new(27);
    public static readonly OperationKind Compensation = new(28);

    // Platform
    public static readonly OperationKind Device = new(29);
    public static readonly OperationKind AutomationStep = new(30);
    public static readonly OperationKind CustomLogic = new(31);
    public static readonly OperationKind Resilience = new(32);

    // Records that are not timed work
    /// <summary>A plain message (<c>[Log]</c> methods, <c>ILog</c> calls) without a registered kind.</summary>
    public static readonly OperationKind Message = new(33);

    /// <summary>Work the application started by itself (polling, live refresh, timers): a "system" root (logging 13 §2).</summary>
    public static readonly OperationKind SystemAction = new(34);

    /// <summary>Anything else.</summary>
    public static readonly OperationKind Custom = new(63);

    /// <summary>True for <c>default</c>: "no kind given".</summary>
    public bool IsEmpty => Id == 0;

    /// <summary>True for the built-in kinds (fixed ids).</summary>
    public bool IsWellKnown => Id is > 0 and < FirstRegisteredId;

    /// <summary>Stable name (<c>cqrs.command</c>, <c>fiscal.sefaz</c>). Unknown ids give <c>kind.{id}</c>.</summary>
    public string Name => NameOf(this);

    /// <summary>One more than the highest id in use: the length engines give their per-kind arrays.</summary>
    public static int Count => KindTable.Count;

    /// <summary>Registers (or finds) an app kind by name. Idempotent and thread-safe. Returns an id ≥ 64 for new names.</summary>
    public static OperationKind Register(string name) => new(KindTable.Register(name));

    /// <summary>Finds a well-known or registered kind by its name.</summary>
    public static bool TryFind(string name, out OperationKind kind)
    {
        bool found = KindTable.TryFind(name, out ushort id);
        kind = new(id);
        return found;
    }

    /// <summary>Name of a kind (consumer side; no allocation for known ids).</summary>
    public static string NameOf(OperationKind kind) => KindTable.NameOf(kind.Id);

    public override string ToString() => Name;

    private static class KindTable
    {
        private static readonly Lock s_lock = new();
        private static readonly Dictionary<string, ushort> s_ids = new(StringComparer.Ordinal);
        private static string?[] s_names = CreateWellKnown();
        private static int s_count = FirstRegisteredId;

        public static int Count => Volatile.Read(ref s_count);

        private static string?[] CreateWellKnown()
        {
            string?[] names = new string?[FirstRegisteredId * 2];
            ReadOnlySpan<string> known =
            [
                "", "cqrs.command", "cqrs.query", "event.publish", "event.handle",
                "rpc.client", "rpc.server", "http.client", "http.server",
                "navigation", "state.command", "ui.action",
                "validation", "rules.evaluation", "authorization",
                "persistence", "sql", "event.append", "event.read", "projection", "cache",
                "outbox.enqueue", "outbox.replay", "sync",
                "job", "saga", "saga.step", "process.manager", "compensation",
                "device", "automation.step", "custom.logic", "resilience",
                "log.message", "system.action",
            ];
            for (int i = 1; i < known.Length; i++)
            {
                names[i] = known[i];
                s_ids[known[i]] = (ushort)i;
            }
            names[63] = "custom";
            s_ids["custom"] = 63;
            return names;
        }

        public static ushort Register(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            lock (s_lock)
            {
                if (s_ids.TryGetValue(name, out ushort existing)) return existing;
                int id = s_count;
                if (id > ushort.MaxValue) throw new InvalidOperationException("Too many operation kinds registered.");
                string?[] names = s_names;
                if (id >= names.Length)
                {
                    var grown = new string?[names.Length * 2];
                    names.CopyTo(grown, 0);
                    names = grown;
                }
                names[id] = name;
                s_ids[name] = (ushort)id;
                Volatile.Write(ref s_names, names);
                Volatile.Write(ref s_count, id + 1);
                return (ushort)id;
            }
        }

        public static bool TryFind(string name, out ushort id)
        {
            lock (s_lock) return s_ids.TryGetValue(name, out id);
        }

        public static string NameOf(ushort id)
        {
            string?[] names = Volatile.Read(ref s_names);
            return id < names.Length && names[id] is { Length: > 0 } name ? name : UnknownName(id);
        }

        [SuppressMessage("Performance", "CA1863", Justification = "Cold path: unknown ids only.")]
        private static string UnknownName(ushort id) => "kind." + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
