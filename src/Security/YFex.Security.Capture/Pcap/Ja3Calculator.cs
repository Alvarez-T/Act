using System.Security.Cryptography;
using System.Text;

namespace YFex.Security.Capture.Pcap;

public static class Ja3Calculator
{
    private static readonly HashSet<ushort> GreaseValues =
    [
        0x0a0a, 0x1a1a, 0x2a2a, 0x3a3a, 0x4a4a,
        0x5a5a, 0x6a6a, 0x7a7a, 0x8a8a, 0x9a9a,
        0xaaaa, 0xbaba, 0xcaca, 0xdada, 0xeaea, 0xfafa
    ];

    public static (string Ja3Hash, string Ja3Raw, string Sni) ComputeFromClientHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x16)
            return ("", "", "");

        int recordLength = (payload[3] << 8) | payload[4];
        var record = payload.Slice(5, Math.Min(recordLength, payload.Length - 5));

        if (record.Length < 4 || record[0] != 0x01)
            return ("", "", "");

        int handshakeLength = (record[1] << 16) | (record[2] << 8) | record[3];
        var hello = record.Slice(4, Math.Min(handshakeLength, record.Length - 4));

        if (hello.Length < 38) return ("", "", "");

        ushort tlsVersion = (ushort)((hello[0] << 8) | hello[1]);
        int sessionIdLength = hello[34];
        int offset = 35 + sessionIdLength;

        if (offset + 2 > hello.Length) return ("", "", "");
        int cipherSuitesLength = (hello[offset] << 8) | hello[offset + 1];
        offset += 2;

        var cipherSuites = new List<ushort>();
        for (int i = 0; i < cipherSuitesLength && offset + 1 < hello.Length; i += 2)
        {
            ushort suite = (ushort)((hello[offset] << 8) | hello[offset + 1]);
            if (!GreaseValues.Contains(suite))
                cipherSuites.Add(suite);
            offset += 2;
        }

        if (offset >= hello.Length) return ("", "", "");
        int compMethodsLength = hello[offset];
        offset += 1 + compMethodsLength;

        var extensions = new List<ushort>();
        var ellipticCurves = new List<ushort>();
        var ecPointFormats = new List<byte>();
        string sni = "";

        if (offset + 2 <= hello.Length)
        {
            int extensionsLength = (hello[offset] << 8) | hello[offset + 1];
            offset += 2;
            int extensionsEnd = offset + extensionsLength;

            while (offset + 4 <= extensionsEnd && offset + 4 <= hello.Length)
            {
                ushort extType = (ushort)((hello[offset] << 8) | hello[offset + 1]);
                int extLength = (hello[offset + 2] << 8) | hello[offset + 3];
                offset += 4;

                if (!GreaseValues.Contains(extType))
                    extensions.Add(extType);

                var extData = hello.Slice(offset, Math.Min(extLength, hello.Length - offset));

                switch (extType)
                {
                    case 0x0000: // SNI
                        sni = ParseSni(extData);
                        break;
                    case 0x000a: // supported_groups (elliptic_curves)
                        ParseEllipticCurves(extData, ellipticCurves);
                        break;
                    case 0x000b: // ec_point_formats
                        ParseEcPointFormats(extData, ecPointFormats);
                        break;
                }

                offset += extLength;
            }
        }

        string ja3Raw = string.Join(",",
            tlsVersion,
            string.Join("-", cipherSuites),
            string.Join("-", extensions),
            string.Join("-", ellipticCurves),
            string.Join("-", ecPointFormats));

        string ja3Hash = ComputeMd5(ja3Raw);

        return (ja3Hash, ja3Raw, sni);
    }

    private static string ParseSni(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return "";
        int listLength = (data[0] << 8) | data[1];
        if (data.Length < 2 + listLength || data[2] != 0x00) return "";

        int nameLength = (data[3] << 8) | data[4];
        if (data.Length < 5 + nameLength) return "";

        return Encoding.ASCII.GetString(data.Slice(5, nameLength));
    }

    private static void ParseEllipticCurves(ReadOnlySpan<byte> data, List<ushort> curves)
    {
        if (data.Length < 2) return;
        int length = (data[0] << 8) | data[1];
        for (int i = 2; i + 1 < data.Length && i < 2 + length; i += 2)
        {
            ushort curve = (ushort)((data[i] << 8) | data[i + 1]);
            if (!GreaseValues.Contains(curve))
                curves.Add(curve);
        }
    }

    private static void ParseEcPointFormats(ReadOnlySpan<byte> data, List<byte> formats)
    {
        if (data.Length < 1) return;
        int length = data[0];
        for (int i = 1; i < data.Length && i <= length; i++)
            formats.Add(data[i]);
    }

    private static string ComputeMd5(string input)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}
