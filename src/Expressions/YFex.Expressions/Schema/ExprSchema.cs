using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using YFex.Helpers;

namespace YFex.Expressions;

/// <summary>
/// A field an expression can read: <c>{"var": Name}</c> at the top level, <c>{"line": Name}</c> inside an item of the default
/// collection. Ids are dense inside their schema (<c>Fields[i].Id == i</c>) — programs read by id through <see cref="IFieldReader"/>.
/// </summary>
public sealed class ExprField(ushort id, string name, RawType type, bool exposed = true, ExprEnumDomain? enumDomain = null)
{
    public ushort Id { get; } = id;
    public string Name { get; } = name;
    public RawType Type { get; } = type;
    /// <summary>False → reading it is a binder error (e.g. situation fields not exposed to runtime rules).</summary>
    public bool Exposed { get; } = exposed;
    /// <summary>Options of an enum field (<see cref="RawType.EnumCode"/>); null otherwise.</summary>
    public ExprEnumDomain? EnumDomain { get; } = enumDomain;

    /// <summary>The static type expressions see for this field.</summary>
    public ExprType ExprType => new(Type, Type.Storage == ValueKind.EnumCode ? EnumDomain : null);

    public override string ToString() => Name;
}

/// <summary>A named list of items (situation lines, a screen grid). Items have their own schema; <see cref="Id"/> is what
/// <see cref="IFieldReader.Count"/>/<see cref="IFieldReader.ReadItem"/> receive.</summary>
public sealed class ExprCollection(ushort id, string name, ExprSchema items)
{
    public ushort Id { get; } = id;
    public string Name { get; } = name;
    public ExprSchema Items { get; } = items;

    public override string ToString() => Name;
}

/// <summary>
/// Runtime description of what expressions may read — built once and immutable (Rules: from a situation schema; Composer: from
/// a screen's data shape at plan compile). <see cref="Hash"/> belongs in every compile-cache key.
/// </summary>
public sealed class ExprSchema
{
    private readonly ExprField[] _fields;
    private readonly ExprCollection[] _collections;
    private readonly FrozenDictionary<string, ExprField> _byName;
    private readonly FrozenDictionary<string, ExprField>.AlternateLookup<ReadOnlySpan<char>> _byNameSpan;
    private readonly FrozenDictionary<string, ExprCollection> _collectionsByName;

    /// <param name="name">Schema name (diagnostics).</param>
    /// <param name="fields">Top-level fields; ids must be dense (<c>fields[i].Id == i</c>).</param>
    /// <param name="collections">Item collections (ids are host-defined; names unique).</param>
    /// <param name="defaultCollection">Index into <paramref name="collections"/> that <c>{"line": …}</c> and collection-less
    /// aggregates use; −1 = none.</param>
    /// <param name="noun">What the schema describes, for messages (<c>"situation"</c>, <c>"screen"</c>).</param>
    public ExprSchema(string name, ExprField[] fields, ExprCollection[]? collections = null, int defaultCollection = -1, string noun = "schema")
    {
        Name = name;
        Noun = noun;
        _fields = fields;
        _collections = collections ?? [];
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Id != i) throw new ArgumentException($"Field ids must be dense: '{fields[i].Name}' has id {fields[i].Id}, expected {i}.", nameof(fields));
        if (defaultCollection >= _collections.Length) throw new ArgumentOutOfRangeException(nameof(defaultCollection));
        Default = defaultCollection < 0 ? null : _collections[defaultCollection];

        var byName = new Dictionary<string, ExprField>(StringComparer.Ordinal);
        foreach (var f in fields) byName.Add(f.Name, f);
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _byNameSpan = _byName.GetAlternateLookup<ReadOnlySpan<char>>();
        var byCollection = new Dictionary<string, ExprCollection>(StringComparer.Ordinal);
        foreach (var c in _collections) byCollection.Add(c.Name, c);
        _collectionsByName = byCollection.ToFrozenDictionary(StringComparer.Ordinal);

        var h = StableHash64.Create(0x5C4E);
        Append(ref h, this);
        Hash = h.Finish();
    }

    public string Name { get; }
    public string Noun { get; }
    public ReadOnlySpan<ExprField> Fields => _fields;
    public ReadOnlySpan<ExprCollection> Collections => _collections;
    /// <summary>The collection <c>{"line": …}</c> and collection-less aggregates refer to; null when none.</summary>
    public ExprCollection? Default { get; }
    /// <summary>StableHash64 over names, ids, type keys, exposure and collections (recursively).</summary>
    public ulong Hash { get; }

    public ExprField this[int id] => _fields[id];

    public bool TryGetField(ReadOnlySpan<char> name, [NotNullWhen(true)] out ExprField? field) => _byNameSpan.TryGetValue(name, out field);

    public bool TryGetCollection(string name, [NotNullWhen(true)] out ExprCollection? collection) => _collectionsByName.TryGetValue(name, out collection);

    public override string ToString() => Name;

    private static void Append(ref StableHash64 h, ExprSchema schema)
    {
        h.Append(schema.Name);
        foreach (var f in schema._fields)
        {
            h.Append((int)f.Id);
            h.Append(f.Name);
            h.Append(f.Type.Key);
            h.Append(f.Exposed);
        }
        foreach (var c in schema._collections)
        {
            h.Append((int)c.Id);
            h.Append(c.Name);
            h.Append(ReferenceEquals(c, schema.Default));
            Append(ref h, c.Items);
        }
    }
}
