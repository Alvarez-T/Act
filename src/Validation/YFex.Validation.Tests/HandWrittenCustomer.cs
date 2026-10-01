namespace YFex.Validation.Tests;

/// <summary>
/// Hand-written stand-in for what YFex.Validation.Generator will emit (03 §2): per-member Check
/// methods, constant paths, invariant gated by HasErrorFor. Lets Phase 1 runtime tests exercise the
/// contract before the generator exists.
/// </summary>
public readonly record struct CustomerDto(string Name, string Email, int Age, string Document, decimal Credit)
    : IValidatable<CustomerDto>
{
    public static class FieldIds
    {
        public const ushort Name = 0, Email = 1, Age = 2, Document = 3, Credit = 4;
    }

    public static class Fields
    {
        public static readonly FieldPath Name = FieldPath.Of("Name");
        public static readonly FieldPath Email = FieldPath.Of("Email");
        public static readonly FieldPath Age = FieldPath.Of("Age");
        public static readonly FieldPath Document = FieldPath.Of("Document");
        public static readonly FieldPath Credit = FieldPath.Of("Credit");
    }

    public static readonly FieldConstraints NameConstraints = FieldConstraints.Create(required: true, minLength: 2, maxLength: 80);

    private static bool CheckName(string? v, ref ViolationWriter w)
    {
        if (string.IsNullOrWhiteSpace(v)) { w.Add(in Fields.Name, RuleCodes.Required); return false; }
        if ((uint)(v.Length - 2) > 78u) { w.Add(in Fields.Name, RuleCodes.Length, RawValue.From(2), RawValue.From(80)); return false; }
        return true;
    }

    private static bool CheckEmail(string? v, ref ViolationWriter w)
    {
        if (v is null || v.AsSpan().IndexOf('@') <= 0) { w.Add(in Fields.Email, RuleCodes.Email); return false; }
        return true;
    }

    private static bool CheckAge(int v, ref ViolationWriter w)
    {
        if ((uint)v > 150u) { w.Add(in Fields.Age, RuleCodes.Range, RawValue.From(0), RawValue.From(150)); return false; }
        return true;
    }

    private static bool CheckDocument(string? v, ref ViolationWriter w)
    {
        if (string.IsNullOrWhiteSpace(v)) { w.Add(in Fields.Document, RuleCodes.Required); return false; }
        return true;
    }

    private static bool CheckCredit(decimal v, ref ViolationWriter w)
    {
        if (v < 0) { w.Add(in Fields.Credit, RuleCodes.Range, RawValue.From(0m), Severity.Warning); return false; }
        return true;
    }

    public static bool Validate(in CustomerDto v, ref ViolationWriter w)
    {
        int errorsBefore = w.ErrorCount;
        CheckName(v.Name, ref w);
        CheckEmail(v.Email, ref w);
        CheckAge(v.Age, ref w);
        CheckDocument(v.Document, ref w);
        CheckCredit(v.Credit, ref w);

        if (!w.HasErrorFor(in Fields.Age) && !w.HasErrorFor(in Fields.Document))
        {
            var prev = w.CurrentOrigin;
            w.CurrentOrigin = new(ViolationSource.Invariant, 1);
            if (v.Age < 18 && v.Document.Length == 14) w.Add(in Fields.Document, "customer.minor-cnpj");
            w.CurrentOrigin = prev;
        }

        RuntimeRules<CustomerDto>.Run(in v, ref w);
        return w.ErrorCount == errorsBefore;
    }

    public static bool ValidateMember(FieldId member, in CustomerDto v, ref ViolationWriter w) => member.Value switch
    {
        FieldIds.Name => CheckName(v.Name, ref w),
        FieldIds.Email => CheckEmail(v.Email, ref w),
        FieldIds.Age => CheckAge(v.Age, ref w),
        FieldIds.Document => CheckDocument(v.Document, ref w),
        FieldIds.Credit => CheckCredit(v.Credit, ref w),
        _ => true,
    };

    public static bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints)
    {
        switch (path)
        {
            case "Name": constraints = NameConstraints; return true;
            default: return RuntimeRules<CustomerDto>.TryGetConstraints(path, out constraints);
        }
    }

    public static bool TryGetFieldId(ReadOnlySpan<char> path, out FieldId id)
    {
        switch (path)
        {
            case "Name": id = new(FieldIds.Name); return true;
            case "Email": id = new(FieldIds.Email); return true;
            case "Age": id = new(FieldIds.Age); return true;
            case "Document": id = new(FieldIds.Document); return true;
            case "Credit": id = new(FieldIds.Credit); return true;
            default: id = default; return false;
        }
    }

    public static readonly CustomerDto ValidSample = new("Ana Souza", "ana@x.com", 30, "12345678901", 10m);
}
