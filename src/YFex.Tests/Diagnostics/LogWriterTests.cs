using YFex.Diagnostics;
using YFex.Extensions;

namespace YFex.Tests.Diagnostics;

public sealed class LogWriterTests
{
    internal sealed class TestStorage(int growTo = 0, int references = 8) : LogRecordStorage
    {
        public byte[] Bytes = new byte[256];
        public readonly List<object> References = [];
        public int GrowCalls;

        public override Span<byte> Grow(int written, int required)
        {
            GrowCalls++;
            if (growTo == 0 || written + required > growTo) return default;
            var larger = new byte[growTo];
            Bytes.AsSpan(0, written).CopyTo(larger);
            Bytes = larger;
            return larger;
        }

        public override int AddReference(object value)
        {
            for (int i = 0; i < References.Count; i++)
                if (ReferenceEquals(References[i], value)) return i;
            if (References.Count == references) return -1;
            References.Add(value);
            return References.Count - 1;
        }
    }

    private enum Color : byte { Red = 1, Green = 2 }
    private enum Signed : short { Minus = -3, Plus = 4 }

    private sealed record Item(string Name, int Quantity);

    private sealed record Order(Guid Id, Item First, Money Total);

    static LogWriterTests()
    {
        LogShapeFor<Item>.Register(["name", "quantity"], static (in Item v, ref LogWriter w) =>
        {
            w.Write(0, v.Name);
            w.Write(1, v.Quantity);
        });
        LogShapeFor<Order>.Register(["id", "first", "total"], static (in Order v, ref LogWriter w) =>
        {
            w.Write(0, v.Id);
            w.WriteShape(1, v.First);
            w.WriteRawValue(2, v.Total);
        });
    }

    [Fact]
    public void Primitives_round_trip()
    {
        Span<byte> buffer = stackalloc byte[512];
        var w = new LogWriter(buffer);
        var guid = Guid.NewGuid();
        var now = new DateTime(2026, 10, 1, 10, 41, 2, DateTimeKind.Utc);
        var offset = new DateTimeOffset(2026, 10, 1, 10, 41, 2, TimeSpan.FromHours(-3));

        w.Write(0, true);
        w.Write(1, 42);
        w.Write(2, -7L);
        w.Write(3, ulong.MaxValue);
        w.Write(4, 3.5);
        w.Write(5, 149.90m);
        w.Write(6, guid);
        w.Write(7, now);
        w.Write(8, offset);
        w.Write(9, TimeSpan.FromMilliseconds(31.9));
        w.Write(10, new DateOnly(2026, 10, 1));
        w.Write(11, new TimeOnly(10, 41));
        w.WriteNull(12);
        w.WriteCount(13, 4);
        w.WriteLength(14, 1L << 40);
        w.WriteErased(15);

        w.IsTruncated.Should().BeFalse();
        w.FieldCount.Should().Be(16);

        var r = new LogFieldReader(w.Written);
        Next(ref r, 0, LogValueKind.Bool).GetBool().Should().BeTrue();
        Next(ref r, 1, LogValueKind.Int32).GetInt32().Should().Be(42);
        Next(ref r, 2, LogValueKind.Int64).GetInt64().Should().Be(-7);
        Next(ref r, 3, LogValueKind.UInt64).GetUInt64().Should().Be(ulong.MaxValue);
        Next(ref r, 4, LogValueKind.Double).GetDouble().Should().Be(3.5);
        Next(ref r, 5, LogValueKind.Decimal).GetDecimal().Should().Be(149.90m);
        Next(ref r, 6, LogValueKind.Guid).GetGuid().Should().Be(guid);
        LogField dt = Next(ref r, 7, LogValueKind.DateTime);
        dt.GetDateTime().Should().Be(now);
        dt.GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        Next(ref r, 8, LogValueKind.DateTimeOffset).GetDateTimeOffset().Should().Be(offset);
        Next(ref r, 9, LogValueKind.TimeSpan).GetTimeSpan().Should().Be(TimeSpan.FromMilliseconds(31.9));
        Next(ref r, 10, LogValueKind.DateOnly).GetDateOnly().Should().Be(new DateOnly(2026, 10, 1));
        Next(ref r, 11, LogValueKind.TimeOnly).GetTimeOnly().Should().Be(new TimeOnly(10, 41));
        Next(ref r, 12, LogValueKind.Null);
        Next(ref r, 13, LogValueKind.Count).GetInt32().Should().Be(4);
        Next(ref r, 14, LogValueKind.Length).GetInt64().Should().Be(1L << 40);
        Next(ref r, 15, LogValueKind.Omitted).GetOmission().Should().Be(LogOmission.Erased);
        r.TryRead(out _).Should().BeFalse();
        r.IsMalformed.Should().BeFalse();
    }

