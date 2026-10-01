using System.Collections.Immutable;
using YFex.Helpers;

namespace YFex.Rules.Tests.HandWritten;

// Hand-written equivalent of what YFex.Rules.Generator emits for the doc-12 situations. It pins the runtime
// contract independently of the generator (same role as YFex.Validation.Tests/HandWrittenCustomer.cs).

public enum Channel { Pos, Web }
public enum CustomerTier { None, Silver, Gold }
public enum ActorLevel { Seller, Manager }
public enum PaymentMethod { Cash, CreditCard, Pix, Boleto }

public readonly record struct TenantId(string Value);
public readonly record struct StoreId(string Value);

public readonly record struct HSaleLine(string Product, string Category, decimal Quantity, Money UnitPrice) : ISituation<HSaleLine>
{
    public static class Fields
    {
        public static readonly SituationField<HSaleLine, string> Product = new(0, "Product", RawType.Text);
        public static readonly SituationField<HSaleLine, string> Category = new(1, "Category", RawType.Text);
        public static readonly SituationField<HSaleLine, decimal> Quantity = new(2, "Quantity", RawType.Decimal);
        public static readonly SituationField<HSaleLine, Money> UnitPrice = new(3, "UnitPrice", Money.RawType);
    }

    public static SituationSchema Schema { get; } = new("sale.line",
    [
        new(0, "Product", RawType.Text),
        new(1, "Category", RawType.Text),
        new(2, "Quantity", RawType.Decimal),
        new(3, "UnitPrice", Money.RawType),
    ]);

    public static RawValue Read(in HSaleLine s, ushort fieldId) => fieldId switch
    {
        0 => RawValue.From(s.Product),
        1 => RawValue.From(s.Category),
        2 => RawValue.From(s.Quantity),
        3 => RawValue.From(s.UnitPrice),
        _ => default,
    };

    public static bool TryGetFieldId(ReadOnlySpan<char> name, out ushort id)
    {
        id = name switch { "Product" => 0, "Category" => 1, "Quantity" => 2, "UnitPrice" => 3, _ => ushort.MaxValue };
        return id != ushort.MaxValue;
    }

    public static int LineCount(in HSaleLine s) => 0;
    public static RawValue ReadLine(in HSaleLine s, int lineIndex, ushort lineFieldId) => default;
    public static DateOnly EffectiveDate(in HSaleLine s) => DateOnly.MaxValue;
    public static RuleTierPath TierPath(in HSaleLine s) => RuleTierPath.Code;

    public static void AppendTo(in HSaleLine s, ref StableHash64 hash)
    {
        RawValue.From(s.Product).AppendTo(ref hash);
        RawValue.From(s.Category).AppendTo(ref hash);
        RawValue.From(s.Quantity).AppendTo(ref hash);
        RawValue.From(s.UnitPrice).AppendTo(ref hash);
    }

    public static void Serialize(in HSaleLine s, ref SituationWriter w)
    {
        w.Write(RawValue.From(s.Product));
        w.Write(RawValue.From(s.Category));
        w.Write(RawValue.From(s.Quantity));
        w.Write(RawValue.From(s.UnitPrice));
    }

    public static HSaleLine Deserialize(ref SituationReader r)
        => new(r.Read<string>(), r.Read<string>(), r.Read<decimal>(), r.Read<Money>());

    public static ulong DiffMask(in HSaleLine a, in HSaleLine b)
    {
        ulong m = 0;
        if (a.Product != b.Product) m |= 1UL << 0;
        if (a.Category != b.Category) m |= 1UL << 1;
        if (a.Quantity != b.Quantity) m |= 1UL << 2;
        if (a.UnitPrice != b.UnitPrice) m |= 1UL << 3;
        return m;
    }
}

