using System.Text;
using Xunit;
using YFex.Canonical;
using YFex.Diagnostics;
using YFex.Installations;

namespace YFex.Tests.Installations;

public sealed class InstallationIdTests
{
    private const string Text = "00112233-4455-7677-8899-aabbccddeeff";

    [Fact]
    public void Canonical_form_is_big_endian_and_uses_the_shared_contract()
    {
        var value = InstallationId.Parse(Text);
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(Write(in value, bytes, out int written));
        Assert.Equal(16, written);
        Assert.Equal("00112233445576778899AABBCCDDEEFF", Convert.ToHexString(bytes));
        Assert.Equal(value.Value, new Guid(bytes, bigEndian: true));
    }

    [Fact]
    public void Short_canonical_destination_is_unchanged()
    {
        var value = InstallationId.Parse(Text);
        Span<byte> bytes = stackalloc byte[15];
        bytes.Fill(0xA5);
        Assert.False(InstallationId.TryWriteCanonical(in value, bytes, out int written));
        Assert.Equal(0, written);
        foreach (byte item in bytes) Assert.Equal(0xA5, item);
    }

    [Fact]
    public void Character_and_utf8_text_round_trip()
    {
        var value = InstallationId.Parse(Text);
        Span<char> characters = stackalloc char[36];
        Span<byte> utf8 = stackalloc byte[36];
        Assert.True(value.TryFormat(characters, out int charactersWritten));
        Assert.True(value.TryFormat(utf8, out int bytesWritten));
        Assert.Equal(Text, new string(characters[..charactersWritten]));
        Assert.Equal(Text, Encoding.UTF8.GetString(utf8[..bytesWritten]));
        Assert.Equal(value, ParseCharacters<InstallationId>(characters));
        Assert.Equal(value, ParseUtf8<InstallationId>(utf8));
        Assert.True(InstallationId.TryParse(Text, null, out var parsed));
        Assert.Equal(value, parsed);
        Assert.True(InstallationId.TryParse(Text.ToUpperInvariant(), null, out parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00112233445576778899aabbccddeeff")]
    [InlineData("{00112233-4455-7677-8899-aabbccddeeff}")]
    [InlineData(" 00112233-4455-7677-8899-aabbccddeeff")]
    [InlineData("00112233-4455-7677-8899-aabbccddeeff ")]
    [InlineData("00112233-4455-7677-8899-aabbccddeefz")]
    public void Malformed_text_is_refused(string? text)
    {
        Assert.False(InstallationId.TryParse(text, null, out var result));
        Assert.True(result.IsEmpty);
        Assert.False(InstallationId.TryParse(Encoding.UTF8.GetBytes(text ?? ""), null, out result));
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Non_ascii_utf8_and_oversized_input_are_refused()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Text);
        bytes[0] = 0xFF;
        Assert.False(InstallationId.TryParse(bytes, null, out _));
        Assert.False(InstallationId.TryParse(new byte[4096], null, out _));
    }

    [Fact]
    public void Empty_value_is_representable_but_is_not_an_enrolled_identity()
    {
        Assert.True(default(InstallationId).IsEmpty);
        Assert.Equal(default, InstallationId.Parse("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void Formatting_reports_short_destination_without_throwing()
    {
        var value = InstallationId.Parse(Text);
        Span<char> characters = stackalloc char[35];
        Span<byte> bytes = stackalloc byte[35];
        Assert.False(value.TryFormat(characters, out int charactersWritten));
        Assert.False(value.TryFormat(bytes, out int bytesWritten));
        Assert.Equal(0, charactersWritten);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Generic_logging_writer_is_registered_as_a_search_key()
    {
        Assert.NotNull(LogValueWriterFor<InstallationId>.Write);
        Assert.True(LogValueWriterFor<InstallationId>.IsSearchKey);
    }

    [Fact]
    public void Span_round_trips_do_not_allocate_after_warmup()
    {
        var value = InstallationId.Parse(Text);
        Exercise(in value, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool valid = Exercise(in value, 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.Equal(0, allocated);
    }

    private static bool Exercise(in InstallationId value, int count)
    {
        Span<char> characters = stackalloc char[36];
        Span<byte> utf8 = stackalloc byte[36];
        Span<byte> canonical = stackalloc byte[16];
        for (int index = 0; index < count; index++)
        {
            if (!value.TryFormat(characters, out _) || !value.TryFormat(utf8, out _)
                || !InstallationId.TryParse(characters, null, out var a)
                || !InstallationId.TryParse(utf8, null, out var b)
                || !InstallationId.TryWriteCanonical(in value, canonical, out _) || a != value || b != value) return false;
        }
        return true;
    }

    private static bool Write<T>(in T value, Span<byte> bytes, out int written) where T : ICanonicalValue<T>
        => T.TryWriteCanonical(in value, bytes, out written);
    private static T ParseCharacters<T>(ReadOnlySpan<char> text) where T : ISpanParsable<T> => T.Parse(text, null);
    private static T ParseUtf8<T>(ReadOnlySpan<byte> text) where T : IUtf8SpanParsable<T> => T.Parse(text, null);
}
