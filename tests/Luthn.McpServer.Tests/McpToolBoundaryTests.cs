using System.Reflection;
using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.McpServer;
using Luthn.McpServer.Tools;
using Luthn.Sdk.Access;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Context;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Source;
using Luthn.Sdk.Wiki;

namespace Luthn.McpServer.Tests;

public sealed class McpToolBoundaryTests
{
    [Fact]
    public void DefaultRegistryContainsOnlySafeAgentTools()
    {
        var tools = LuthnMcpToolRegistry.CreateDefault(new FakeLuthnClient());
        var names = tools.Select(tool => tool.Name).ToArray();

        Assert.Equal([
            "get_context_pack",
            "search_safe_context",
            "get_wiki_proposal",
            "classify_preview",
            "create_shared_memory",
            "query_shared_memory",
            "get_shared_memory_item"
        ], names);
        Assert.DoesNotContain("read_raw_vault", names);
        Assert.DoesNotContain("dump_source_records", names);
        Assert.DoesNotContain("query_private_records", names);
    }

    [Fact]
    public void McpServerDependsOnConnectorButNotCore()
    {
        var references = typeof(LuthnMcpToolRegistry).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.Contains("Luthn.AgentConnector.Http", references);
        Assert.DoesNotContain("Luthn.Core", references);
    }

    [Fact]
    public async Task ContextPackToolCallsConnectorWithCoreTags()
    {
        var client = new FakeLuthnClient();
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("""{"query":"demo runbook","coreTags":["demo"],"maxItems":5}""");

        var result = await tool.InvokeAsync(args.RootElement);

        Assert.Equal(["demo"], client.LastCoreTags);
        Assert.Equal(5, client.LastMaxItems);
        Assert.Equal("demo runbook", client.LastContextPackQuery);
        Assert.IsType<ContextPackDto>(result);
    }

    [Fact]
    public async Task SafeSearchToolCallsConnectorWithQueryAndCoreTags()
    {
        var client = new FakeLuthnClient();
        var tool = new SearchSafeContextTool(client);
        using var args = JsonDocument.Parse("""{"query":"billing outage","coreTags":["runbook"],"maxItems":5}""");

        var result = await tool.InvokeAsync(args.RootElement);

        Assert.Equal("billing outage", client.LastSearchQuery);
        Assert.Equal(["runbook"], client.LastSearchCoreTags);
        Assert.Equal(5, client.LastSearchMaxItems);
        Assert.IsType<SafeSearchResponseDto>(result);
    }

    [Fact]
    public async Task WikiProposalAndClassificationToolsCallSafeConnectorMethods()
    {
        var client = new FakeLuthnClient();
        using var wikiArgs = JsonDocument.Parse("""{"id":"wiki-demo-runbook"}""");
        using var previewArgs = JsonDocument.Parse(
            """{"sourceId":"source-1","content":"Public implementation note.","sourceType":"note"}""");

        var wiki = await new GetWikiProposalTool(client).InvokeAsync(wikiArgs.RootElement);
        var preview = await new ClassifyPreviewTool(client).InvokeAsync(previewArgs.RootElement);

        Assert.Equal("wiki-demo-runbook", client.LastWikiProposalId);
        Assert.Equal("source-1", client.LastPreviewRequest?.SourceId);
        Assert.IsType<WikiProposalDto>(wiki);
        Assert.IsType<ClassificationPreviewDto>(preview);
    }

