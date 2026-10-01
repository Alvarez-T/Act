using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YFex.Validation.Generator;

/// <summary>One constant argument of a rule, from an attribute (<see cref="TypedConstant"/>) or a fluent call.</summary>
internal readonly struct ArgValue
{
    public ArgValue(object? value, string cSharp, bool isPrimitive)
    {
        Value = value;
        CSharp = cSharp;
        IsPrimitive = isPrimitive;
    }

    public object? Value { get; }
    public string CSharp { get; }        // C# literal ("default" when not a primitive)
    public bool IsPrimitive { get; }

    public static ArgValue From(TypedConstant c)
        => c.Kind == TypedConstantKind.Primitive ? new(c.Value, c.ToCSharpString(), true) : new(c.Value, "default", false);

    /// <summary>A compile-time constant of a fluent argument (<c>SemanticModel.GetConstantValue</c>).</summary>
    public static ArgValue FromConstant(object? value) => value switch
    {
        null => new(null, "null", true),
        string s => new(s, SymbolDisplay.FormatLiteral(s, quote: true), true),
        char ch => new(ch, SymbolDisplay.FormatLiteral(ch, quote: true), true),
        bool b => new(b, b ? "true" : "false", true),
        int i => new(i, i.ToString(CultureInfo.InvariantCulture), true),
        long l => new(l, l.ToString(CultureInfo.InvariantCulture) + "L", true),
        uint u => new(u, u.ToString(CultureInfo.InvariantCulture) + "U", true),
        ulong ul => new(ul, ul.ToString(CultureInfo.InvariantCulture) + "UL", true),
        decimal m => new(m, m.ToString(CultureInfo.InvariantCulture) + "m", true),
        double d => new(d, d.ToString("R", CultureInfo.InvariantCulture) + "d", true),
        float f => new(f, f.ToString("R", CultureInfo.InvariantCulture) + "f", true),
        short or byte or sbyte or ushort => new(value, System.Convert.ToString(value, CultureInfo.InvariantCulture)!, true),
        _ => new(value, "default", false),
    };
}

/// <summary>Arguments of one rule declaration, independent of where it was declared (attribute or fluent).</summary>
internal sealed class RuleInput
{
    public string AttributeMetadataName { get; set; } = "";   // DataAnnotations variants (StringLength, MinLength…)
    public ArgValue[] Args { get; set; } = new ArgValue[0];
    public KeyValuePair<string, ArgValue>[] Named { get; set; } = new KeyValuePair<string, ArgValue>[0];
    public INamedTypeSymbol? RuleType { get; set; }          // With<T> / [ValidateWith<T>]

    public static RuleInput From(AttributeData a)
    {
        var args = new ArgValue[a.ConstructorArguments.Length];
        for (int i = 0; i < args.Length; i++) args[i] = ArgValue.From(a.ConstructorArguments[i]);
        var named = new KeyValuePair<string, ArgValue>[a.NamedArguments.Length];
        for (int i = 0; i < named.Length; i++)
            named[i] = new(a.NamedArguments[i].Key, ArgValue.From(a.NamedArguments[i].Value));
        var cls = a.AttributeClass;
        return new RuleInput
        {
            AttributeMetadataName = cls?.MetadataName ?? "",
            Args = args,
            Named = named,
            RuleType = cls is { TypeArguments.Length: 1 } && cls.TypeArguments[0] is INamedTypeSymbol rt ? rt : null,
        };
    }
}
