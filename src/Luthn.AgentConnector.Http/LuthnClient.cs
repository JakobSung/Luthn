using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Luthn.Sdk.Access;
using Luthn.Sdk.Agent;
using Luthn.Sdk.AgentConnections;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Context;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Source;
using Luthn.Sdk.Wiki;

namespace Luthn.AgentConnector.Http;

public sealed class LuthnClient : ILuthnClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Action<HttpRequestMessage>? _configureRequest;

    public LuthnClient(HttpClient http)
        : this(http, configureRequest: null)
    {
    }

    public LuthnClient(LuthnClientOptions options)
        : this(CreateHttpClient(options), CreateConfigureRequest(options))
    {
    }

    private LuthnClient(HttpClient http, Action<HttpRequestMessage>? configureRequest)
    {
        _http = http;
        _configureRequest = configureRequest;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("http://localhost:8080");
        }
    }

    public async Task<ContextPackDto> GetContextPackAsync(
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreTags);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/context-packs")
        {
            Content = JsonContent.Create(
                new ContextPackRequestDto(coreTags, maxItems, query),
                options: JsonOptions)
        };

        return await SendJsonAsync<ContextPackDto>(request, cancellationToken);
    }

    public async Task<SafeSearchResponseDto> SearchAsync(
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreTags);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/search")
        {
            Content = JsonContent.Create(
                new SafeSearchRequestDto(query, coreTags, maxItems),
                options: JsonOptions)
        };

        return await SendJsonAsync<SafeSearchResponseDto>(request, cancellationToken);
    }

    public async Task<WikiProposalDto> GetWikiProposalAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Wiki proposal id is required.", nameof(id));
        }

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/wiki/proposals/{Uri.EscapeDataString(id)}");

        _configureRequest?.Invoke(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var markdown = await response.Content.ReadAsStringAsync(cancellationToken);

        return new WikiProposalDto(id, markdown, []);
    }

    public async Task<ClassificationPreviewDto> ClassifyPreviewAsync(
        ClassificationPreviewRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/classification/preview")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<ClassificationPreviewDto>(httpRequest, cancellationToken);
    }

    public async Task<TurnSummaryIntakeResponseDto> IntakeTurnSummaryAsync(
        TurnSummaryIntakeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/agent/turn-summaries")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<TurnSummaryIntakeResponseDto>(httpRequest, cancellationToken);
    }

    public Task<AgentConnectionListDto> ListAgentConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/agent-connections");
        return SendJsonAsync<AgentConnectionListDto>(request, cancellationToken);
    }

    public async Task<AgentConnectionDto> ReportAgentConnectionObservationAsync(
        string agentId,
        AgentConnectionObservationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        ArgumentNullException.ThrowIfNull(request);
        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/agent-connections/{Uri.EscapeDataString(agentId)}/observations")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<AgentConnectionDto>(httpRequest, cancellationToken);
    }

    public async Task<SourceIntakeResponseDto> IntakeSourceAsync(
        SourceIntakeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/sources")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<SourceIntakeResponseDto>(httpRequest, cancellationToken);
    }

    public async Task<SharedMemoryItemDto> CreateSharedMemoryItemAsync(
        CreateSharedMemoryItemRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/memory/items")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<SharedMemoryItemDto>(httpRequest, cancellationToken);
    }

    public async Task<SharedMemoryItemDto> GetSharedMemoryItemAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Shared memory item id is required.", nameof(id));
        }

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/memory/items/{Uri.EscapeDataString(id)}");

        return await SendJsonAsync<SharedMemoryItemDto>(request, cancellationToken);
    }

    public async Task<SharedMemoryQueryResponseDto> QuerySharedMemoryAsync(
        SharedMemoryQueryRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/memory/query")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        return await SendJsonAsync<SharedMemoryQueryResponseDto>(httpRequest, cancellationToken);
    }

    public async Task<SensitiveAccessRequestDto> CreateSensitiveAccessRequestAsync(
        SensitiveAccessCreateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/access-requests")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        return await SendJsonAsync<SensitiveAccessRequestDto>(httpRequest, cancellationToken);
    }

    public async Task<SensitiveAccessRequestDto> GetSensitiveAccessRequestAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Sensitive access request id is required.", nameof(id));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/access-requests/{Uri.EscapeDataString(id)}");
        return await SendJsonAsync<SensitiveAccessRequestDto>(request, cancellationToken);
    }

    public async Task<SensitiveAccessResultDto> GetSensitiveAccessResultAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Sensitive access request id is required.", nameof(id));
        }

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/access-requests/{Uri.EscapeDataString(id)}/result");

        return await SendJsonAsync<SensitiveAccessResultDto>(request, cancellationToken);
    }


    private async Task<T> SendJsonAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _configureRequest?.Invoke(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new InvalidOperationException("Luthn API returned an empty response.");
    }

    private static HttpClient CreateHttpClient(LuthnClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var http = options.ConfigureHttpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(options.ConfigureHttpMessageHandler(), disposeHandler: true);

        http.BaseAddress = options.BaseUrl;
        return http;
    }

    private static Action<HttpRequestMessage>? CreateConfigureRequest(
        LuthnClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BearerToken))
        {
            return options.ConfigureRequest;
        }

        return request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                options.BearerToken);
            options.ConfigureRequest?.Invoke(request);
        };
    }
}
