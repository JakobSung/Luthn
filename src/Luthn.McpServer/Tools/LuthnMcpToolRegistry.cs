using Luthn.AgentConnector.Http;

namespace Luthn.McpServer.Tools;

public static class LuthnMcpToolRegistry
{
    public static IReadOnlyList<LuthnMcpToolDescriptor> ToolDescriptors { get; } =
    [
        new(
            "get_context_pack",
            "Return an agent-safe Luthn context pack for an optional query and Core tags.",
            ToolSchema([
                StringProperty("query", "Optional retrieval query."),
                TagsProperty(),
                IntegerProperty("maxItems", "Maximum context items to return."),
                IntegerProperty("maxTokens", "Optional conservative token budget for returned items.", 4_000),
                IntegerProperty("timeoutMs", "Optional retrieval deadline in milliseconds.", 5_000),
                StringProperty("cacheKey", "Optional non-sensitive project and task cache key."),
                IntegerProperty("cacheTtlSeconds", "Optional in-process cache lifetime.", 3_600),
                BooleanProperty("failOpen", "Return an empty context pack instead of an error when retrieval fails.")
            ])),
        new(
            "search_safe_context",
            "Search only Luthn safe projections that are allowed for agent context.",
            ToolSchema([
                StringProperty("query", "Optional retrieval query."),
                TagsProperty(),
                IntegerProperty("maxItems", "Maximum search results to return.")
            ])),
        new(
            "get_wiki_proposal",
            "Read a public-safe wiki proposal by id.",
            ToolSchema([StringProperty("id", "Wiki proposal id.")], ["id"])),
        new(
            "classify_preview",
            "Preview Luthn classification and policy routing for bounded content.",
            ToolSchema([
                StringProperty("sourceId", "Public source id."),
                StringProperty("content", "Bounded content to classify."),
                StringProperty("sourceType", "Optional source type.")
            ], ["sourceId", "content"])),
        new(
            "create_shared_memory",
            "Explicitly submit a shared-memory candidate. Luthn reclassifies it before agent visibility.",
            ToolSchema([
                StringProperty("title", "Short memory title."),
                StringProperty("safeSummary", "Bounded summary candidate; Luthn treats this as untrusted."),
                TagsProperty(),
                StringProperty("visibility", "Visibility, defaults to SharedAcrossAgents."),
                StringProperty("retentionKind", "Retention kind, defaults to Durable."),
                StringProperty("expiresAt", "Expiration timestamp for non-durable memory."),
                StringProperty("sourceSessionId", "Optional source session id."),
                StringProperty("sensitivity", "Declared sensitivity, defaults to Public.")
            ], ["title", "safeSummary", "coreTags"])),
        new(
            "query_shared_memory",
            "Query safe shared-memory projections only.",
            ToolSchema([
                StringProperty("query", "Optional retrieval query."),
                TagsProperty(),
                IntegerProperty("maxItems", "Maximum memory items to return.")
            ])),
        new(
            "get_shared_memory_item",
            "Read a safe shared-memory item by id.",
            ToolSchema([StringProperty("id", "Shared memory item id.")], ["id"]))
    ];

    public static IReadOnlyList<string> AllowedToolNames { get; } =
        ToolDescriptors.Select(descriptor => descriptor.Name).ToArray();

    public static IReadOnlyList<ILuthnMcpTool> CreateDefault(ILuthnClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return
        [
            new GetContextPackTool(client),
            new SearchSafeContextTool(client),
            new GetWikiProposalTool(client),
            new ClassifyPreviewTool(client),
            new CreateSharedMemoryTool(client),
            new QuerySharedMemoryTool(client),
            new GetSharedMemoryItemTool(client)
        ];
    }

    private static Dictionary<string, object> ToolSchema(
        IReadOnlyList<KeyValuePair<string, object>> properties,
        IReadOnlyList<string>? required = null) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = properties.ToDictionary(
                property => property.Key,
                property => property.Value),
            ["required"] = required ?? []
        };

    private static KeyValuePair<string, object> StringProperty(string name, string description) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description
        });

    private static KeyValuePair<string, object> IntegerProperty(
        string name,
        string description,
        int maximum = 50) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = 1,
            ["maximum"] = maximum
        });

    private static KeyValuePair<string, object> BooleanProperty(string name, string description) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "boolean",
            ["description"] = description
        });

    private static KeyValuePair<string, object> TagsProperty() =>
        new("coreTags", new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = "Optional Core tags used for safe retrieval.",
            ["items"] = new Dictionary<string, object>
            {
                ["type"] = "string"
            }
        });
}

public sealed record LuthnMcpToolDescriptor(
    string Name,
    string Description,
    IReadOnlyDictionary<string, object> InputSchema);
