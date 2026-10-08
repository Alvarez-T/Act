using YFex.Diagnostics;
using YFex.Privacy;

namespace YFex.Tests.Privacy;

/// <summary>Hand-written IClassifiedData (what YFex.Privacy.Generator emits for a [Sensitive] value object).</summary>
internal readonly struct TestCpf(string digits) : IClassifiedData<TestCpf>
{
    public static DataClassification Classification { get; } =
        new(DataSensitivity.PersonalIdentifier, DataCategory.Identification, DataClassificationFlags.Identifier);

    public static int MaxMaskedLength => 14;

    public static bool TryWriteMasked(in TestCpf value, Span<char> destination, out int written)
    {
        string d = value._digits;
        if (d.Length != 11) return Masks.TryWriteHidden(destination, out written);
        Span<char> punctuated = stackalloc char[14];
        $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}".AsSpan().CopyTo(punctuated);
        return Masks.TryWritePattern(punctuated, "***.###.###-**", destination, out written);
    }

    private readonly string _digits = digits;
}

internal readonly struct TestDiagnosis(string text) : IClassifiedData<TestDiagnosis>
{
    public static DataClassification Classification { get; } = new(DataSensitivity.SensitivePersonal, DataCategory.Health);
    public static int MaxMaskedLength => Masks.Hidden.Length;
    public static bool TryWriteMasked(in TestDiagnosis value, Span<char> destination, out int written) => Masks.TryWriteHidden(destination, out written);
    public string Text { get; } = text;
}

public sealed class MasksTests
{
    private static string Apply(Func<Span<char>, (bool Ok, int Written)> write)
    {
        Span<char> buffer = stackalloc char[64];
        (bool ok, int written) = write(buffer);
        ok.Should().BeTrue();
        return new string(buffer[..written]);
    }

    [Theory]
    [InlineData("123.456.789-09", "***.456.789-**")]
    [InlineData("12.345.678/0001-95", "12.345.678/0001-95")]     // second alternative (CNPJ kept)
    [InlineData("123456789-09", Masks.Hidden)]                    // length matches nothing
    [InlineData("123-456-789.09", Masks.Hidden)]                  // literal mismatch
    public void Pattern_alternatives_by_length_fail_closed(string source, string expected)
        => Apply(d => (Masks.TryWritePattern(source, "***.###.###-**|##.###.###/####-##", d, out int w), w)).Should().Be(expected);

    [Theory]
    [InlineData("joao.silva@acme.com.br", "j***@acme.com.br")]
    [InlineData("@acme.com", Masks.Hidden)]
    [InlineData("joao@", Masks.Hidden)]
    [InlineData("sem-arroba", Masks.Hidden)]
    public void Email(string source, string expected)
        => Apply(d => (Masks.TryWriteEmail(source, d, out int w), w)).Should().Be(expected);

    [Theory]
    [InlineData("João da Silva Santos", "J. S. S.")]
    [InlineData("  maria   de  souza ", "M. S.")]
    [InlineData("Li Wu", Masks.Hidden)]
    public void Initials(string source, string expected)
        => Apply(d => (Masks.TryWriteInitials(source, d, out int w), w)).Should().Be(expected);

    [Fact]
    public void Last_and_first_use_a_fixed_number_of_stars()
    {
        Apply(d => (Masks.TryWriteLast("(11) 98765-4321", 4, d, out int w), w)).Should().Be("****4321");
        Apply(d => (Masks.TryWriteFirst("Rua das Flores 123", 3, d, out int w), w)).Should().Be("Rua****");
        Apply(d => (Masks.TryWriteLast("1234", 4, d, out int w), w)).Should().Be(Masks.Hidden);
    }

    [Theory]
    [InlineData("192.168.10.42", "192.168.10.*")]
    [InlineData("2001:db8::1", "2001:db8::*")]
    [InlineData("1.2.3", Masks.Hidden)]
    public void Ip_address(string source, string expected)
        => Apply(d => (Masks.TryWriteIpAddress(source, d, out int w), w)).Should().Be(expected);

    [Fact]
    public void Too_small_destination_returns_false()
    {
        Span<char> tiny = stackalloc char[3];
        Masks.TryWriteHidden(tiny, out int written).Should().BeFalse();
        written.Should().Be(0);
    }

    [Fact]
    public void Default_presets_follow_the_sensitivity()
    {
        Masks.DefaultPreset(DataSensitivity.Personal, out int personal).Should().Be(MaskPreset.First);
        personal.Should().Be(1);
        Masks.DefaultPreset(DataSensitivity.PersonalIdentifier, out _).Should().Be(MaskPreset.Last);
        Masks.DefaultPreset(DataSensitivity.SensitivePersonal, out _).Should().Be(MaskPreset.Hidden);
        Masks.DefaultPreset(DataSensitivity.Internal, out _).Should().Be(MaskPreset.None);
    }

