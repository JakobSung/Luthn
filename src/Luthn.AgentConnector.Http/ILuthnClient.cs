using Luthn.Sdk.Access;
using Luthn.Sdk.Agent;
using Luthn.Sdk.AgentConnections;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Context;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Source;
using Luthn.Sdk.Telemetry;
using Luthn.Sdk.Wiki;

namespace Luthn.AgentConnector.Http;

public interface ILuthnAgentClient
{
    Task<ContextPackDto> GetContextPackAsync(
        ContextPackRequestDto request,
        CancellationToken cancellationToken = default) =>
        GetContextPackAsync(
            request.CoreTags,
            request.MaxItems,
            request.Query,
            cancellationToken);

    Task<ContextPackDto> GetContextPackAsync(
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<SafeSearchResponseDto> SearchAsync(
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        CancellationToken cancellationToken = default);

    Task<SafeSearchResponseDto> SearchAsync(
        SafeSearchRequestDto request,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            request.Query,
            request.CoreTags,
            request.MaxItems,
            cancellationToken);

    Task<WikiProposalDto> GetWikiProposalAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<ClassificationPreviewDto> ClassifyPreviewAsync(
        ClassificationPreviewRequestDto request,
        CancellationToken cancellationToken = default);

    Task<TurnSummaryIntakeResponseDto> IntakeTurnSummaryAsync(
        TurnSummaryIntakeRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement turn summary intake.");

    Task<AgentConnectionListDto> ListAgentConnectionsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement agent connection status reads.");

    Task<AgentConnectionDto> ReportAgentConnectionObservationAsync(
        string agentId,
        AgentConnectionObservationRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement agent connection observations.");

    Task<SourceIntakeResponseDto> IntakeSourceAsync(
        SourceIntakeRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement source intake.");

    Task<SharedMemoryItemDto> CreateSharedMemoryItemAsync(
        CreateSharedMemoryItemRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory writes.");

    Task<SharedMemoryItemDto> GetSharedMemoryItemAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory reads.");

    Task<SharedMemoryQueryResponseDto> QuerySharedMemoryAsync(
        SharedMemoryQueryRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory queries.");

    Task<SensitiveAccessRequestDto> CreateSensitiveAccessRequestAsync(
        SensitiveAccessCreateRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access requests.");

    Task<SensitiveAccessRequestDto> GetSensitiveAccessRequestAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access status reads.");

    Task<SensitiveAccessResultDto> GetSensitiveAccessResultAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access result reads.");

    Task<SearchTelemetryAcceptedDto> ReportSearchObservationAsync(
        SearchObservationRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement search telemetry observations.");

    Task<SearchTelemetryAcceptedDto> SubmitSearchFeedbackAsync(
        SearchFeedbackRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement search feedback.");

}

public interface ILuthnClient : ILuthnAgentClient
{
    Task<ContextPackDto> ILuthnAgentClient.GetContextPackAsync(
        IReadOnlyList<string> coreTags,
        int maxItems,
        string? query,
        CancellationToken cancellationToken) =>
        GetContextPackAsync(coreTags, maxItems, query, cancellationToken);

    new Task<ContextPackDto> GetContextPackAsync(
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<SafeSearchResponseDto> ILuthnAgentClient.SearchAsync(
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems,
        CancellationToken cancellationToken) =>
        SearchAsync(query, coreTags, maxItems, cancellationToken);

    new Task<SafeSearchResponseDto> SearchAsync(
        string? query,
        IReadOnlyList<string> coreTags,
        int maxItems = 20,
        CancellationToken cancellationToken = default);

    Task<WikiProposalDto> ILuthnAgentClient.GetWikiProposalAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetWikiProposalAsync(id, cancellationToken);

    new Task<WikiProposalDto> GetWikiProposalAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<ClassificationPreviewDto> ILuthnAgentClient.ClassifyPreviewAsync(
        ClassificationPreviewRequestDto request,
        CancellationToken cancellationToken) =>
        ClassifyPreviewAsync(request, cancellationToken);

    new Task<ClassificationPreviewDto> ClassifyPreviewAsync(
        ClassificationPreviewRequestDto request,
        CancellationToken cancellationToken = default);

    Task<TurnSummaryIntakeResponseDto> ILuthnAgentClient.IntakeTurnSummaryAsync(
        TurnSummaryIntakeRequestDto request,
        CancellationToken cancellationToken) =>
        IntakeTurnSummaryAsync(request, cancellationToken);

    new Task<TurnSummaryIntakeResponseDto> IntakeTurnSummaryAsync(
        TurnSummaryIntakeRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement turn summary intake.");

    Task<AgentConnectionListDto> ILuthnAgentClient.ListAgentConnectionsAsync(
        CancellationToken cancellationToken) =>
        ListAgentConnectionsAsync(cancellationToken);

    new Task<AgentConnectionListDto> ListAgentConnectionsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement agent connection status reads.");

    Task<AgentConnectionDto> ILuthnAgentClient.ReportAgentConnectionObservationAsync(
        string agentId,
        AgentConnectionObservationRequestDto request,
        CancellationToken cancellationToken) =>
        ReportAgentConnectionObservationAsync(agentId, request, cancellationToken);

    new Task<AgentConnectionDto> ReportAgentConnectionObservationAsync(
        string agentId,
        AgentConnectionObservationRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement agent connection observations.");

    Task<SourceIntakeResponseDto> ILuthnAgentClient.IntakeSourceAsync(
        SourceIntakeRequestDto request,
        CancellationToken cancellationToken) =>
        IntakeSourceAsync(request, cancellationToken);

    new Task<SourceIntakeResponseDto> IntakeSourceAsync(
        SourceIntakeRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement source intake.");

    Task<SharedMemoryItemDto> ILuthnAgentClient.CreateSharedMemoryItemAsync(
        CreateSharedMemoryItemRequestDto request,
        CancellationToken cancellationToken) =>
        CreateSharedMemoryItemAsync(request, cancellationToken);

    new Task<SharedMemoryItemDto> CreateSharedMemoryItemAsync(
        CreateSharedMemoryItemRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory writes.");

    Task<SharedMemoryItemDto> ILuthnAgentClient.GetSharedMemoryItemAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetSharedMemoryItemAsync(id, cancellationToken);

    new Task<SharedMemoryItemDto> GetSharedMemoryItemAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory reads.");

    Task<SharedMemoryQueryResponseDto> ILuthnAgentClient.QuerySharedMemoryAsync(
        SharedMemoryQueryRequestDto request,
        CancellationToken cancellationToken) =>
        QuerySharedMemoryAsync(request, cancellationToken);

    new Task<SharedMemoryQueryResponseDto> QuerySharedMemoryAsync(
        SharedMemoryQueryRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement shared memory queries.");

    Task<SearchTelemetryAcceptedDto> ILuthnAgentClient.ReportSearchObservationAsync(
        SearchObservationRequestDto request,
        CancellationToken cancellationToken) =>
        ReportSearchObservationAsync(request, cancellationToken);

    new Task<SearchTelemetryAcceptedDto> ReportSearchObservationAsync(
        SearchObservationRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement search telemetry observations.");

    Task<SearchTelemetryAcceptedDto> ILuthnAgentClient.SubmitSearchFeedbackAsync(
        SearchFeedbackRequestDto request,
        CancellationToken cancellationToken) =>
        SubmitSearchFeedbackAsync(request, cancellationToken);

    new Task<SearchTelemetryAcceptedDto> SubmitSearchFeedbackAsync(
        SearchFeedbackRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement search feedback.");

    Task<SensitiveAccessRequestDto> ILuthnAgentClient.CreateSensitiveAccessRequestAsync(
        SensitiveAccessCreateRequestDto request,
        CancellationToken cancellationToken) =>
        CreateSensitiveAccessRequestAsync(request, cancellationToken);

    new Task<SensitiveAccessRequestDto> CreateSensitiveAccessRequestAsync(
        SensitiveAccessCreateRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access requests.");

    Task<SensitiveAccessRequestDto> ILuthnAgentClient.GetSensitiveAccessRequestAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetSensitiveAccessRequestAsync(id, cancellationToken);

    new Task<SensitiveAccessRequestDto> GetSensitiveAccessRequestAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access status reads.");

    Task<SensitiveAccessResultDto> ILuthnAgentClient.GetSensitiveAccessResultAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetSensitiveAccessResultAsync(id, cancellationToken);

    new Task<SensitiveAccessResultDto> GetSensitiveAccessResultAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access result reads.");

    [Obsolete("Approval is an operator-only capability. Use the trusted access-decision API directly.")]
    Task<SensitiveAccessRequestDto> ApproveSensitiveAccessRequestAsync(
        string id,
        SensitiveAccessDecisionRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access approvals.");

    [Obsolete("Denial is an operator-only capability. Use the trusted access-decision API directly.")]
    Task<SensitiveAccessRequestDto> DenySensitiveAccessRequestAsync(
        string id,
        SensitiveAccessDecisionRequestDto request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This connector does not implement sensitive access denials.");
}
