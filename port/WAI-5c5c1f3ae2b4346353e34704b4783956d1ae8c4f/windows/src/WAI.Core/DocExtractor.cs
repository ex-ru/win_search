// Document text extraction and chunking. Ports DocExtractor
// (Extractors.swift:161-246): plain text from txt/md directly, HTML by
// stripping tags, rich formats through a converter.
//
// Windows mapping: Cocoa's text system (rtf/docx/doc/odt) becomes
// DocumentFormat.OpenXml for docx plus a conversion step for legacy/odt/rtf.
// Only an RTF fallback is implemented here; docx/doc/odt throw a clear
// ExtractError until the converter is wired, instead of indexing zip garbage.

namespace WAI.Core;

public static class DocExtractor
{
    /// <summary>Plain text of a document file. Throws ExtractError when unreadable.</summary>
    public static string Text(string path)
    {
        string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "html" or "htm" => PlainTextFromHtml(ReadText(path)),
            "rtf" => PlainTextFromRtf(ReadText(path)),
            "docx" => ExtractDocxText(path),
            "doc" or "odt" => throw ExtractError.Unreadable(
                $"{ext} converter not wired yet (needs external converter or LibreOffice)"),
            _ => ReadText(path),
        };
    }

    /// <summary>
    /// Reads plain text from a Word .docx package (word/document.xml) without external libraries.
    /// </summary>
    public static string ExtractDocxText(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return string.Empty;

            using var stream = entry.Open();
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            var doc = System.Xml.Linq.XDocument.Load(reader);

            var sb = new System.Text.StringBuilder();
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var texts = p.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value);
                string pText = string.Concat(texts);
                if (!string.IsNullOrWhiteSpace(pText))
                {
                    sb.AppendLine(pText.Trim());
                }
            }
            return sb.ToString();
        }
        catch (System.Exception ex)
        {
            throw ExtractError.Unreadable($"failed to read docx: {ex.Message}");
        }
    }

    internal static string ReadText(string path)
    {
        try
        {
            return EncodingHelper.ReadTextAuto(path);
        }
        catch (System.Exception error) when (error is System.IO.IOException || error is System.UnauthorizedAccessException)
        {
            throw ExtractError.Unreadable(error.Message);
        }
    }

    /// <summary>
    /// Good enough for search: drops scripts, styles, head and comments, turns
    /// block ends into line breaks, strips tags, decodes entities.
    /// Ports plainText(fromHTML:) (Extractors.swift:186).
    /// </summary>
    public static string PlainTextFromHtml(string html)
    {
        string text = html;
        foreach (string pattern in new[]
                 {
                     @"(?is)<script\b.*?</script\s*>",
                     @"(?is)<style\b.*?</style\s*>",
                     @"(?s)<!--.*?-->",
                     @"(?is)<head\b.*?</head\s*>",
                 })
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, " ");
        }

        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?i)<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)\b[^>]*>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ");

        foreach (var (entity, character) in new (string, string)[]
                 {
                     ("&nbsp;", " "), ("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"),
                     ("&quot;", "\""), ("&#39;", "'"), ("&apos;", "'"),
                     ("&mdash;", "—"), ("&ndash;", "–"), ("&hellip;", "…"),
                 })
        {
            text = text.Replace(entity, character);
        }

        text = DecodeNumericEntities(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\n\s*", "\n");
        return text.Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex NumericEntity =
        new(@"&#(x[0-9a-fA-F]+|[0-9]+);", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string DecodeNumericEntities(string text)
    {
        if (!text.Contains("&#"))
        {
            return text;
        }

        return NumericEntity.Replace(text, match =>
        {
            string code = match.Groups[1].Value;
            uint value = code.StartsWith("x", System.StringComparison.OrdinalIgnoreCase)
                ? System.Convert.ToUInt32(code.Substring(1), 16)
                : System.Convert.ToUInt32(code, 10);
            try
            {
                return char.ConvertFromUtf32((int)value);
            }
            catch
            {
                return " ";
            }
        });
    }

    /// <summary>
    /// Extracts plain text from RTF content:
    /// - Strips metadata, font, color, and picture hex-dump groups (pict, object, fonttbl, etc.)
    /// - Decodes Windows-1251 Cyrillic hex escapes (\'hh) and unicode (\uN)
    /// - Preserves paragraph line breaks and tabs
    /// - Cleans mojibake and redundant whitespace
    /// </summary>
    public static string PlainTextFromRtf(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return string.Empty;

        // 1. Detect code page from \ansicpgN (default to 1251 for Cyrillic / Russian documents)
        int codePage = 1251;
        var cpgMatch = System.Text.RegularExpressions.Regex.Match(rtf, @"\\ansicpg(\d+)");
        if (cpgMatch.Success && int.TryParse(cpgMatch.Groups[1].Value, out int cp))
        {
            codePage = cp;
        }

        System.Text.Encoding docEncoding;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            docEncoding = System.Text.Encoding.GetEncoding(codePage);
        }
        catch
        {
            docEncoding = System.Text.Encoding.GetEncoding(1251);
        }

        var sb = new System.Text.StringBuilder();
        var hexBytes = new System.Collections.Generic.List<byte>();

        void FlushHex()
        {
            if (hexBytes.Count > 0)
            {
                sb.Append(docEncoding.GetString(hexBytes.ToArray()));
                hexBytes.Clear();
            }
        }

        int len = rtf.Length;
        int i = 0;
        int depth = 0;
        int skipUntilDepth = -1;
        int ucSkip = 1;

        while (i < len)
        {
            char c = rtf[i];

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                FlushHex();
                if (skipUntilDepth >= 0 && depth <= skipUntilDepth)
                {
                    skipUntilDepth = -1;
                }
                depth--;
                i++;
                continue;
            }

            if (skipUntilDepth >= 0)
            {
                // Inside an ignored destination group (e.g. pict, fonttbl, stylesheet, object)
                i++;
                continue;
            }

            if (c == '\\')
            {
                i++;
                if (i >= len) break;
                char next = rtf[i];

                if (next is '{' or '}' or '\\')
                {
                    FlushHex();
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (next == '~')
                {
                    FlushHex();
                    sb.Append(' ');
                    i++;
                    continue;
                }

                if (next == '_')
                {
                    FlushHex();
                    sb.Append('-');
                    i++;
                    continue;
                }

                if (next == '\'')
                {
                    // Hex byte escape: \'hh
                    i++;
                    if (i + 1 < len)
                    {
                        string hex = rtf.Substring(i, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                        {
                            hexBytes.Add(b);
                            i += 2;
                            continue;
                        }
                    }
                    continue;
                }

                if (next == '*')
                {
                    // Ignorable destination group {\*...}
                    skipUntilDepth = depth;
                    i++;
                    continue;
                }

                // Control word: letters followed by optional signed integer
                int wordStart = i;
                while (i < len && char.IsLetter(rtf[i])) i++;
                string word = rtf.Substring(wordStart, i - wordStart);

                int paramStart = i;
                if (i < len && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                {
                    i++;
                    while (i < len && char.IsDigit(rtf[i])) i++;
                }
                string paramStr = rtf.Substring(paramStart, i - paramStart);

                // Optional delimiter space
                if (i < len && rtf[i] == ' ') i++;

                FlushHex();

                // Ignorable destination groups
                if (word is "pict" or "object" or "fonttbl" or "colortbl" or "stylesheet" or "info"
                    or "xmlnstbl" or "themedata" or "datastore" or "filetbl" or "generator" or "header" or "footer")
                {
                    skipUntilDepth = depth;
                    continue;
                }

                if (word is "par" or "line" or "row")
                {
                    sb.Append('\n');
                }
                else if (word is "tab")
                {
                    sb.Append('\t');
                }
                else if (word == "u" && int.TryParse(paramStr, out int uVal))
                {
                    if (uVal < 0) uVal += 65536;
                    try
                    {
                        sb.Append((char)uVal);
                    }
                    catch { }

                    // Skip uc fallback characters
                    for (int skip = 0; skip < ucSkip && i < len; skip++)
                    {
                        if (rtf[i] == '\\' && i + 3 < len && rtf[i + 1] == '\'')
                        {
                            i += 4;
                        }
                        else
                        {
                            i++;
                        }
                    }
                }
                else if (word == "uc" && int.TryParse(paramStr, out int ucVal))
                {
                    ucSkip = Math.Max(0, ucVal);
                }

                continue;
            }

            // Normal text character
            if (c is '\r' or '\n')
            {
                FlushHex();
                i++;
                continue;
            }

            FlushHex();
            sb.Append(c);
            i++;
        }

        FlushHex();

        string text = sb.ToString();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t\u00A0]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n");
        text = text.Trim();

        return EncodingHelper.FixMojibake(text);
    }

    /// <summary>
    /// Splits text into ~<paramref name="size"/>-character pieces (~450 tokens)
    /// that overlap a little, breaking at whitespace. Ports chunks
    /// (Extractors.swift:222): size 1800, overlap 200.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> Chunks(
        string text, int size = 1800, int overlap = 200, int limit = int.MaxValue)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t ]+", " ");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\n\s*\n\s*\n+", "\n\n");
        clean = clean.Trim();
        var chunks = new System.Collections.Generic.List<string>();
        int start = 0;
        while (start < clean.Length && chunks.Count < limit)
        {
            int end = System.Math.Min(start + size, clean.Length);
            if (end < clean.Length)
            {
                int space = clean.LastIndexOfAny(new[] { ' ', '\t', '\n', '\r' }, end - 1, end - start);
                if (space > start && space - start > size / 2)
                {
                    end = space;
                }
            }

            string chunk = clean.Substring(start, end - start).Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= clean.Length)
            {
                break;
            }

            int next = System.Math.Max(start, end - overlap);
            if (next <= start)
            {
                next = end;
            }

            while (next < end && !char.IsWhiteSpace(clean[next]))
            {
                next++;
            }

            start = next < end ? next : end;
        }

        return chunks;
    }
}