    [Fact]
    public void Masking_does_not_allocate()
    {
        Span<char> buffer = stackalloc char[64];
        Masks.TryWritePattern("123.456.789-09", "***.###.###-**", buffer, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Masks.TryWritePattern("123.456.789-09", "***.###.###-**", buffer, out _);
            Masks.TryWriteEmail("joao.silva@acme.com.br", buffer, out _);
            Masks.TryWriteInitials("João da Silva Santos", buffer, out _);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}

public sealed class DataCategoryAndClassificationTests
{
    [Fact]
    public void Well_known_categories_have_stable_minimums()
    {
        DataCategory.Health.IsSpecialCategory.Should().BeTrue();
        DataCategory.Contact.MinimumSensitivity.Should().Be(DataSensitivity.Personal);
        DataCategory.FromId(DataCategory.Health.Id).Should().BeSameAs(DataCategory.Health);
        DataCategory.Get("health").Should().BeSameAs(DataCategory.Health);
    }

    [Fact]
    public void Define_is_idempotent_and_rejects_a_different_minimum()
    {
        DataCategory first = DataCategory.Define("test.loyalty", DataSensitivity.Personal);
        DataCategory.Define("test.loyalty", DataSensitivity.Personal).Should().BeSameAs(first);
        FluentActions.Invoking(() => DataCategory.Define("test.loyalty", DataSensitivity.Financial)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => DataCategory.Define("Not Valid", DataSensitivity.Personal)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Classification_max_and_floor()
    {
        var cpf = new DataClassification(DataSensitivity.PersonalIdentifier, DataCategory.Identification, DataClassificationFlags.Identifier);
        var health = new DataClassification(DataSensitivity.SensitivePersonal, DataCategory.Health, DataClassificationFlags.RecordLevel);
        DataClassification max = DataClassification.Max(cpf, health);
        max.Sensitivity.Should().Be(DataSensitivity.SensitivePersonal);
        max.Category.Should().BeSameAs(DataCategory.Health);
        max.Flags.Should().Be(DataClassificationFlags.Identifier | DataClassificationFlags.RecordLevel);
        cpf.AtLeast(DataSensitivity.SensitivePersonal).Sensitivity.Should().Be(DataSensitivity.SensitivePersonal);
        cpf.AtLeast(DataSensitivity.Personal).Should().Be(cpf);
        DataClassification.NotPersonal.IsPersonal.Should().BeFalse();
        DataSensitivity.Secret.IsPersonal().Should().BeFalse();
    }

    [Fact]
    public void Classified_helpers_and_boxed_view()
    {
        var cpf = new TestCpf("12345678909");
        Classified.Mask(in cpf).Should().Be("***.456.789-**");
        Classified.Of(in cpf).Sensitivity.Should().Be(DataSensitivity.PersonalIdentifier);
        IClassifiedData boxed = cpf;
        boxed.Classify().Category.Should().BeSameAs(DataCategory.Identification);
    }
}

public sealed class LogWriterPrivacyTests
{
    private static (LogValueKind Kind, string? Text, LogTextFlags Flags, LogOmission Omission) Single(scoped ReadOnlySpan<byte> written)
    {
        var reader = new LogFieldReader(written);
        reader.TryRead(out LogField field).Should().BeTrue();
        if (field.Kind == LogValueKind.Text)
        {
            ReadOnlySpan<char> text = field.GetText(out LogTextFlags flags);
            return (field.Kind, new string(text), flags, default);
        }
        return (field.Kind, null, default, field.GetOmission());
    }

    [Fact]
    public void Personal_value_object_is_written_masked()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        writer.WriteClassified(0, new TestCpf("12345678909"));
        var entry = Single(writer.Written);
        entry.Kind.Should().Be(LogValueKind.Text);
        entry.Text.Should().Be("***.456.789-**");
        entry.Flags.Should().HaveFlag(LogTextFlags.Masked);
    }

    [Fact]
    public void Sensitive_value_object_is_never_recorded()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        writer.WriteClassified(0, new TestDiagnosis("F41.1"));
        var entry = Single(writer.Written);
        entry.Kind.Should().Be(LogValueKind.Omitted);
        entry.Omission.Should().Be(LogOmission.Sensitive);
    }

    [Fact]
    public void Record_level_floor_raises_a_personal_value_to_never_recorded()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        writer.WriteClassified(0, new TestCpf("12345678909"), DataSensitivity.SensitivePersonal);
        Single(writer.Written).Omission.Should().Be(LogOmission.Sensitive);
    }

    [Theory]
    [InlineData(DataSensitivity.Personal, "João da Silva", "J****")]
    [InlineData(DataSensitivity.PersonalIdentifier, "MG-12.345.678", "****78")]
    public void Raw_personal_text_uses_the_level_default_mask(DataSensitivity sensitivity, string value, string expected)
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        writer.WriteMaskedText(0, value, sensitivity);
        Single(writer.Written).Text.Should().Be(expected);
    }

    [Fact]
    public void Secret_text_is_erased()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        writer.WriteMaskedText(0, "hunter2", DataSensitivity.Secret);
        Single(writer.Written).Omission.Should().Be(LogOmission.Erased);
    }
}
