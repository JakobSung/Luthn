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
                ProjectKeyProperty(),
                TaskKeyProperty(),
                TopicTagsProperty(),
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
                ProjectKeyProperty(),
                TaskKeyProperty(),
                TopicTagsProperty(),
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
                ProjectKeyProperty(),
                TaskKeyProperty(),
                TopicTagsProperty(),
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
                ProjectKeyProperty(),
                TaskKeyProperty(),
                TopicTagsProperty(),
                IntegerProperty("maxItems", "Maximum memory items to return.")
            ])),
        new(
            "submit_search_feedback",
            "Submit explicit helpful or unhelpful feedback for an opaque Luthn retrieval id. No query or result content is accepted.",
            ToolSchema([
                BoundedStringProperty("retrievalId", "Opaque retrieval id returned by a safe search or context pack.", 64, "^retrieval-[0-9a-f]{32}$"),
                EnumStringProperty("judgment", "Feedback judgment.", ["helpful", "unhelpful"])
            ], ["retrievalId", "judgment"])),
        new(
            "get_shared_memory_item",
            "Read a safe shared-memory item by id.",
            ToolSchema([StringProperty("id", "Shared memory item id.")], ["id"])),
        new(
            "create_sensitive_access_request",
            "Create a bounded sensitive-access request. This tool cannot approve or deny requests.",
            ToolSchema([
                StringProperty("sensitiveReferenceId", "Public sensitive record reference id."),
                StringProperty("reason", "Bounded request purpose."),
                StringProperty("sessionId", "Non-sensitive session correlation id."),
                IntegerProperty("expiresInSeconds", "Bounded request lifetime in seconds.", 3_600, 60)
            ], ["sensitiveReferenceId", "reason", "sessionId", "expiresInSeconds"])),
        new(
            "get_sensitive_access_request",
            "Read the metadata-only status of a sensitive-access request.",
            ToolSchema([StringProperty("id", "Sensitive access request id.")], ["id"])),
        new(
            "get_sensitive_access_result",
            "Read the bounded public-safe redacted result of a sensitive-access request.",
            ToolSchema([StringProperty("id", "Sensitive access request id.")], ["id"]))
    ];

    public static IReadOnlyList<string> AllowedToolNames { get; } =
        ToolDescriptors.Select(descriptor => descriptor.Name).ToArray();

    public static IReadOnlyList<ILuthnMcpTool> CreateDefault(ILuthnAgentClient client)
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
            new SubmitSearchFeedbackTool(client),
            new GetSharedMemoryItemTool(client),
            new CreateSensitiveAccessRequestTool(client),
            new GetSensitiveAccessRequestTool(client),
            new GetSensitiveAccessResultTool(client)
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

    private static KeyValuePair<string, object> BoundedStringProperty(
        string name,
        string description,
        int maximumLength,
        string pattern) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
            ["maxLength"] = maximumLength,
            ["pattern"] = pattern
        });

    private static KeyValuePair<string, object> EnumStringProperty(
        string name,
        string description,
        IReadOnlyList<string> values) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = values
        });

    private static KeyValuePair<string, object> IntegerProperty(
        string name,
        string description,
        int maximum = 50,
        int minimum = 1) =>
        new(name, new Dictionary<string, object>
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = minimum,
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

    private static KeyValuePair<string, object> ProjectKeyProperty() =>
        StringProperty(
            "projectKey",
            "Optional normalized non-sensitive project key. Do not send a raw path.");

    private static KeyValuePair<string, object> TaskKeyProperty() =>
        StringProperty("taskKey", "Optional normalized non-sensitive task key.");

    private static KeyValuePair<string, object> TopicTagsProperty() =>
        new("topicTags", new Dictionary<string, object>
        {
            ["type"] = "array",
            ["description"] = "Optional normalized non-sensitive topic tags.",
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
