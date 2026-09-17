using System;
using System.Text;

namespace RetailPOS.Application.Security;

public static class CrockfordBase32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        var sb = new StringBuilder();
        int bitBuffer = 0;
        int bitCount = 0;

        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;

            while (bitCount >= 5)
            {
                bitCount -= 5;
                int index = (bitBuffer >> bitCount) & 0x1F;
                sb.Append(Alphabet[index]);
            }
        }

        if (bitCount > 0)
        {
            int index = (bitBuffer << (5 - bitCount)) & 0x1F;
            sb.Append(Alphabet[index]);
        }

        return sb.ToString();
    }

    public static byte[] Decode(string input)
    {
        string norm = Normalize(input);
        if (string.IsNullOrEmpty(norm)) return Array.Empty<byte>();

        var bitBuffer = 0;
        var bitCount = 0;
        var output = new System.Collections.Generic.List<byte>();

        foreach (char c in norm)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid Crockford Base32 character: {c}");

            bitBuffer = (bitBuffer << 5) | val;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                output.Add((byte)((bitBuffer >> bitCount) & 0xFF));
            }
        }

        return output.ToArray();
    }

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in input)
        {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            char up = char.ToUpperInvariant(c);
            if (up == 'O') up = '0';
            else if (up == 'I' || up == 'L') up = '1';
            sb.Append(up);
        }
        return sb.ToString();
    }

    public static string FormatGroups(string input, int groupSize = 4)
    {
        string norm = Normalize(input);
        if (norm.Length <= groupSize) return norm;

        var sb = new StringBuilder();
        for (int i = 0; i < norm.Length; i++)
        {
            if (i > 0 && i % groupSize == 0) sb.Append('-');
            sb.Append(norm[i]);
        }
        return sb.ToString();
    }
}
