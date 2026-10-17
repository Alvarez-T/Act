using System.IO.Hashing;
using YFex.Helpers;

namespace YFex.Tests.Helpers;

public class StableHash64Tests
{
    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    public void KnownXxh64Vectors(string ascii, ulong expected)
    {
        StableHash64.Hash(System.Text.Encoding.ASCII.GetBytes(ascii)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(42UL)]
    [InlineData(ulong.MaxValue)]
    public void MatchesReferenceImplementation_ForAllLengthsAndChunkings(ulong seed)
    {
        var rng = new Random(1234);
        for (int len = 0; len <= 200; len++)
        {
            var data = new byte[len];
            rng.NextBytes(data);
            ulong expected = XxHash64.HashToUInt64(data, (long)seed);

            StableHash64.Hash(data, seed).Should().Be(expected, $"one-shot, length {len}");

            // Streaming in irregular chunks must give the same result.
            var h = StableHash64.Create(seed);
            int pos = 0, chunk = 1;
            while (pos < len)
            {
                int take = Math.Min(chunk, len - pos);
                h.Append(data.AsSpan(pos, take));
                pos += take;
                chunk = chunk * 3 % 37 + 1;
            }
            h.Finish().Should().Be(expected, $"streamed, length {len}");
        }
    }

    [Fact]
    public void Finish_DoesNotConsumeState()
    {
        var h = StableHash64.Create();
        h.Append(123L);
        h.Finish().Should().Be(h.Finish());
    }

    [Fact]
    public void Decimal_IsNormalized()
    {
        Hash(1.0m).Should().Be(Hash(1.00m)).And.Be(Hash(1m));
        Hash(1.5m).Should().NotBe(Hash(1.05m));

        static ulong Hash(decimal d)
        {
            var h = StableHash64.Create();
            h.Append(d);
            return h.Finish();
        }
    }

    [Fact]
    public void Strings_AreLengthPrefixed()
    {
        var a = StableHash64.Create();
        a.Append("ab");
        a.Append("c");
        var b = StableHash64.Create();
        b.Append("a");
        b.Append("bc");
        a.Finish().Should().NotBe(b.Finish());

        var n = StableHash64.Create();
        n.Append((string?)null);
        var e = StableHash64.Create();
        e.Append(string.Empty);
        n.Finish().Should().NotBe(e.Finish());
    }

    [Fact]
    public void Capture_copies_the_exact_stream_without_changing_the_hash()
    {
        var plain = StableHash64.Create(7);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var captured = StableHash64.Create(7, buffer);
        foreach (var h in new[] { 0, 1 })
        {
            ref var target = ref h == 0 ? ref plain : ref captured;
            target.Append("approval");
            target.Append(42L);
            target.Append(new byte[40]);                                       // crosses a 32-byte stripe
        }

        captured.Finish().Should().Be(plain.Finish());
        buffer.WrittenCount.Should().Be(4 + 16 + 8 + 40);                     // length prefix + UTF-16 text + long + bytes
        StableHash64.Hash(buffer.WrittenSpan, 7).Should().Be(plain.Finish());  // same bytes, same hash
    }
}