    [Fact]
    public async Task SharedMemoryToolsCallSafeConnectorMethods()
    {
        var client = new FakeLuthnClient();
        using var createArgs = JsonDocument.Parse(
            """
            {
              "title": "Release memory",
              "safeSummary": "Public-safe release summary.",
              "coreTags": ["release", "runbook"],
              "sourceSessionId": "session-1"
            }
            """);
        using var queryArgs = JsonDocument.Parse(
            """{"query":"release","coreTags":["runbook"],"maxItems":5}""");
        using var readArgs = JsonDocument.Parse("""{"id":"memory-1"}""");

        var created = await new CreateSharedMemoryTool(client).InvokeAsync(createArgs.RootElement);
        var query = await new QuerySharedMemoryTool(client).InvokeAsync(queryArgs.RootElement);
        var read = await new GetSharedMemoryItemTool(client).InvokeAsync(readArgs.RootElement);

        Assert.Equal("Release memory", client.LastCreateMemoryRequest?.Title);
        Assert.Equal("Public-safe release summary.", client.LastCreateMemoryRequest?.SafeSummary);
        Assert.Equal(["release", "runbook"], client.LastCreateMemoryRequest?.CoreTags);
        Assert.Equal("session-1", client.LastCreateMemoryRequest?.SourceSessionId);
        Assert.Equal("release", client.LastMemoryQueryRequest?.Query);
        Assert.Equal(["runbook"], client.LastMemoryQueryRequest?.CoreTags);
        Assert.Equal(5, client.LastMemoryQueryRequest?.MaxItems);
        Assert.Equal("memory-1", client.LastMemoryItemId);
        Assert.IsType<SharedMemoryItemDto>(created);
        Assert.IsType<SharedMemoryQueryResponseDto>(query);
        Assert.IsType<SharedMemoryItemDto>(read);
    }