    [Fact]
    public void Text_is_copied_capped_and_flagged()
    {
        Span<byte> buffer = stackalloc byte[4096];
        var w = new LogWriter(buffer);
        w.Write(0, "olá");
        w.Write(1, (string?)null);
        w.WriteScrubbed(2, "cliente pediu nota com cpf 123.456.789-09", maxCharacters: 10);
        w.Write(3, new string('x', 2000));

        var r = new LogFieldReader(w.Written);
        Next(ref r, 0, LogValueKind.Text).GetText(out LogTextFlags f0).ToString().Should().Be("olá");
        f0.Should().Be(LogTextFlags.None);
        Next(ref r, 1, LogValueKind.Null);
        Next(ref r, 2, LogValueKind.Text).GetText(out LogTextFlags f2).ToString().Should().Be("cliente pe");
        f2.Should().Be(LogTextFlags.Scrub | LogTextFlags.Truncated);
        ReadOnlySpan<char> big = Next(ref r, 3, LogValueKind.Text).GetText(out LogTextFlags f3);
        big.Length.Should().Be(LogWriter.MaxTextCharacters);
        f3.Should().Be(LogTextFlags.Truncated);
    }

    [Fact]
    public void Text_that_does_not_fit_keeps_a_prefix()
    {
        Span<byte> buffer = stackalloc byte[40];
        var w = new LogWriter(buffer);
        w.Write(0, new string('a', 100));
        w.IsTruncated.Should().BeFalse();
        var r = new LogFieldReader(w.Written);
        ReadOnlySpan<char> text = Next(ref r, 0, LogValueKind.Text).GetText(out LogTextFlags flags);
        text.Length.Should().Be((40 - 3 - 3) / 2);
        flags.Should().HaveFlag(LogTextFlags.Truncated);
    }

    [Fact]
    public void Core_value_types_write_their_raw_value()
    {
        Span<byte> buffer = stackalloc byte[1024];
        var w = new LogWriter(buffer);
        w.Write<Money>(0, new Money(149.90m));
        w.Write<Percentual>(1, 12.5m.AsPercentual());
        w.Write<Mass>(2, Mass.FromGrams(1500m));
        w.Write<FieldPath>(3, FieldPath.Of("Lines[].Quantity", 3));
        w.WriteRaw(4, RawValue.FromEnum(Color.Green));

        var r = new LogFieldReader(w.Written);
        Next(ref r, 0, LogValueKind.Raw).GetRaw().As<Money>().Should().Be(new Money(149.90m));
        Next(ref r, 1, LogValueKind.Raw).GetRaw().As<Percentual>().Should().Be(12.5m.AsPercentual());
        Next(ref r, 2, LogValueKind.Raw).GetRaw().As<Mass>().Should().Be(Mass.FromGrams(1500m));
        Next(ref r, 3, LogValueKind.Text).GetText(out _).ToString().Should().Be("Lines[3].Quantity");
        RawValue enumRaw = Next(ref r, 4, LogValueKind.Raw).GetRaw();
        enumRaw.Should().Be(RawValue.FromEnum(Color.Green));
    }

