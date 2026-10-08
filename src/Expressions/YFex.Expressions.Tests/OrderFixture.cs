using System.Collections.Immutable;
using System.Text;
using YFex.Validation;

namespace YFex.Expressions.Tests;

public enum PayMethod { Cash, Card, Pix }

/// <summary>
/// A runtime schema like a screen or a situation: top-level fields (Money, enum, bool, int, date, text) and one default
/// collection of lines. Values live in arrays; readers are a struct and a ref struct.
/// </summary>
public static class Order
{
    public const ushort Total = 0, Payment = 1, Member = 2, Installments = 3, Date = 4, Note = 5;
    public const ushort LinesId = 100;
    public const ushort Sku = 0, Category = 1, Quantity = 2, UnitPrice = 3;

    public static readonly ExprEnumDomain PayDomain = new("Payment", "PayMethod",
        [new EnumOption(0, "Cash", "Cash"), new EnumOption(1, "Card", "Card"), new EnumOption(2, "Pix", "Pix")]);

    public static readonly ExprSchema LineSchema = new("order.line",
    [
        new(Sku, "Sku", RawType.Text),
        new(Category, "Category", RawType.Text),
        new(Quantity, "Quantity", RawType.Int32),
        new(UnitPrice, "UnitPrice", Money.RawType),
    ]);

    public static readonly ExprSchema Schema = new("order",
    [
        new(Total, "Total", Money.RawType),
        new(Payment, "Payment", RawType.EnumCode, enumDomain: PayDomain),
        new(Member, "Member", RawType.Bool),
        new(Installments, "Installments", RawType.Int32),
        new(Date, "Date", RawType.Date),
        new(Note, "Note", RawType.Text, exposed: true),
    ],
    [new ExprCollection(LinesId, "Lines", LineSchema)], defaultCollection: 0, noun: "order");

    public static ExprCompileOptions Options(FunctionCatalog? functions = null) => new(Schema, functions ?? FunctionCatalog.BuiltIn);

    public static Reader Sale(PayMethod pay = PayMethod.Pix, bool member = false, string? note = null)
    {
        RawValue[][] lines =
        [
            [RawValue.From("beer"), RawValue.From("drinks"), RawValue.From(6), RawValueConvert.From(new Money(10))],
            [RawValue.From("tv"), RawValue.From("electronics"), RawValue.From(1), RawValueConvert.From(new Money(900))],
        ];
        RawValue[] fields =
        [
            RawValueConvert.From(new Money(960)), RawValue.FromEnum(pay), RawValue.From(member), RawValue.From(1),
            RawValue.From(new DateOnly(2026, 10, 1)), note is null ? RawValue.None : RawValue.From(note),
        ];
        return new Reader(fields, lines);
    }

    public static Expr Parse(string json) => ExprJson.Read(Encoding.UTF8.GetBytes(json));

    /// <summary>Compiles or fails the test with the violations.</summary>
    public static ExprProgram Compile(string json, ExprCompileOptions? options = null)
    {
        var errors = new List<Violation>();
        var program = ExprCompiler.Compile(Parse(json), "e", options ?? Options(), errors);
        errors.Should().BeEmpty();
        return program!;
    }

    /// <summary>The "path:code" pairs of a failed compilation.</summary>
    public static List<string> Errors(string json, ExprCompileOptions? options = null)
    {
        var errors = new List<Violation>();
        ExprCompiler.Compile(Parse(json), "e", options ?? Options(), errors).Should().BeNull();
        return errors.ConvertAll(static v => $"{v.Path}:{v.Code.Value}");
    }

    public static RawValue Run(ExprProgram p, Reader r, int item = -1)
    {
        var env = default(ExprEnv);
        return p.Run(ref r, in env, item);
    }
}

/// <summary>Array-backed reader (struct): fields by id, lines by index.</summary>
public struct Reader(RawValue[] fields, RawValue[][] lines) : IFieldReader
{
    public readonly RawValue Read(ushort fieldId) => fieldId < fields.Length ? fields[fieldId] : RawValue.None;
    public readonly int Count(ushort collectionId) => collectionId == Order.LinesId ? lines.Length : 0;
    public readonly RawValue ReadItem(ushort collectionId, int index, ushort fieldId) => lines[index][fieldId];
}

/// <summary>The same data through a ref struct (spans) — proves <c>allows ref struct</c>.</summary>
public readonly ref struct SpanReader(ReadOnlySpan<RawValue> fields) : IFieldReader
{
    private readonly ReadOnlySpan<RawValue> _fields = fields;
    public RawValue Read(ushort fieldId) => fieldId < _fields.Length ? _fields[fieldId] : RawValue.None;
    public int Count(ushort collectionId) => 0;
    public RawValue ReadItem(ushort collectionId, int index, ushort fieldId) => RawValue.None;
}
