// MCP (Model Context Protocol) Server implementation for WAI.
// Enables AI Agents (Claude Desktop, Cursor, Antigravity, OpenCode, VS Code, etc.)
// to search and read indexed documents in strict Read-Only mode over standard JSON-RPC 2.0 (stdio).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WAI.Core;

/// <summary>
/// Model Context Protocol (MCP) server for local RAG and document exploration.
/// Provides strictly read-only access to indexed files and SQLite vector/FTS5 indexes.
/// </summary>
public sealed class McpServer : IDisposable
{
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "wai-mcp";
    public const string ServerVersion = "1.0.5";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly string _indexDir;
    private readonly Func<string, bool, IReadOnlyList<float>?>? _queryEmbedder;
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public McpServer(
        TextReader input,
        TextWriter output,
        string indexDir,
        Func<string, bool, IReadOnlyList<float>?>? queryEmbedder = null)
    {
        _input = input;
        _output = output;
        _indexDir = indexDir;
        _queryEmbedder = queryEmbedder;
    }

    /// <summary>
    /// Runs the JSON-RPC message pump on the input stream until EOF or disposal.
    /// </summary>
    public void Run()
    {
        while (!_disposed)
        {
            string? line = _input.ReadLine();
            if (line is null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            ProcessMessage(line);
        }
    }

    /// <summary>
    /// Processes a single JSON-RPC line. Public to allow unit tests without infinite loop.
    /// </summary>
    public void ProcessMessage(string line)
    {
        line = line.TrimStart('\uFEFF').Trim();
        if (string.IsNullOrWhiteSpace(line)) return;

        JsonRpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOptions);
        }
        catch (Exception ex)
        {
            SendError(null, -32700, $"Parse error: {ex.Message}");
            return;
        }

        if (req is null)
        {
            SendError(null, -32600, "Invalid Request");
            return;
        }

        // Notification (no id)
        if (req.Id is null)
        {
            HandleNotification(req);
            return;
        }