    [Fact]
    public void Enums_store_the_value_and_a_name_table_reference()
    {
        var storage = new TestStorage();
        var w = new LogWriter(storage.Bytes, storage);
        w.WriteEnum(0, Color.Green);
        w.WriteEnum(1, Signed.Minus);
        w.WriteEnum(2, (Color)99);

        var r = new LogFieldReader(w.Written);
        LogField green = Next(ref r, 0, LogValueKind.Enum);
        green.GetInt64().Should().Be(2);
        ((LogEnumNames)storage.References[green.GetEnumNamesReference()]).NameOf(green.GetInt64()).Should().Be("Green");
        LogField minus = Next(ref r, 1, LogValueKind.Enum);
        minus.GetInt64().Should().Be(-3);
        ((LogEnumNames)storage.References[minus.GetEnumNamesReference()]).NameOf(-3).Should().Be("Minus");
        LogField undefined = Next(ref r, 2, LogValueKind.Enum);
        ((LogEnumNames)storage.References[undefined.GetEnumNamesReference()]).NameOf(undefined.GetInt64()).Should().Be("99");
        storage.References.Should().HaveCount(2, "one shared name table per enum type");
    }

    [Fact]
    public void Formatted_values_are_written_as_text()
    {
        Span<byte> buffer = stackalloc byte[512];
        var w = new LogWriter(buffer);
        w.WriteFormatted(0, 1234.5);
        w.WriteSpanFormattable(1, 149.9, "F2");

        var r = new LogFieldReader(w.Written);
        System.Text.Encoding.UTF8.GetString(Next(ref r, 0, LogValueKind.Utf8).GetUtf8(out _)).Should().Be("1234.5");
        Next(ref r, 1, LogValueKind.Text).GetText(out _).ToString().Should().Be("149.90");
        r.TryRead(out _).Should().BeFalse();
        r.IsMalformed.Should().BeFalse();
    }

