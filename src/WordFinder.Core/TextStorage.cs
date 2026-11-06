using System.IO.Compression;
using System.Text;

namespace WordFinder.Core;

public static class TextStorage
{
    public static string Normalize(string text) => text.ToUpperInvariant();

    public static (byte[] Payload, int Codec) Encode(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        if (raw.Length < 256) return (raw, 0);
        using var buffer = new MemoryStream();
        using (var compressor = new BrotliStream(buffer, CompressionLevel.Fastest, true))
            compressor.Write(raw);
        return buffer.Length + 12 < raw.Length ? (buffer.ToArray(), 1) : (raw, 0);
    }

    public static string Decode(byte[] payload, long codec)
    {
        if (codec == 0) return Encoding.UTF8.GetString(payload);
        if (codec != 1) throw new InvalidDataException("索引文本格式不受支持。");
        using var input = new MemoryStream(payload, false);
        using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string Tokens(string normalized, bool query = false)
    {
        var runes = normalized.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (query && runes.Length > 1)
        {
            for (var index = 1; index < runes.Length; index++)
                tokens.Add($"b{runes[index - 1]:X6}{runes[index]:X6}");
        }
        else
        {
            for (var index = 0; index < runes.Length; index++)
            {
                tokens.Add($"u{runes[index]:X6}");
                if (index > 0) tokens.Add($"b{runes[index - 1]:X6}{runes[index]:X6}");
            }
        }
        return string.Join(query ? " AND " : " ", tokens);
    }

    public static string Preview(string value, int length = 150)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "…";
    }

    public static bool Contains(string text, string normalizedTarget) =>
        Normalize(text).Contains(normalizedTarget, StringComparison.Ordinal);
}
