namespace YFex.Security.Cli;

/// <summary>Minimal positional + named argument parser for the security CLI.</summary>
public sealed class ArgMap
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static ArgMap Parse(string[] args, int skip)
    {
        var map = new ArgMap();
        for (int i = skip; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--"))
            {
                string name = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    map._options[name] = args[++i];
                }
                else
                {
                    map._flags.Add(name);
                    map._options[name] = null;
                }
            }
            else
            {
                map._positionals.Add(arg);
            }
        }
        return map;
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string Get(string name, string fallback) => Get(name) ?? fallback;

    public int? GetInt(string name) => int.TryParse(Get(name), out var v) ? v : null;

    public bool Has(string name) => _flags.Contains(name) || _options.ContainsKey(name);

    public DateTimeOffset? GetDate(string name) =>
        DateTimeOffset.TryParse(Get(name), out var dt) ? dt : null;
}