    [Fact]
    public void Shapes_nest_with_name_tables_and_stop_at_the_depth_limit()
    {
        var storage = new TestStorage();
        var w = new LogWriter(storage.Bytes, storage);
        var order = new Order(Guid.NewGuid(), new Item("dipirona", 2), new Money(10m));
        w.WriteShape(0, order);
        w.BeginObject(1).Should().BeTrue();
        w.BeginObject(2).Should().BeTrue();
        w.WriteShape(3, order);              // already at depth 2
        w.EndObject();
        w.EndObject();
        w.WriteShape(4, "no shape for string");
        w.WriteShape<Item?>(5, null);

        var r = new LogFieldReader(w.Written);
        LogField start = Next(ref r, 0, LogValueKind.ObjectStart);
        storage.References[start.GetObjectNamesReference()].Should().BeEquivalentTo(new[] { "id", "first", "total" });
        Next(ref r, 0, LogValueKind.Guid).GetGuid().Should().Be(order.Id);
        LogField itemStart = Next(ref r, 1, LogValueKind.ObjectStart);
        r.Depth.Should().Be(2);
        storage.References[itemStart.GetObjectNamesReference()].Should().BeEquivalentTo(new[] { "name", "quantity" });
        Next(ref r, 0, LogValueKind.Text).GetText(out _).ToString().Should().Be("dipirona");
        Next(ref r, 1, LogValueKind.Int32).GetInt32().Should().Be(2);
        Next(ref r, 0, LogValueKind.ObjectEnd);
        Next(ref r, 2, LogValueKind.Raw).GetRaw().As<Money>().Should().Be(new Money(10m));
        Next(ref r, 0, LogValueKind.ObjectEnd);
        r.Depth.Should().Be(0);

        Next(ref r, 1, LogValueKind.ObjectStart);
        Next(ref r, 2, LogValueKind.ObjectStart);
        Next(ref r, 3, LogValueKind.Omitted).GetOmission().Should().Be(LogOmission.DepthLimit);
        Next(ref r, 0, LogValueKind.ObjectEnd);
        Next(ref r, 0, LogValueKind.ObjectEnd);
        Next(ref r, 4, LogValueKind.Omitted).GetOmission().Should().Be(LogOmission.NoShape);
        Next(ref r, 5, LogValueKind.Null);
        r.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void Named_fields_reference_the_name()
    {
        var storage = new TestStorage();
        var w = new LogWriter(storage.Bytes, storage);
        w.Write(w.Field("itens.Count"), 1824);

        var r = new LogFieldReader(w.Written);
        r.TryRead(out LogField field).Should().BeTrue();
        field.IsNamed.Should().BeTrue();
        storage.References[field.NameReference].Should().Be("itens.Count");
        field.GetInt32().Should().Be(1824);

        var noStorage = new LogWriter(stackalloc byte[64]);
        noStorage.Field("x").Should().Be(LogWriter.NoField);
        noStorage.Write(LogWriter.NoField, 1);
        noStorage.FieldCount.Should().Be(0);
        noStorage.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void Grows_once_then_truncates_without_throwing()
    {
        var storage = new TestStorage(growTo: 512);
        storage.Bytes = new byte[16];
        var w = new LogWriter(storage.Bytes, storage);
        w.Write(0, 1L);                       // 11 bytes
        w.Write(1, 2L);                       // needs growth → 512
        storage.GrowCalls.Should().Be(1);
        for (int i = 0; i < 100; i++) w.Write((ushort)(2 + i), (long)i);
        w.IsTruncated.Should().BeTrue();
        storage.GrowCalls.Should().Be(1, "a record grows at most once");
        w.FieldCount.Should().BeLessThanOrEqualTo(LogWriter.MaxFieldsPerRecord);

        var r = new LogFieldReader(w.Written);
        int count = 0;
        while (r.TryRead(out LogField f))
        {
            f.Field.Should().Be((ushort)count);
            f.GetInt64().Should().Be(count < 2 ? count + 1 : count - 2);
            count++;
        }
        r.IsMalformed.Should().BeFalse();
        count.Should().Be(w.FieldCount);
    }

    [Fact]
    public void Field_limit_marks_truncation()
    {
        Span<byte> buffer = stackalloc byte[4096];
        var w = new LogWriter(buffer);
        for (int i = 0; i < LogWriter.MaxFieldsPerRecord + 5; i++) w.Write((ushort)i, i);
        w.FieldCount.Should().Be(LogWriter.MaxFieldsPerRecord);
        w.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void Reader_reports_malformed_data()
    {
        byte[] bytes = [0, 0, (byte)LogValueKind.Int64, 1, 2];   // payload too short
        var r = new LogFieldReader(bytes);
        r.TryRead(out _).Should().BeFalse();
        r.IsMalformed.Should().BeTrue();

        byte[] unknown = [0, 0, 200];
        var u = new LogFieldReader(unknown);
        u.TryRead(out _).Should().BeFalse();
        u.IsMalformed.Should().BeTrue();
    }

    [Fact]
    public void Writing_values_does_not_allocate()
    {
        var storage = new TestStorage();
        storage.Bytes = new byte[4096];
        var money = new Money(149.90m);
        var path = FieldPath.Of("Lines[].Quantity", 3);
        var item = new Item("dipirona", 2);
        Write(storage, money, path, item);    // warm up (enum table, statics)

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            storage.References.Clear();
            Write(storage, money, path, item);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);

        static void Write(TestStorage storage, in Money money, in FieldPath path, Item item)
        {
            var w = new LogWriter(storage.Bytes, storage);
            w.Write(0, 42);
            w.Write(1, 149.90m);
            w.Write(2, "cliente");
            w.Write<Money>(3, money);
            w.Write<FieldPath>(4, path);
            w.WriteEnum(5, Color.Red);
            w.WriteFormatted(6, 3.25);
            w.WriteShape(7, item);
            w.Write(w.Field("hole"), Guid.Empty);
        }
    }

    private static LogField Next(ref LogFieldReader reader, ushort field, LogValueKind kind)
    {
        reader.TryRead(out LogField entry).Should().BeTrue();
        entry.Kind.Should().Be(kind);
        if (kind != LogValueKind.ObjectEnd) entry.Field.Should().Be(field);
        return entry;
    }
}
