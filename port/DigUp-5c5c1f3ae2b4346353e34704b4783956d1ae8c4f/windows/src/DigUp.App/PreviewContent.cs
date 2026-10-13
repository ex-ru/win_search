// What Space previews for a hit, Mac Quick Look style: the thing itself,
// not the excerpt. Pure model (paths, text, moments) so it can be tested
// without a window; PreviewWindow turns it into controls.

using System.IO;
using DigUp.Core;

namespace DigUp.App;

public abstract record PreviewModel
{
    /// <summary>A picture file as-is.</summary>
    public sealed record Image(string Path) : PreviewModel;
    /// <summary>A rendered PDF page (temp file: the window deletes it).</summary>
    public sealed record TempImage(string Path) : PreviewModel;
    /// <summary>Sound or video from seconds in.</summary>
    public sealed record Media(string Path, double Seconds, bool IsVideo) : PreviewModel;
    /// <summary>PDF document: initial page, total pages, rendered initial PNG.</summary>
    public sealed record Pdf(string Path, int InitialPage, int TotalPages, string InitialImage) : PreviewModel;
    /// <summary>Words: code around the match, a text file, or an excerpt.
    /// Highlight marks the relevant passage (offset/length, -1 when none).</summary>
    public sealed record Text(string Body, int Highlight = -1, int HighlightLength = 0) : PreviewModel;
    public sealed record None(string Note) : PreviewModel;
}

public static class PreviewContent
{
    private const long MaxTextBytes = 8 * 1024 * 1024;
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".ini", ".cfg", ".sql", ".bat", ".ps1", ".sh"];

    /// <summary>The preview for <paramref name="hit"/>: pictures as pictures,
    /// PDFs as the matched page rendered, sound/video from their moment,
    /// code around the matched lines, small text files whole. The words
    /// carry the relevant passage marked (from <paramref name="query"/>).
    /// Never throws and never returns null; missing files fall back to
    /// the excerpt.</summary>
    public static PreviewModel For(Engine.Hit hit, string pdfiumDll, string query = "")
    {
        string path = hit.Path.Replace('/', Path.DirectorySeparatorChar);
        List<string> tokens = Tokens(query);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (File.Exists(path))
            {
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif" or ".tiff" or ".tif")
                    return new PreviewModel.Image(path);
                if (ext == ".pdf")
                    return PdfPage(hit, path, pdfiumDll, tokens);
            }

            return hit.Kind switch
            {
                "Image" or "Screenshot" when File.Exists(path) => new PreviewModel.Image(path),
                "Video" when File.Exists(path) =>
                    new PreviewModel.Media(path, MainWindow.ParseSeconds(hit.Where), true),
                "Audio" when File.Exists(path) =>
                    new PreviewModel.Media(path, MainWindow.ParseSeconds(hit.Where), false),
                "Pdf" when File.Exists(path) => PdfPage(hit, path, pdfiumDll, tokens),
                "Code" when File.Exists(path) => CodeAt(hit, path, tokens),
                _ => Words(hit, path, tokens),
            };
        }
        catch
        {
            return Fallback(hit, tokens);
        }
    }

    /// <summary>Query words to jump to. The "code:" prefix isn't one.</summary>
    private static List<string> Tokens(string query)
    {
        try { return SearchText.Tokenize(CodeQuery.TextOf(query.Trim()) ?? query); }
        catch { return []; }
    }

    /// <summary>First query word in the body (case-insensitive): the relevant
    /// passage. (-1, 0) when none matches.</summary>
    private static (int At, int Length) Passage(string body, List<string> tokens)
    {
        int at = -1, length = 0;
        foreach (string token in tokens)
        {
            int i = body.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (at < 0 || i < at)) { at = i; length = token.Length; }
        }
        return (at, length);
    }

    private static PreviewModel PdfPage(Engine.Hit hit, string path, string pdfiumDll, List<string> tokens)
    {
        try
        {
            int page = (int)(MainWindow.ParsePage(hit.Where) ?? 1);
            using var engine = new PdfiumEngine(pdfiumDll);
            using var doc = engine.Open(path);
            int total = Math.Max(1, doc.PageCount);
            int targetPage = Math.Clamp(page, 1, total);
            string png = Path.Combine(Path.GetTempPath(), "digup-prev-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                doc.RenderPageToPng(targetPage - 1, 1600, png);
            }
            catch
            {
                doc.RenderPageToPng(0, 1600, png);
            }
            return new PreviewModel.Pdf(path, targetPage, total, png);
        }
        catch
        {
            return Fallback(hit, tokens);
        }
    }

    private static PreviewModel CodeAt(Engine.Hit hit, string path, List<string> tokens)
    {
        try
        {
            int line = MainWindow.ParseLine(hit.Where);
            string[] lines = EncodingHelper.ReadAllLinesAuto(path);
            int from = Math.Max(1, line - 40), to = Math.Min(lines.Length, line + 40);
            var sb = new System.Text.StringBuilder();
            for (int i = from; i <= to; i++)
                sb.Append(i).Append(": ").AppendLine(lines[i - 1]);
            string body = EncodingHelper.FixMojibake(sb.ToString());
            var (at, length) = Passage(body, tokens);
            return new PreviewModel.Text(body, at, length);
        }
        catch
        {
            return Fallback(hit, tokens);
        }
    }

    private static PreviewModel Words(Engine.Hit hit, string path, List<string> tokens)
    {
        try
        {
            if (File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".docx")
                {
                    string body = DocExtractor.ExtractDocxText(path);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        body = EncodingHelper.FixMojibake(body);
                        var (at, length) = Passage(body, tokens);
                        return new PreviewModel.Text(body, at, length);
                    }
                }
                else if (TextExtensions.Contains(ext) && new FileInfo(path).Length <= MaxTextBytes)
                {
                    string body = EncodingHelper.FixMojibake(EncodingHelper.ReadTextAuto(path, (int)MaxTextBytes));
                    var (at, length) = Passage(body, tokens);
                    return new PreviewModel.Text(body, at, length);
                }
            }
        }
        catch { }
        return Fallback(hit, tokens);
    }

    private static PreviewModel Fallback(Engine.Hit hit, List<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(hit.Excerpt))
            return new PreviewModel.None("(no words — found by what it shows or says)");
        string body = EncodingHelper.FixMojibake(hit.Excerpt);
        var (at, length) = Passage(body, tokens);
        return new PreviewModel.Text(body, at, length);
    }
}