    [Fact]
    public async Task JsonRpcServerInitializesAndListsToolSchemas()
    {
        var server = new McpJsonRpcServer(LuthnMcpToolRegistry.CreateDefault(new FakeLuthnClient()));

        var initialize = await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        var tools = await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        using var initializeJson = JsonDocument.Parse(initialize!);
        using var toolsJson = JsonDocument.Parse(tools!);
        Assert.Equal("2025-06-18", initializeJson.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("luthn-mcp-server", initializeJson.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        var tool = toolsJson.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "get_context_pack");
        Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("inputSchema").TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task JsonRpcToolCallInvokesRegisteredSafeTool()
    {
        var client = new FakeLuthnClient();
        var server = new McpJsonRpcServer(LuthnMcpToolRegistry.CreateDefault(client));

        var response = await server.HandleAsync(
            """
            {"jsonrpc":"2.0","id":"call-1","method":"tools/call","params":{"name":"get_context_pack","arguments":{"query":"demo","coreTags":["demo"],"maxItems":3}}}
            """);

        using var json = JsonDocument.Parse(response!);
        var content = Assert.Single(json.RootElement.GetProperty("result").GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.False(json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("demo", client.LastContextPackQuery);
        Assert.Equal(["demo"], client.LastCoreTags);
        Assert.Equal(3, client.LastMaxItems);
    }

    private sealed class FakeLuthnClient : ILuthnClient
    {
        public IReadOnlyList<string>? LastCoreTags { get; private set; }
        public int? LastMaxItems { get; private set; }
        public string? LastContextPackQuery { get; private set; }
        public string? LastSearchQuery { get; private set; }
        public IReadOnlyList<string>? LastSearchCoreTags { get; private set; }
        public int? LastSearchMaxItems { get; private set; }
        public string? LastWikiProposalId { get; private set; }
        public ClassificationPreviewRequestDto? LastPreviewRequest { get; private set; }
        public CreateSharedMemoryItemRequestDto? LastCreateMemoryRequest { get; private set; }
        public SharedMemoryQueryRequestDto? LastMemoryQueryRequest { get; private set; }
        public string? LastMemoryItemId { get; private set; }

        public Task<ContextPackDto> GetContextPackAsync(
            IReadOnlyList<string> coreTags,
            int maxItems = 20,
            string? query = null,
            CancellationToken cancellationToken = default)
        {
            LastCoreTags = coreTags;
            LastMaxItems = maxItems;
            LastContextPackQuery = query;
            return Task.FromResult(new ContextPackDto(coreTags, []));
        }

        public Task<SafeSearchResponseDto> SearchAsync(
            string? query,
            IReadOnlyList<string> coreTags,
            int maxItems = 20,
            CancellationToken cancellationToken = default)
        {
            LastSearchQuery = query;
            LastSearchCoreTags = coreTags;
            LastSearchMaxItems = maxItems;
            return Task.FromResult(new SafeSearchResponseDto(query, coreTags, []));
        }

        public Task<WikiProposalDto> GetWikiProposalAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            LastWikiProposalId = id;
            return Task.FromResult(new WikiProposalDto(id, "# Demo", []));
        }

        public Task<ClassificationPreviewDto> ClassifyPreviewAsync(
            ClassificationPreviewRequestDto request,
            CancellationToken cancellationToken = default)
        {
            LastPreviewRequest = request;
            return Task.FromResult(new ClassificationPreviewDto(
                request.SourceId,
                new ClassificationResultDto("Public", 0.95, ["runbook"], false),
                new StorageDecisionDto("WikiAndCore", ["public-safe"], true, true, false)));
        }

        public Task<SourceIntakeResponseDto> IntakeSourceAsync(
            SourceIntakeRequestDto request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceIntakeResponseDto(
                "source-1",
                "source-1",
                "classification-1",
                "wiki-1",
                null,
                "audit-1",
                new ClassificationResultDto("Public", 0.95, ["runbook"], false),
                new StorageDecisionDto("WikiCandidate", ["public-safe"], true, true, false)));

        public Task<SharedMemoryItemDto> CreateSharedMemoryItemAsync(
            CreateSharedMemoryItemRequestDto request,
            CancellationToken cancellationToken = default)
        {
            LastCreateMemoryRequest = request;
            return Task.FromResult(MemoryItem());
        }

        public Task<SharedMemoryItemDto> GetSharedMemoryItemAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            LastMemoryItemId = id;
            return Task.FromResult(MemoryItem());
        }

        public Task<SharedMemoryQueryResponseDto> QuerySharedMemoryAsync(
            SharedMemoryQueryRequestDto request,
            CancellationToken cancellationToken = default)
        {
            LastMemoryQueryRequest = request;
            return Task.FromResult(new SharedMemoryQueryResponseDto(
                request.Query,
                request.CoreTags,
                [MemoryItem()]));
        }

        public Task<SensitiveAccessResultDto> GetSensitiveAccessResultAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensitiveAccessResultDto(
                id,
                "sensitive-ref-1",
                "Approved",
                "approved-redacted-output-available",
                true,
                "Public-safe release steps.",
                "redacted-output",
                "approved-redacted-output-available",
                ["Approved limited output is sourced from a public-safe redacted summary."]));

        public Task<SensitiveAccessRequestDto> ApproveSensitiveAccessRequestAsync(
            string id,
            SensitiveAccessDecisionRequestDto request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensitiveAccessRequestDto(
                id,
                "sensitive-ref-1",
                "Approved",
                "agent-service",
                DateTimeOffset.UnixEpoch,
                "operator",
                DateTimeOffset.UnixEpoch,
                !string.IsNullOrWhiteSpace(request.RedactedSummary),
                string.IsNullOrWhiteSpace(request.RedactedSummary)
                    ? "approved-redacted-output-unavailable"
                    : "approved-redacted-output-available"));

        public Task<SensitiveAccessRequestDto> DenySensitiveAccessRequestAsync(
            string id,
            SensitiveAccessDecisionRequestDto request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensitiveAccessRequestDto(
                id,
                "sensitive-ref-1",
                "Denied",
                "agent-service",
                DateTimeOffset.UnixEpoch,
                "operator",
                DateTimeOffset.UnixEpoch,
                false,
                "denied-no-output"));

        private static SharedMemoryItemDto MemoryItem() =>
            new(
                "memory-1",
                "Release memory",
                "Public-safe release summary.",
                "Public",
                ["release", "runbook"],
                "SharedAcrossAgents",
                "Durable",
                null,
                "session-1",
                true,
                DateTimeOffset.UnixEpoch);
    }
}