        try
        {
            switch (req.Method)
            {
                case "initialize":
                    HandleInitialize(req);
                    break;
                case "ping":
                    SendResult(req.Id, new { });
                    break;
                case "tools/list":
                    HandleToolsList(req);
                    break;
                case "tools/call":
                    HandleToolsCall(req);
                    break;
                default:
                    SendError(req.Id, -32601, $"Method not found: {req.Method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(req.Id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private void HandleNotification(JsonRpcRequest req)
    {
        // Handled notifications: "notifications/initialized", "cancelled", etc.
        // No response is sent for notifications in JSON-RPC.
    }

    private void HandleInitialize(JsonRpcRequest req)
    {
        var result = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = ServerName,
                version = ServerVersion
            }
        };
        SendResult(req.Id, result);
    }

    private void HandleToolsList(JsonRpcRequest req)
    {
        var tools = new object[]
        {
            new
            {
                name = "search_documents",
                description = "Searches indexed personal documents, notes, PDFs, and code using hybrid semantic (vector) and keyword (FTS5) search strictly in Read-Only mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "The natural language search question or keywords. Prefix with 'code:' to search code index only." },
                        ["limit"] = new { type = "integer", description = "Max number of matches to return (default: 5, min: 1, max: 50)." },
                        ["kind"] = new { type = "string", description = "Optional document category filter: 'pdf', 'image', 'screenshot', 'doc', 'code', 'video', 'audio'." }
                    },
                    required = new[] { "query" }
                }
            },
            new
            {
                name = "read_document_content",
                description = "Safely reads the text content or code snippet of a local file strictly in Read-Only mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Absolute file path on local disk." },
                        ["start_line"] = new { type = "integer", description = "Starting line number (1-based, default: 1)." },
                        ["max_lines"] = new { type = "integer", description = "Maximum number of lines to return (default: 200, max: 2000)." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "get_document_info",
                description = "Returns metadata and indexed segment summary for a specific file from the WAI index.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Absolute file path on local disk." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "get_index_status",
                description = "Returns current WAI RAG index statistics, total indexed documents, vector status, and database path.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>()
                }
            }
        };

        SendResult(req.Id, new { tools });
    }

    private void HandleToolsCall(JsonRpcRequest req)
    {
        if (req.Params is null)
        {
            SendError(req.Id, -32602, "Invalid params: missing parameters object");
            return;
        }

        string toolName = "";
        JsonElement args = default;

        if (req.Params.Value.TryGetProperty("name", out var nameProp))
            toolName = nameProp.GetString() ?? "";

        if (req.Params.Value.TryGetProperty("arguments", out var argsProp))
            args = argsProp;

        switch (toolName)
        {
            case "search_documents":
                ExecuteSearch(req.Id, args);
                break;
            case "read_document_content":
                ExecuteReadContent(req.Id, args);
                break;
            case "get_document_info":
                ExecuteGetDocInfo(req.Id, args);
                break;
            case "get_index_status":
                ExecuteGetStatus(req.Id);
                break;
            default:
                SendToolError(req.Id, $"Unknown tool: {toolName}");
                break;
        }
    }

    private void ExecuteSearch(JsonElement? id, JsonElement args)
    {
        string query = "";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var qProp))
            query = qProp.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(query))
        {
            SendToolError(id, "Parameter 'query' cannot be empty.");
            return;
        }

        int limit = 5;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var limProp) && limProp.TryGetInt32(out int l))
            limit = Math.Clamp(l, 1, 50);

        string? kindFilter = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("kind", out var kProp))
            kindFilter = kProp.GetString();

        HashSet<FileKind>? kinds = null;
        if (!string.IsNullOrEmpty(kindFilter))
        {
            kinds = new HashSet<FileKind> { FileKindStrings.Parse(kindFilter) };
        }

        string mainDb = Path.Combine(_indexDir, IndexStore.MainFile);
        string codeDb = Path.Combine(_indexDir, IndexStore.CodeFile);

        bool isCodeQuery = CodeQuery.TextOf(query) is not null || kindFilter is "code";
        string targetDb = isCodeQuery && File.Exists(codeDb) ? codeDb : mainDb;

        if (!File.Exists(targetDb))
        {
            SendToolResult(id, $"Index file not found at '{targetDb}'. Run 'wai index <folder>' first to build the index.");
            return;
        }

        try
        {
            using var store = new IndexStore(_indexDir, Path.GetFileName(targetDb));
            using var searcher = new Searcher(store, loadVectors: false);

            string searchQuery = isCodeQuery ? (CodeQuery.TextOf(query) ?? query) : query;
            IReadOnlyList<float>? vec = _queryEmbedder?.Invoke(searchQuery, isCodeQuery);

            var hits = searcher.Search(searchQuery, vec, kinds, limit: limit);

            if (hits.Count == 0 && !isCodeQuery && File.Exists(codeDb))
            {
                // Fallback to code search if main index returned 0 hits
                using var codeStore = new IndexStore(_indexDir, IndexStore.CodeFile);
                using var codeSearcher = new Searcher(codeStore, loadVectors: false);
                hits = codeSearcher.Search(searchQuery, vec, limit: limit);
            }

            if (hits.Count == 0)
            {
                SendToolResult(id, $"No documents matched query: \"{query}\"");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {hits.Count} matching document(s) for \"{query}\":\n");

            for (int i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                string loc = h.Loc is not null ? (h.Segment is SegmentKind.Lines or SegmentKind.Cells
                    ? $" [lines {h.Loc}-{h.LocEnd}]" : $" [page {h.Loc}]") : "";

                sb.AppendLine($"### [{i + 1}] {Path.GetFileName(h.Path)}{loc}");
                sb.AppendLine($"- **Path:** `{h.Path}`");
                sb.AppendLine($"- **Type:** {h.Kind} | **Score:** {h.Score:F3}");
                if (!string.IsNullOrWhiteSpace(h.Excerpt))
                {
                    sb.AppendLine($"- **Excerpt:**");
                    sb.AppendLine($"  > {h.Excerpt.Trim().Replace("\r", "").Replace("\n", " ")}");
                }
                sb.AppendLine();
            }

            SendToolResult(id, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            SendToolError(id, $"Search failed: {ex.Message}");
        }
    }

    private void ExecuteReadContent(JsonElement? id, JsonElement args)
    {
        string path = "";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var pProp))
            path = pProp.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(path))
        {
            SendToolError(id, "Parameter 'path' is required.");
            return;
        }

        path = Path.GetFullPath(path);

        // Strict read-only safety guardrails against critical system hives
        string lower = path.ToLowerInvariant();
        if (lower.Contains(@"windows\system32\config\sam") ||
            lower.Contains(@"windows\system32\config\system") ||
            lower.Contains(@"windows\system32\config\security"))
        {
            SendToolError(id, "Access denied: Reading system security hives is restricted.");
            return;
        }

        if (!File.Exists(path))
        {
            SendToolError(id, $"File not found: '{path}'");
            return;
        }

        int startLine = 1;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("start_line", out var slProp) && slProp.TryGetInt32(out int sl))
            startLine = Math.Max(1, sl);

        int maxLines = 200;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("max_lines", out var mlProp) && mlProp.TryGetInt32(out int ml))
            maxLines = Math.Clamp(ml, 1, 2000);

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // PDF Handling: if PDF, try reading indexed text segments or raw note
        if (ext == ".pdf")
        {
            string pdfText = ReadIndexedPdfText(path, maxLines);
            if (!string.IsNullOrEmpty(pdfText))
            {
                SendToolResult(id, $"### Document: {Path.GetFileName(path)} (PDF extracted text)\n\n{pdfText}");
                return;
            }
        }

        // Binary files check (executables, audio, raw video)
        if (ext is ".exe" or ".dll" or ".bin" or ".obj" or ".iso" or ".mp3" or ".wav" or ".mp4" or ".mkv")
        {
            var fi = new FileInfo(path);
            SendToolResult(id, $"File '{Path.GetFileName(path)}' is a binary format ({ext}, {fi.Length:N0} bytes). Plain text view is not supported; use 'search_documents' to inspect indexed OCR/metadata transcripts.");
            return;
        }

        try
        {
            string[] allLines = EncodingHelper.ReadAllLinesAuto(path);
            int total = allLines.Length;
            int fromIdx = Math.Max(0, startLine - 1);
            int count = Math.Min(maxLines, Math.Max(0, total - fromIdx));
            int actualEnd = fromIdx + count;

            var sb = new StringBuilder();
            sb.AppendLine($"### Content: {Path.GetFileName(path)} (lines {fromIdx + 1}..{actualEnd} of {total})");
            sb.AppendLine("```");
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(allLines[fromIdx + i]);
            }
            sb.AppendLine("```");

            if (actualEnd < total)
            {
                sb.AppendLine($"*(truncated at line {actualEnd}; call again with start_line={actualEnd + 1} to continue reading)*");
            }

            SendToolResult(id, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            SendToolError(id, $"Failed to read file: {ex.Message}");
        }
    }

    private string ReadIndexedPdfText(string path, int maxLines)
    {
        try
        {
            string mainDb = Path.Combine(_indexDir, IndexStore.MainFile);
            if (!File.Exists(mainDb)) return "";

            using var store = new IndexStore(_indexDir);
            var segments = new List<string>();

            store.Db.Query(
                "SELECT s.loc, s.excerpt, fts.body " +
                "FROM files f " +
                "JOIN segments s ON s.file = f.id " +
                "LEFT JOIN segments_fts fts ON fts.rowid = s.id " +
                "WHERE f.path = ? ORDER BY s.loc ASC LIMIT ?",
                new[] { SqlValue.Text(path), SqlValue.Int(maxLines) },
                r =>
                {
                    double? loc = r.IsDBNull(0) ? null : r.GetDouble(0);
                    string? excerpt = r.NullableText(1);
                    string? body = r.NullableText(2);
                    string text = !string.IsNullOrWhiteSpace(body) ? body : (excerpt ?? "");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string header = loc is not null ? $"[Page {loc}]: " : "";
                        segments.Add(header + text.Trim());
                    }
                });

            return string.Join("\n\n", segments);
        }
        catch
        {
            return "";
        }
    }

    private void ExecuteGetDocInfo(JsonElement? id, JsonElement args)
    {
        string path = "";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var pProp))
            path = pProp.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(path))
        {
            SendToolError(id, "Parameter 'path' is required.");
            return;
        }

        path = Path.GetFullPath(path);
        bool exists = File.Exists(path);
        long size = exists ? new FileInfo(path).Length : 0;
        DateTime mtime = exists ? File.GetLastWriteTime(path) : DateTime.MinValue;

        int segmentsCount = 0;
        string kind = "Unknown";
        string state = exists ? "on-disk" : "missing";

        string mainDb = Path.Combine(_indexDir, IndexStore.MainFile);
        if (File.Exists(mainDb))
        {
            try
            {
                using var store = new IndexStore(_indexDir);
                store.Db.Query(
                    "SELECT f.kind, f.state, COUNT(s.id) " +
                    "FROM files f LEFT JOIN segments s ON s.file = f.id " +
                    "WHERE f.path = ? GROUP BY f.id",
                    new[] { SqlValue.Text(path) },
                    r =>
                    {
                        kind = r.NullableText(0) ?? kind;
                        state = r.NullableText(1) ?? state;
                        segmentsCount = (int)r.GetInt64(2);
                    });
            }
            catch { }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### Metadata: {Path.GetFileName(path)}");
        sb.AppendLine($"- **Path:** `{path}`");
        sb.AppendLine($"- **Status on disk:** {(exists ? "Exists" : "Not Found")}");
        sb.AppendLine($"- **Size:** {size:N0} bytes");
        sb.AppendLine($"- **Modified:** {mtime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Indexed Kind:** {kind}");
        sb.AppendLine($"- **Indexed State:** {state}");
        sb.AppendLine($"- **Indexed Segments:** {segmentsCount}");

        SendToolResult(id, sb.ToString().TrimEnd());
    }

    private void ExecuteGetStatus(JsonElement? id)
    {
        string mainDb = Path.Combine(_indexDir, IndexStore.MainFile);
        string codeDb = Path.Combine(_indexDir, IndexStore.CodeFile);

        bool mainExists = File.Exists(mainDb);
        bool codeExists = File.Exists(codeDb);

        int mainSegments = 0, mainVectors = 0, codeSegments = 0, codeVectors = 0;
        string vectorSource = "None";

        if (mainExists)
        {
            try
            {
                using var store = new IndexStore(_indexDir);
                var c = store.Counts();
                mainSegments = c.Segments;
                mainVectors = c.Vectors;
                vectorSource = store.Meta("vector_source") ?? "Words-only (EmbeddingGemma not loaded)";
            }
            catch { }
        }

        if (codeExists)
        {
            try
            {
                using var store = new IndexStore(_indexDir, IndexStore.CodeFile);
                var c = store.Counts();
                codeSegments = c.Segments;
                codeVectors = c.Vectors;
            }
            catch { }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### WAI RAG Index Status");
        sb.AppendLine($"- **Index Directory:** `{_indexDir}`");
        sb.AppendLine($"- **Main Index:** {(mainExists ? $"Active ({mainSegments} segments, {mainVectors} vectors)" : "Not Created")}");
        sb.AppendLine($"- **Code Index:** {(codeExists ? $"Active ({codeSegments} segments, {codeVectors} vectors)" : "Disabled / Empty")}");
        sb.AppendLine($"- **Vector Runtime:** `{vectorSource}`");
        sb.AppendLine($"- **Access Mode:** Strict Read-Only");

        SendToolResult(id, sb.ToString().TrimEnd());
    }

    private void SendResult(JsonElement? id, object result)
    {
        var resp = new JsonRpcResponse
        {
            Id = id,
            Result = result
        };
        SendResponse(resp);
    }

    private void SendToolResult(JsonElement? id, string text)
    {
        var result = new
        {
            content = new object[]
            {
                new { type = "text", text }
            },
            isError = false
        };
        SendResult(id, result);
    }

    private void SendToolError(JsonElement? id, string message)
    {
        var result = new
        {
            content = new object[]
            {
                new { type = "text", text = $"Error: {message}" }
            },
            isError = true
        };
        SendResult(id, result);
    }

    private void SendError(JsonElement? id, int code, string message)
    {
        var resp = new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message }
        };
        SendResponse(resp);
    }

    private void SendResponse(JsonRpcResponse resp)
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(resp, JsonOptions);
            _output.WriteLine(json);
            _output.Flush();
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
