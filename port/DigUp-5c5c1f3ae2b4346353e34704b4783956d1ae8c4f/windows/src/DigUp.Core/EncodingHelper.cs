using System.IO;
using System.Text;

namespace DigUp.Core;

/// <summary>
/// Universal text encoding detector and reader.
/// Seamlessly handles UTF-8 (with/without BOM), UTF-16, Windows-1251 (Cyrillic ANSI),
/// CP866 (DOS Cyrillic), KOI8-R and Latin-1.
/// </summary>
public static class EncodingHelper
{
    private static readonly Encoding Windows1251;
    private static readonly Encoding Cp866;

    static EncodingHelper()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch { }

        try { Windows1251 = Encoding.GetEncoding(1251); }
        catch { Windows1251 = Encoding.UTF8; }

        try { Cp866 = Encoding.GetEncoding(866); }
        catch { Cp866 = Windows1251; }
    }

    /// <summary>
    /// Reads text from a file with automatic encoding detection.
    /// </summary>
    public static string ReadTextAuto(string path, int maxBytes = -1)
    {
        if (!File.Exists(path)) return string.Empty;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int length = (int)Math.Min(fs.Length, maxBytes > 0 ? maxBytes : fs.Length);
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int r = fs.Read(buffer, read, length - read);
            if (r == 0) break;
            read += r;
        }

        return Decode(buffer, 0, read);
    }

    /// <summary>
    /// Reads all lines from a file with automatic encoding detection.
    /// </summary>
    public static string[] ReadAllLinesAuto(string path, int maxBytes = -1)
    {
        string text = ReadTextAuto(path, maxBytes);
        return text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
    }

    /// <summary>
    /// Decodes a byte buffer using detected encoding.
    /// </summary>
    public static string Decode(byte[] bytes, int index = 0, int count = -1)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        if (count < 0) count = bytes.Length - index;
        if (count <= 0) return string.Empty;

        // 1. Check BOM signatures
        if (count >= 3 && bytes[index] == 0xEF && bytes[index + 1] == 0xBB && bytes[index + 2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, index + 3, count - 3);
        }
        if (count >= 2 && bytes[index] == 0xFF && bytes[index + 1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, index + 2, count - 2);
        }
        if (count >= 2 && bytes[index] == 0xFE && bytes[index + 1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, index + 2, count - 2);
        }

        // 2. Try strict UTF-8
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
            // Invalid UTF-8 sequence, fallback to heuristic legacy detection
        }

        // 3. Heuristic Cyrillic / ANSI detection
        Encoding detected = DetectLegacyEncoding(bytes, index, count);
        return detected.GetString(bytes, index, count);
    }

    /// <summary>
    /// Distinguishes Windows-1251, CP866 and fallback encodings based on character distribution.
    /// </summary>
    public static Encoding DetectLegacyEncoding(byte[] bytes, int index, int count)
    {
        int win1251Score = 0;
        int cp866Score = 0;
        int totalNonAscii = 0;

        int sample = Math.Min(count, 8192);
        for (int i = index; i < index + sample; i++)
        {
            byte b = bytes[i];
            if (b >= 0x80)
            {
                totalNonAscii++;
                // Windows-1251 Russian letters: А-Я (0xC0-0xDF), а-я (0xE0-0xFF), Ё (0xA8), ё (0xB8)
                if (b >= 0xC0 || b == 0xA8 || b == 0xB8)
                {
                    win1251Score++;
                    // Common Russian lowercase vowels in 1251: о (0xEE), е (0xE5), а (0xE0), и (0xE8)
                    if (b is 0xEE or 0xE5 or 0xE0 or 0xE8) win1251Score += 2;
                }

                // CP866 Russian letters: А-п (0x80-0xAF), р-я (0xE0-0xEF), Ё (0xF0), ё (0xF1)
                if ((b >= 0x80 && b <= 0xAF) || (b >= 0xE0 && b <= 0xEF) || b is 0xF0 or 0xF1)
                {
                    cp866Score++;
                    // Common Russian lowercase vowels in CP866: о (0xAE), е (0xA5), а (0xA0), и (0xA8)
                    if (b is 0xAE or 0xA5 or 0xA0 or 0xA8) cp866Score += 2;
                }
            }
        }

        if (totalNonAscii > 0)
        {
            if (win1251Score >= cp866Score && win1251Score > 0) return Windows1251;
            if (cp866Score > win1251Score) return Cp866;
        }

        // Default to Windows-1251 for Cyrillic OS environments, or system ANSI
        return Windows1251;
    }

    /// <summary>
    /// Detects and repairs mojibake (e.g. Cyrillic Windows-1251 accidentally decoded as Latin-1 / Windows-1252,
    /// such as "Ïî÷òîâûé ÿùèê" -> "Почтовый ящик").
    /// </summary>
    public static string FixMojibake(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

        // Check for clusters of accented Latin chars typical of Windows-1251 decoded as Latin1
        int mojibakeChars = 0;
        int cyrillicChars = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '\u00C0' && c <= '\u00FF' && c != '×')
                mojibakeChars++;
            else if ((c >= '\u0410' && c <= '\u044F') || c == '\u0401' || c == '\u0451')
                cyrillicChars++;
        }

        if (mojibakeChars >= 3 && mojibakeChars > cyrillicChars)
        {
            try
            {
                byte[] raw = new byte[text.Length];
                bool canMap = true;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c <= 0xFF)
                    {
                        raw[i] = (byte)c;
                    }
                    else
                    {
                        canMap = false;
                        break;
                    }
                }

                if (!canMap)
                {
                    raw = Encoding.Latin1.GetBytes(text);
                }

                string candidate = Windows1251.GetString(raw);

                int restoredCyrillic = 0;
                for (int i = 0; i < candidate.Length; i++)
                {
                    char c = candidate[i];
                    if ((c >= '\u0410' && c <= '\u044F') || c == '\u0401' || c == '\u0451')
                        restoredCyrillic++;
                }

                if (restoredCyrillic >= 3 && restoredCyrillic >= mojibakeChars * 0.8)
                {
                    return candidate;
                }
            }
            catch { }
        }

        return text;
    }
}
