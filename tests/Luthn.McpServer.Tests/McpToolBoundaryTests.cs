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
using Luthn.Sdk.Telemetry;
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
            "submit_search_feedback",
            "get_shared_memory_item",
            "create_sensitive_access_request",
            "get_sensitive_access_request",
            "get_sensitive_access_result"
        ], names);
        Assert.DoesNotContain("read_raw_vault", names);
        Assert.DoesNotContain("dump_source_records", names);
        Assert.DoesNotContain("query_private_records", names);
        Assert.DoesNotContain(names, name => name.Contains("approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("deny", StringComparison.OrdinalIgnoreCase));
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
        var observation = Assert.Single(client.SearchObservations);
        Assert.Equal("bypass", observation.CacheStatus);
        Assert.Equal("zero_result", observation.Outcome);
    }

    [Fact]
    public async Task ContextPackToolPassesMetadataAndIsolatesCacheByScope()
    {
        var client = new FakeLuthnClient();
        var tool = new GetContextPackTool(client);
        using var luthnArgs = JsonDocument.Parse(
            """{"query":"recall","projectKey":"luthn","taskKey":"ranking","topicTags":["quality"],"cacheKey":"recall","cacheTtlSeconds":600}""");
        using var otherArgs = JsonDocument.Parse(
            """{"query":"recall","projectKey":"other","taskKey":"ranking","topicTags":["quality"],"cacheKey":"recall","cacheTtlSeconds":600}""");

        await tool.InvokeAsync(luthnArgs.RootElement);
        await tool.InvokeAsync(luthnArgs.RootElement);
        await tool.InvokeAsync(otherArgs.RootElement);

        Assert.Equal(2, client.ContextPackCallCount);
        Assert.Equal("other", client.LastContextPackRequest?.ProjectKey);
        Assert.Equal("ranking", client.LastContextPackRequest?.TaskKey);
        Assert.Equal(["quality"], client.LastContextPackRequest?.TopicTags);
    }

    [Fact]
    public async Task LightweightContextPackCachesWithinTtlAndRefreshesForTopicOrExpiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var client = new FakeLuthnClient
        {
            ContextPackResult = ContextPack("memory-1", "Cached context")
        };
        var tool = new GetContextPackTool(client, () => now);
        using var firstArgs = JsonDocument.Parse(
            """{"query":"release","cacheKey":"project:release","cacheTtlSeconds":600,"maxItems":3,"maxTokens":600}""");
        using var changedTopicArgs = JsonDocument.Parse(
            """{"query":"billing","cacheKey":"project:billing","cacheTtlSeconds":600,"maxItems":3,"maxTokens":600}""");

        await tool.InvokeAsync(firstArgs.RootElement);
        await tool.InvokeAsync(firstArgs.RootElement);
        Assert.Equal(1, client.ContextPackCallCount);

        await tool.InvokeAsync(changedTopicArgs.RootElement);
        Assert.Equal(2, client.ContextPackCallCount);

        now = now.AddMinutes(11);
        await tool.InvokeAsync(firstArgs.RootElement);
        Assert.Equal(3, client.ContextPackCallCount);
        Assert.Equal(["miss", "hit", "miss", "expired"], client.SearchObservations.Select(item => item.CacheStatus));
    }

    [Fact]
    public async Task LightweightContextPackTruncatesRankedItemsWithinConservativeTokenBounds()
    {
        var client = new FakeLuthnClient
        {
            ContextPackResult = new ContextPackDto(
                ["project"],
                Enumerable.Range(1, 5)
                    .Select(index => new ContextPackItemDto(
                        $"memory-{index}",
                        $"Memory {index}",
                        new string('x', 1_400),
                        "Public",
                        ["project"]))
                    .ToArray())
        };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("""{"maxItems":3,"maxTokens":600}""");

        var result = Assert.IsType<ContextPackDto>(await tool.InvokeAsync(args.RootElement));

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.EndsWith("…", item.SafeSummary, StringComparison.Ordinal));
        Assert.True(EstimateTokens(result.Items) <= 600);
        Assert.Equal(12, client.LastMaxItems);
    }

    [Fact]
    public async Task LightweightContextPackRetriesWithFewerItemsWhenEqualSlotsAreTooSmall()
    {
        var client = new FakeLuthnClient
        {
            ContextPackResult = new ContextPackDto(
                ["project"],
                Enumerable.Range(1, 3)
                    .Select(index => new ContextPackItemDto(
                        new string((char)('a' + index), 600),
                        $"Memory {index}",
                        "Fitting context",
                        "Public",
                        ["project"]))
                    .ToArray())
        };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("""{"maxItems":3,"maxTokens":600}""");

        var result = Assert.IsType<ContextPackDto>(await tool.InvokeAsync(args.RootElement));

        Assert.Equal(2, result.Items.Count);
        Assert.True(EstimateTokens(result.Items) <= 600);
    }

    [Fact]
    public async Task LightweightContextPackBackfillsWhenRankedCandidateMetadataCannotFit()
    {
        var client = new FakeLuthnClient
        {
            ContextPackResult = new ContextPackDto(
                ["project"],
                [
                    new ContextPackItemDto(
                        new string('i', 800),
                        "Oversized metadata",
                        "Relevant but impossible to fit.",
                        "Public",
                        ["project"]),
                    ContextPack("memory-1", "First backfilled context").Items[0],
                    ContextPack("memory-2", "Second backfilled context").Items[0],
                    ContextPack("memory-3", "Third backfilled context").Items[0]
                ])
        };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("""{"maxItems":3,"maxTokens":600}""");

        var result = Assert.IsType<ContextPackDto>(await tool.InvokeAsync(args.RootElement));

        Assert.Equal(["memory-1", "memory-2", "memory-3"], result.Items.Select(item => item.Id));
        Assert.True(EstimateTokens(result.Items) <= 600);
    }

    [Fact]
    public async Task LightweightContextPackFailsOpenOnTimeoutAndServiceError()
    {
        var timeoutClient = new FakeLuthnClient
        {
            ContextPackDelay = TimeSpan.FromSeconds(1)
        };
        var timeoutTool = new GetContextPackTool(timeoutClient);
        using var timeoutArgs = JsonDocument.Parse(
            """{"timeoutMs":20,"failOpen":true,"maxItems":3,"maxTokens":600}""");

        var timeoutResult = Assert.IsType<ContextPackDto>(
            await timeoutTool.InvokeAsync(timeoutArgs.RootElement));
        Assert.Empty(timeoutResult.Items);
        Assert.Equal("timeout", Assert.Single(timeoutClient.SearchObservations).Outcome);

        var errorClient = new FakeLuthnClient
        {
            ContextPackException = new HttpRequestException("unavailable")
        };
        var errorTool = new GetContextPackTool(errorClient);
        using var errorArgs = JsonDocument.Parse("""{"failOpen":true}""");

        var errorResult = Assert.IsType<ContextPackDto>(
            await errorTool.InvokeAsync(errorArgs.RootElement));
        Assert.Empty(errorResult.Items);
        Assert.Equal("error", Assert.Single(errorClient.SearchObservations).Outcome);
    }

    [Fact]
    public async Task ManualContextPackStillReportsServiceErrors()
    {
        var client = new FakeLuthnClient
        {
            ContextPackException = new HttpRequestException("unavailable")
        };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => tool.InvokeAsync(args.RootElement));
    }

    [Fact]
    public async Task ContextPackReportsCallerCancellationSeparately()
    {
        var client = new FakeLuthnClient { ContextPackDelay = TimeSpan.FromSeconds(1) };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.InvokeAsync(args.RootElement, cancellation.Token));

        Assert.Equal("canceled", Assert.Single(client.SearchObservations).Outcome);
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
              "sourceSessionId": "session-1",
              "projectKey": "luthn",
              "taskKey": "release",
              "topicTags": ["delivery"]
            }
            """);
        using var queryArgs = JsonDocument.Parse(
            """{"query":"release","coreTags":["runbook"],"maxItems":5,"projectKey":"luthn","taskKey":"release","topicTags":["delivery"]}""");
        using var readArgs = JsonDocument.Parse("""{"id":"memory-1"}""");

        var created = await new CreateSharedMemoryTool(client).InvokeAsync(createArgs.RootElement);
        var query = await new QuerySharedMemoryTool(client).InvokeAsync(queryArgs.RootElement);
        var read = await new GetSharedMemoryItemTool(client).InvokeAsync(readArgs.RootElement);

        Assert.Equal("Release memory", client.LastCreateMemoryRequest?.Title);
        Assert.Equal("Public-safe release summary.", client.LastCreateMemoryRequest?.SafeSummary);
        Assert.Equal(["release", "runbook"], client.LastCreateMemoryRequest?.CoreTags);
        Assert.Equal("session-1", client.LastCreateMemoryRequest?.SourceSessionId);
        Assert.Equal("luthn", client.LastCreateMemoryRequest?.ProjectKey);
        Assert.Equal("release", client.LastCreateMemoryRequest?.TaskKey);
        Assert.Equal(["delivery"], client.LastCreateMemoryRequest?.TopicTags);
        Assert.Equal("release", client.LastMemoryQueryRequest?.Query);
        Assert.Equal(["runbook"], client.LastMemoryQueryRequest?.CoreTags);
        Assert.Equal(5, client.LastMemoryQueryRequest?.MaxItems);
        Assert.Equal("luthn", client.LastMemoryQueryRequest?.ProjectKey);
        Assert.Equal("release", client.LastMemoryQueryRequest?.TaskKey);
        Assert.Equal(["delivery"], client.LastMemoryQueryRequest?.TopicTags);
        Assert.Equal("memory-1", client.LastMemoryItemId);
        Assert.IsType<SharedMemoryItemDto>(created);
        Assert.IsType<SharedMemoryQueryResponseDto>(query);
        Assert.IsType<SharedMemoryItemDto>(read);
    }

    [Fact]
    public async Task FeedbackToolAcceptsOnlyOpaqueIdAndJudgmentFields()
    {
        var client = new FakeLuthnClient();
        using var args = JsonDocument.Parse(
            """{"retrievalId":"retrieval-0123456789abcdef0123456789abcdef","judgment":"helpful"}""");

        var result = await new SubmitSearchFeedbackTool(client).InvokeAsync(args.RootElement);

        Assert.Equal("retrieval-0123456789abcdef0123456789abcdef", client.LastSearchFeedback?.RetrievalId);
        Assert.Equal("helpful", client.LastSearchFeedback?.Judgment);
        Assert.True(Assert.IsType<SearchTelemetryAcceptedDto>(result).Accepted);
    }

    [Fact]
    public async Task TelemetryFailureNeverChangesContextPackResult()
    {
        var client = new FakeLuthnClient
        {
            ContextPackResult = ContextPack("memory-1", "Safe result"),
            TelemetryException = new HttpRequestException("metrics unavailable")
        };
        var tool = new GetContextPackTool(client);
        using var args = JsonDocument.Parse("{}");

        var result = Assert.IsType<ContextPackDto>(await tool.InvokeAsync(args.RootElement));

        Assert.Equal("memory-1", Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task SensitiveAccessToolsExposeRequestStatusAndResultWithoutDecisionTools()
    {
        var client = new FakeLuthnClient();
        using var createArgs = JsonDocument.Parse(
            """{"sensitiveReferenceId":"sensitive-ref-1","reason":"Need bounded access.","sessionId":"session-1","expiresInSeconds":600}""");
        using var idArgs = JsonDocument.Parse("""{"id":"access-1"}""");

        var created = await new CreateSensitiveAccessRequestTool(client).InvokeAsync(createArgs.RootElement);
        var status = await new GetSensitiveAccessRequestTool(client).InvokeAsync(idArgs.RootElement);
        var result = await new GetSensitiveAccessResultTool(client).InvokeAsync(idArgs.RootElement);

        Assert.IsType<SensitiveAccessRequestDto>(created);
        Assert.IsType<SensitiveAccessRequestDto>(status);
        Assert.IsType<SensitiveAccessResultDto>(result);
        Assert.Contains("create_sensitive_access_request", LuthnMcpToolRegistry.AllowedToolNames);
        Assert.Contains("get_sensitive_access_request", LuthnMcpToolRegistry.AllowedToolNames);
        Assert.Contains("get_sensitive_access_result", LuthnMcpToolRegistry.AllowedToolNames);
        Assert.DoesNotContain(LuthnMcpToolRegistry.AllowedToolNames, name => name.Contains("approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(LuthnMcpToolRegistry.AllowedToolNames, name => name.Contains("deny", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(McpJsonRpcServer.SchemaVersion, initializeJson.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("schemaVersion").GetString());

        var tool = toolsJson.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "get_context_pack");
        Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.True(tool.GetProperty("inputSchema").TryGetProperty("properties", out _));
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(4_000, properties.GetProperty("maxTokens").GetProperty("maximum").GetInt32());
        Assert.Equal(5_000, properties.GetProperty("timeoutMs").GetProperty("maximum").GetInt32());
        Assert.Equal(3_600, properties.GetProperty("cacheTtlSeconds").GetProperty("maximum").GetInt32());
        Assert.Equal("boolean", properties.GetProperty("failOpen").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("projectKey").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("taskKey").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("topicTags").GetProperty("type").GetString());

        var sensitiveAccessTool = toolsJson.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "create_sensitive_access_request");
        var expiry = sensitiveAccessTool
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("expiresInSeconds");
        Assert.Equal(60, expiry.GetProperty("minimum").GetInt32());
        Assert.Equal(3_600, expiry.GetProperty("maximum").GetInt32());

        var feedbackTool = toolsJson.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == "submit_search_feedback");
        var feedbackProperties = feedbackTool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(64, feedbackProperties.GetProperty("retrievalId").GetProperty("maxLength").GetInt32());
        Assert.Equal(
            ["helpful", "unhelpful"],
            feedbackProperties.GetProperty("judgment").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void McpRegistryAcceptsOnlyAgentSafeConnectorContract()
    {
        var createDefault = typeof(LuthnMcpToolRegistry).GetMethod(nameof(LuthnMcpToolRegistry.CreateDefault));
        var parameter = Assert.Single(createDefault!.GetParameters());

        Assert.Equal(typeof(ILuthnAgentClient), parameter.ParameterType);
        Assert.NotEqual(typeof(ILuthnClient), parameter.ParameterType);
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
        public ContextPackRequestDto? LastContextPackRequest { get; private set; }
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
        public int ContextPackCallCount { get; private set; }
        public List<SearchObservationRequestDto> SearchObservations { get; } = [];
        public SearchFeedbackRequestDto? LastSearchFeedback { get; private set; }
        public ContextPackDto ContextPackResult { get; init; } = new([], []);
        public TimeSpan ContextPackDelay { get; init; }
        public Exception? ContextPackException { get; init; }
        public Exception? TelemetryException { get; init; }

        public async Task<ContextPackDto> GetContextPackAsync(
            IReadOnlyList<string> coreTags,
            int maxItems = 20,
            string? query = null,
            CancellationToken cancellationToken = default)
        {
            ContextPackCallCount++;
            LastCoreTags = coreTags;
            LastMaxItems = maxItems;
            LastContextPackQuery = query;
            if (ContextPackDelay > TimeSpan.Zero)
            {
                await Task.Delay(ContextPackDelay, cancellationToken);
            }
            if (ContextPackException is not null)
            {
                throw ContextPackException;
            }
            return ContextPackResult with { CoreTags = coreTags };
        }

        public Task<ContextPackDto> GetContextPackAsync(
            ContextPackRequestDto request,
            CancellationToken cancellationToken = default)
        {
            LastContextPackRequest = request;
            return GetContextPackAsync(
                request.CoreTags,
                request.MaxItems,
                request.Query,
                cancellationToken);
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

        public Task<SensitiveAccessRequestDto> CreateSensitiveAccessRequestAsync(
            SensitiveAccessCreateRequestDto request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensitiveAccessRequestDto(
                "access-1",
                "sensitive-ref-1",
                "Pending",
                "agent-service",
                DateTimeOffset.UnixEpoch,
                null,
                null,
                false,
                "pending-approval")
            {
                SessionId = request.SessionId,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(10)
            });

        public Task<SensitiveAccessRequestDto> GetSensitiveAccessRequestAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensitiveAccessRequestDto(
                id,
                "sensitive-ref-1",
                "Pending",
                "agent-service",
                DateTimeOffset.UnixEpoch,
                null,
                null,
                false,
                "pending-approval")
            {
                SessionId = "session-1",
                ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(10)
            });

        public Task<SearchTelemetryAcceptedDto> ReportSearchObservationAsync(
            SearchObservationRequestDto request,
            CancellationToken cancellationToken = default)
        {
            SearchObservations.Add(request);
            return TelemetryException is null
                ? Task.FromResult(new SearchTelemetryAcceptedDto(true))
                : Task.FromException<SearchTelemetryAcceptedDto>(TelemetryException);
        }

        public Task<SearchTelemetryAcceptedDto> SubmitSearchFeedbackAsync(
            SearchFeedbackRequestDto request,
            CancellationToken cancellationToken = default)
        {
            LastSearchFeedback = request;
            return Task.FromResult(new SearchTelemetryAcceptedDto(true));
        }

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

    private static ContextPackDto ContextPack(string id, string summary) =>
        new(
            ["project"],
            [new ContextPackItemDto(id, "Project memory", summary, "Public", ["project"])]);

    private static int EstimateTokens(IEnumerable<ContextPackItemDto> items) =>
        items.Sum(item => Math.Max(
            1,
            (80 +
                item.Id.Length +
                item.Title.Length +
                item.SafeSummary.Length +
                item.Sensitivity.Length +
                item.CoreTags.Sum(tag => tag.Length + 3) +
                (item.ProjectKey?.Length ?? 0) +
                (item.TaskKey?.Length ?? 0) +
                item.TopicTags.Sum(tag => tag.Length + 3) +
                item.ProjectionTimestamp.ToString("O").Length +
                2) / 3));
}