public readonly record struct HSale(
    TenantId Tenant, StoreId Store, Channel Channel, DateOnly BusinessDate, CustomerTier Tier, bool IsBirthdayMonth,
    ActorLevel Actor, PaymentMethod Payment, int Installments, Percentual ManualDiscount, ImmutableArray<HSaleLine> Lines)
    : ISituation<HSale>
{
    public Money Total
    {
        get
        {
            Money sum = Money.Zero;
            foreach (var l in Lines.IsDefault ? [] : Lines) sum += l.UnitPrice * l.Quantity;
            return sum;
        }
    }

    public static class Fields
    {
        public static readonly SituationField<HSale, TenantId> Tenant = new(0, "Tenant", RawType.Text);
        public static readonly SituationField<HSale, StoreId> Store = new(1, "Store", RawType.Text);
        public static readonly SituationField<HSale, Channel> Channel = new(2, "Channel", RawType.EnumCode);
        public static readonly SituationField<HSale, DateOnly> BusinessDate = new(3, "BusinessDate", RawType.Date);
        public static readonly SituationField<HSale, CustomerTier> Tier = new(4, "Tier", RawType.EnumCode);
        public static readonly SituationField<HSale, bool> IsBirthdayMonth = new(5, "IsBirthdayMonth", RawType.Bool);
        public static readonly SituationField<HSale, ActorLevel> Actor = new(6, "Actor", RawType.EnumCode);
        public static readonly SituationField<HSale, PaymentMethod> Payment = new(7, "Payment", RawType.EnumCode);
        public static readonly SituationField<HSale, int> Installments = new(8, "Installments", RawType.Int32);
        public static readonly SituationField<HSale, Percentual> ManualDiscount = new(9, "ManualDiscount", Percentual.RawType);
        public static readonly SituationField<HSale, Money> Total = new(11, "Total", Money.RawType);
    }

    public static SituationSchema Schema { get; } = new("sale",
    [
        new(0, "Tenant", RawType.Text, FieldRole.Tier, tier: 1),
        new(1, "Store", RawType.Text, FieldRole.Tier, tier: 2),
        new(2, "Channel", RawType.EnumCode),
        new(3, "BusinessDate", RawType.Date, FieldRole.EffectiveDate),
        new(4, "Tier", RawType.EnumCode),
        new(5, "IsBirthdayMonth", RawType.Bool),
        new(6, "Actor", RawType.EnumCode),
        new(7, "Payment", RawType.EnumCode),
        new(8, "Installments", RawType.Int32, FieldRole.Requested),
        new(9, "ManualDiscount", Percentual.RawType, FieldRole.Requested),
        new(10, "Lines", RawType.Of(ValueKind.None), FieldRole.Lines, lineSchema: HSaleLine.Schema),
        new(11, "Total", Money.RawType, FieldRole.Derived),
    ]);

    public static RawValue Read(in HSale s, ushort fieldId) => fieldId switch
    {
        0 => RawValue.From(s.Tenant.Value),
        1 => RawValue.From(s.Store.Value),
        2 => RawValue.FromEnum(s.Channel),
        3 => RawValue.From(s.BusinessDate),
        4 => RawValue.FromEnum(s.Tier),
        5 => RawValue.From(s.IsBirthdayMonth),
        6 => RawValue.FromEnum(s.Actor),
        7 => RawValue.FromEnum(s.Payment),
        8 => RawValue.From(s.Installments),
        9 => RawValue.From(s.ManualDiscount),
        11 => RawValue.From(s.Total),
        _ => default,
    };

    public static bool TryGetFieldId(ReadOnlySpan<char> name, out ushort id)
    {
        for (ushort i = 0; i < Schema.Count; i++)
            if (name.SequenceEqual(Schema[i].Name)) { id = i; return true; }
        id = 0;
        return false;
    }

    public static int LineCount(in HSale s) => s.Lines.IsDefault ? 0 : s.Lines.Length;
    public static RawValue ReadLine(in HSale s, int lineIndex, ushort lineFieldId) => HSaleLine.Read(in s.Lines.ItemRef(lineIndex), lineFieldId);
    public static DateOnly EffectiveDate(in HSale s) => s.BusinessDate;
    public static RuleTierPath TierPath(in HSale s) => RuleTierPath.Of(RawValue.From(s.Tenant.Value), RawValue.From(s.Store.Value));

    public static void AppendTo(in HSale s, ref StableHash64 hash)
    {
        for (ushort i = 0; i < 10; i++) Read(in s, i).AppendTo(ref hash);
        int n = LineCount(in s);
        hash.Append(n);
        for (int i = 0; i < n; i++) HSaleLine.AppendTo(in s.Lines.ItemRef(i), ref hash);
    }

    public static void Serialize(in HSale s, ref SituationWriter w)
    {
        for (ushort i = 0; i < 10; i++) w.Write(Read(in s, i));
        int n = LineCount(in s);
        w.WriteCount(n);
        for (int i = 0; i < n; i++) HSaleLine.Serialize(in s.Lines.ItemRef(i), ref w);
    }

    public static HSale Deserialize(ref SituationReader r)
    {
        var tenant = new TenantId(r.Read<string>());
        var store = new StoreId(r.Read<string>());
        var channel = r.ReadEnum<Channel>();
        var date = r.Read<DateOnly>();
        var tier = r.ReadEnum<CustomerTier>();
        var bday = r.Read<bool>();
        var actor = r.ReadEnum<ActorLevel>();
        var payment = r.ReadEnum<PaymentMethod>();
        var installments = r.Read<int>();
        var manual = r.Read<Percentual>();
        int n = r.ReadCount();
        var lines = ImmutableArray.CreateBuilder<HSaleLine>(n);
        for (int i = 0; i < n; i++) lines.Add(HSaleLine.Deserialize(ref r));
        return new(tenant, store, channel, date, tier, bday, actor, payment, installments, manual, lines.MoveToImmutable());
    }

    public static ulong DiffMask(in HSale a, in HSale b)
    {
        ulong m = 0;
        for (ushort i = 0; i < 10; i++) if (!Read(in a, i).Equals(Read(in b, i))) m |= 1UL << i;
        if (!a.Lines.AsSpan().SequenceEqual(b.Lines.AsSpan())) m |= 1UL << 10;
        return m;
    }
}
