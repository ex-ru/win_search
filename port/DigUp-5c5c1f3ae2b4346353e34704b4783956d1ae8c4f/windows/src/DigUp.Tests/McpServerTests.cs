using System.Text.Json;
using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

public sealed class McpServerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _indexDir;

    public McpServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DigUp_McpTests_" + Guid.NewGuid().ToString("N"));
        _indexDir = Path.Combine(_tempDir, "index");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_indexDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string ExecuteMcpRequest(string requestJson, Func<string, bool, IReadOnlyList<float>?>? embedder = null)
    {
        using var doc = JsonDocument.Parse(requestJson);
        string singleLine = JsonSerializer.Serialize(doc.RootElement);
        var input = new StringReader(singleLine + "\n");
        var output = new StringWriter();
        using var server = new McpServer(input, output, _indexDir, embedder);
        server.Run();
        return output.ToString().Trim();
    }

    [Fact]
    public void Mcp_Initialize_ReturnsValidProtocolCapabilities()
    {
        string req = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}""";
        string response = ExecuteMcpRequest(req);

        Assert.NotEmpty(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());

        var result = root.GetProperty("result");
        Assert.Equal(McpServer.ProtocolVersion, result.GetProperty("protocolVersion").GetString());
        Assert.Equal(McpServer.ServerName, result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public void Mcp_Ping_ReturnsEmptyResult()
    {
        string req = """{"jsonrpc":"2.0","id":42,"method":"ping"}""";
        string response = ExecuteMcpRequest(req);

        Assert.NotEmpty(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("result", out _));
    }

    [Fact]
    public void Mcp_ToolsList_ContainsAllExpectedToolsWithSchemas()
    {
        string req = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        string response = ExecuteMcpRequest(req);

        using var doc = JsonDocument.Parse(response);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");

        var names = new List<string>();
        foreach (var t in tools.EnumerateArray())
        {
            names.Add(t.GetProperty("name").GetString()!);
            Assert.True(t.TryGetProperty("description", out _));
            Assert.True(t.TryGetProperty("inputSchema", out _));
        }

        Assert.Contains("search_documents", names);
        Assert.Contains("read_document_content", names);
        Assert.Contains("get_document_info", names);
        Assert.Contains("get_index_status", names);
    }

    [Fact]
    public void Mcp_SearchDocuments_FindsIndexedData()
    {
        // 1. Setup a test index with a file
        string sampleFile = Path.Combine(_tempDir, "contract-alpha.txt");
        File.WriteAllText(sampleFile, "Договор поставки промышленного оборудования номер 99281 на сумму 1500000 рублей.");

        using (var store = new IndexStore(_indexDir))
        {
            long fileId = store.Insert(new CrawlCandidate(sampleFile, 100, 1, FileKind.Doc));
            store.AddSegment(fileId, SegmentKind.Chunk, Modality.Text,
                loc: 1, locEnd: 1, excerpt: "Договор поставки 99281",
                text: "договор поставки промышленного оборудования номер 99281 на сумму 1500000 рублей");
        }

        // 2. Call search_documents over MCP
        string req = """
        {
            "jsonrpc": "2.0",
            "id": 10,
            "method": "tools/call",
            "params": {
                "name": "search_documents",
                "arguments": {
                    "query": "договор 99281",
                    "limit": 5
                }
            }
        }
        """;

        string response = ExecuteMcpRequest(req);
        Assert.NotEmpty(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal(10, root.GetProperty("id").GetInt32());

        var content = root.GetProperty("result").GetProperty("content");
        string text = content[0].GetProperty("text").GetString()!;

        Assert.Contains("contract-alpha.txt", text);
        Assert.Contains("99281", text);
    }

    [Fact]
    public void Mcp_ReadDocumentContent_ReadsRequestedLineSliceSafely()
    {
        string sampleFile = Path.Combine(_tempDir, "config.yaml");
        File.WriteAllLines(sampleFile, new[]
        {
            "server:",
            "  host: 127.0.0.1",
            "  port: 8080",
            "  ssl: false",
            "database:",
            "  name: testdb"
        });

        string req = $$"""
        {
            "jsonrpc": "2.0",
            "id": 11,
            "method": "tools/call",
            "params": {
                "name": "read_document_content",
                "arguments": {
                    "path": "{{sampleFile.Replace("\\", "\\\\")}}",
                    "start_line": 2,
                    "max_lines": 3
                }
            }
        }
        """;

        string response = ExecuteMcpRequest(req);
        using var doc = JsonDocument.Parse(response);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        Assert.Contains("host: 127.0.0.1", text);
        Assert.Contains("port: 8080", text);
        Assert.Contains("ssl: false", text);
        Assert.DoesNotContain("server:", text);
    }

    [Fact]
    public void Mcp_ReadDocumentContent_SecurityGuardrailRejectsRestrictedPaths()
    {
        string restricted = @"C:\Windows\System32\config\SAM";
        string req = $$"""
        {
            "jsonrpc": "2.0",
            "id": 12,
            "method": "tools/call",
            "params": {
                "name": "read_document_content",
                "arguments": {
                    "path": "{{restricted.Replace("\\", "\\\\")}}"
                }
            }
        }
        """;

        string response = ExecuteMcpRequest(req);
        using var doc = JsonDocument.Parse(response);
        var isError = doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        Assert.True(isError);
        Assert.Contains("restricted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mcp_GetIndexStatus_ReturnsStoreInformation()
    {
        using (var store = new IndexStore(_indexDir))
        {
            store.Insert(new CrawlCandidate(Path.Combine(_tempDir, "test.txt"), 10, 1, FileKind.Doc));
        }

        string req = """
        {
            "jsonrpc": "2.0",
            "id": 13,
            "method": "tools/call",
            "params": {
                "name": "get_index_status",
                "arguments": {}
            }
        }
        """;

        string response = ExecuteMcpRequest(req);
        using var doc = JsonDocument.Parse(response);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        Assert.Contains("DigUp RAG Index Status", text);
        Assert.Contains("Main Index:", text);
        Assert.Contains("Strict Read-Only", text);
    }

    [Fact]
    public void Mcp_UnknownTool_ReturnsToolError()
    {
        string req = """
        {
            "jsonrpc": "2.0",
            "id": 99,
            "method": "tools/call",
            "params": {
                "name": "delete_all_files",
                "arguments": {}
            }
        }
        """;

        string response = ExecuteMcpRequest(req);
        using var doc = JsonDocument.Parse(response);
        var isError = doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        Assert.True(isError);
        Assert.Contains("Unknown tool", text);
    }
}
