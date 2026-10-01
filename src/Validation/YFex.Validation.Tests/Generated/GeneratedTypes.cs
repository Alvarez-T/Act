using System.Collections.Immutable;
using YFex.Validation.BuiltIn;
using DA = System.ComponentModel.DataAnnotations;

namespace YFex.Validation.Tests.Generated;

// ── Rule types used with [ValidateWith<T>] ──────────────────────────────────────

public readonly struct EvenNumber : IValueRule<int>
{
    public static RuleCode Code => "even";
    public static bool IsValid(in int value) => (value & 1) == 0;
}

public readonly struct MultipleOf : IValueRule<int, int>
{
    public static RuleCode Code => "multiple";
    public static bool IsValid(in int value, int arg) => value % arg == 0;
}

public readonly struct StartsWithLetter : ITextRule
{
    public static RuleCode Code => "letter.first";
    public static bool IsValid(ReadOnlySpan<char> value) => value.Length > 0 && char.IsLetter(value[0]);
}

public readonly struct MinDigits : ITextRule<int>
{
    public static RuleCode Code => "digits.min";
    public static bool IsValid(ReadOnlySpan<char> value, int arg)
    {
        int n = 0;
        foreach (char c in value) if (char.IsAsciiDigit(c)) n++;
        return n >= arg;
    }
}

public readonly struct EmailNotTaken : IAsyncValueRule<string>
{
    public static RuleCode Code => "email.taken";
    public static ValueTask<bool> IsValidAsync(string value, IServiceProvider services, CancellationToken ct)
        => ValueTask.FromResult(value != "taken@x.com");
}

// ── Validatable types ───────────────────────────────────────────────────────────

[Validatable]
public sealed partial record AddressDto
{
    [Required, Length(2, 60)] public string City { get; init; } = "";
    [Pattern(@"^\d{5}-\d{3}$")] public string? Zip { get; init; }
}

public sealed partial record CustomerDto
{
    [Required, Length(2, 80)] public string Name { get; init; } = "";
    [Required, Email, ValidateWith<EmailNotTaken>] public string Email { get; init; } = "";
    [Range(0, 150)] public int Age { get; init; }
    [Required, ValidateWith<DigitsOnly>, ValidateWith<MinDigits>(11)] public string Document { get; init; } = "";
    [Each] public ImmutableArray<AddressDto> Addresses { get; init; } = [];
    [Required(Severity = Severity.Warning)] public string? Nickname { get; init; }
    public bool IsCompany { get; init; }
    [Required(When = nameof(IsCompany), Code = "customer.company-name")] public string? CompanyName { get; init; }
    [Range("0.00", "99999.99")] public decimal CreditLimit { get; init; }
    [Range(0, 1_000_000)] public Money Balance { get; init; }
    [Range("1900-01-01", "2100-12-31")] public DateOnly? BirthDate { get; init; }
    [NotEmpty, Each, Length(1, 10)] public List<string> Tags { get; init; } = [];
    public AddressDto? Billing { get; init; }
    [Required(Profile = "final")] public string? SignedBy { get; init; }
    [ValidateWith<EvenNumber>, ValidateWith<MultipleOf>(4)] public int Slots { get; init; }
    [Range(1, 100, Coerce = true)] public int PageSize { get; init; } = 10;

    [Invariant]
    private static void MinorCannotHaveCnpj(in CustomerDto c, ref ViolationWriter w)
    {
        if (c.Age < 18 && c.Document.Length == 14) w.Add(in Fields.Document, "customer.minor-cnpj");
    }

    [Invariant(Reads = [nameof(Tags)])]
    private static void TagsNotDuplicated(in CustomerDto c, ref ViolationWriter w)
    {
        if (c.Tags.Count != c.Tags.Distinct().Count()) w.Add(in Fields.Tags, "tags.duplicated");
    }

    public static readonly CustomerDto Valid = new()
    {
        Name = "Ana Souza", Email = "ana@x.com", Age = 30, Document = "12345678901",
        Addresses = [new AddressDto { City = "Curitiba", Zip = "80000-000" }],
        Nickname = "ana", Tags = ["vip"], SignedBy = "manager", Slots = 8, CreditLimit = 100m,
        Balance = new Money(10m), BirthDate = new DateOnly(1990, 1, 1),
    };
}

/// <summary>DataAnnotations only (mapped at compile time, no reflection).</summary>
public partial class LegacyDto
{
    [DA.Required] public string? Code { get; set; }
    [DA.StringLength(5, MinimumLength = 2)] public string? Short { get; set; }
    [DA.Range(1, 9)] public int Level { get; set; }
    [DA.EmailAddress] public string? Mail { get; set; }
    [DA.RegularExpression("^[A-Z]+$")] public string? Upper { get; set; }
}

/// <summary>Positional record: attributes on parameters.</summary>
public sealed partial record Point3([Range(-10, 10)] int X, [Range(-10, 10)] int Y, [Required] string Label);

/// <summary>Struct type.</summary>
public readonly partial record struct Interval([Range(0, 100)] int From, [Range(0, 100)] int To)
{
    [Invariant]
    private static void Ordered(in Interval i, ref ViolationWriter w)
    {
        if (i.From > i.To) w.Add(FieldPath.Empty, "interval.order");
    }
}

/// <summary>Inheritance: derived runs base rules first; ids continue after base members.</summary>
public partial class PersonBase
{
    [Required] public string? FirstName { get; set; }
}

public partial class Employee : PersonBase
{
    [Required] public string? Badge { get; set; }
}

/// <summary>Nested inside containers (CQRS message shape).</summary>
public static partial class Sales
{
    public static partial class Commands
    {
        public sealed partial record CloseSale([Range(1, 48)] int Installments, [Each] ImmutableArray<SaleLine> Lines);

        public sealed partial record SaleLine([Required] string Sku, [Range("0.001", "9999")] decimal Quantity);
    }
}

/// <summary>Instance invariant that passes 'this' on → reads-all (YFV0009 info).</summary>
public partial class Order
{
    [Range(0, 10)] public int Count { get; set; }

    [Invariant]
    private void Check(ref ViolationWriter w)
    {
        if (!IsOk(this)) w.Add(FieldPath.Empty, "order.bad");
    }

    private static bool IsOk(Order o) => o.Count != 7;
}
