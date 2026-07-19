using System.Text.Json;
using System.Text.Json.Nodes;
using Luthn.McpServer.Tools;

namespace Luthn.McpServer;

public sealed class McpJsonRpcServer(IReadOnlyList<ILuthnMcpTool> tools)
{
    public const string ProtocolVersion = "2025-06-18";
    public const string SchemaVersion = "2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly Dictionary<string, ILuthnMcpTool> _tools = tools.ToDictionary(
        tool => tool.Name,
        StringComparer.Ordinal);

    public async Task RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await HandleAsync(line, cancellationToken);
            if (response is not null)
            {
                await output.WriteLineAsync(response);
                await output.FlushAsync();
            }
        }
    }

    public async Task<string?> HandleAsync(
        string line,
        CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException error)
        {
            return Serialize(ErrorResponse(null, Error(-32700, $"Parse error: {error.Message}")));
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind is not JsonValueKind.String)
            {
                var id = TryReadId(root);
                return Serialize(ErrorResponse(id, Error(-32600, "Invalid JSON-RPC request.")));
            }

            var method = methodElement.GetString();
            var requestId = TryReadId(root);
            var isNotification = requestId is null;
            if (isNotification)
            {
                return null;
            }

            try
            {
                var result = method switch
                {
                    "initialize" => InitializeResult(),
                    "ping" => new { },
                    "tools/list" => ToolsListResult(),
                    "tools/call" => await CallToolAsync(root, cancellationToken),
                    _ => null
                };

                return result is null
                    ? Serialize(ErrorResponse(requestId, Error(-32601, $"Method '{method}' is not supported.")))
                    : Serialize(SuccessResponse(requestId, result));
            }
            catch (ArgumentException error)
            {
                return Serialize(ErrorResponse(requestId, Error(-32602, error.Message)));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                return Serialize(ErrorResponse(requestId, Error(-32000, error.Message)));
            }
        }
    }

    private async Task<object> CallToolAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind is not JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            throw new ArgumentException("tools/call requires params.name.");
        }

        var name = nameElement.GetString()!;
        if (!_tools.TryGetValue(name, out var tool))
        {
            throw new ArgumentException($"Unknown tool '{name}'.");
        }

        JsonDocument? emptyArguments = null;
        try
        {
            var arguments = parameters.TryGetProperty("arguments", out var argumentElement) &&
                argumentElement.ValueKind is JsonValueKind.Object
                    ? argumentElement
                    : (emptyArguments = JsonDocument.Parse("{}")).RootElement;
            var result = await tool.InvokeAsync(arguments, cancellationToken);
            var text = JsonSerializer.Serialize(result, JsonOptions);

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text
                    }
                },
                isError = false
            };
        }
        finally
        {
            emptyArguments?.Dispose();
        }
    }

    private static object InitializeResult() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new
        {
            tools = new
            {
                listChanged = false
            }
        },
        serverInfo = new
        {
            name = "luthn-mcp-server",
            version = "0.1.0",
            schemaVersion = SchemaVersion
        }
    };

    private static object ToolsListResult() => new
    {
        tools = LuthnMcpToolRegistry.ToolDescriptors.Select(descriptor => new
        {
            descriptor.Name,
            descriptor.Description,
            inputSchema = descriptor.InputSchema
        })
    };

    private static JsonNode? TryReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        return JsonNode.Parse(id.GetRawText());
    }

    private static object SuccessResponse(JsonNode? id, object result) => new
    {
        jsonrpc = "2.0",
        id,
        result
    };

    private static object ErrorResponse(JsonNode? id, object error) => new
    {
        jsonrpc = "2.0",
        id,
        error
    };

    private static object Error(int code, string message) => new
    {
        code,
        message
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
