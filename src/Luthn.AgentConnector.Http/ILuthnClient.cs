using Luthn.Sdk.Access;
using Luthn.Sdk.Agent;
using Luthn.Sdk.AgentConnections;
using Luthn.Sdk.Classification;
using Luthn.Sdk.Context;
using Luthn.Sdk.Memory;
using Luthn.Sdk.Source;
using Luthn.Sdk.Wiki;

namespace Luthn.AgentConnector.Http;

public interface ILuthnAgentClient
{
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

}

public interface ILuthnClient : ILuthnAgentClient
{
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
