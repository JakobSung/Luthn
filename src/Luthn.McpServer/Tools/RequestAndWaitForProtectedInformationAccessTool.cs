using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class RequestAndWaitForProtectedInformationAccessTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    private const int DefaultMaxWaitSeconds = 30;
    private const int DefaultPollIntervalMs = 250;
    private const int MinMaxWaitSeconds = 1;
    private const int MaxMaxWaitSeconds = 60;
    private const int MinPollIntervalMs = 100;
    private const int MaxPollIntervalMs = 5_000;

    public string Name => "request_and_wait_for_protected_information_access";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        McpToolArguments.RejectUnknownProperties(
            arguments,
            "memoryItemId",
            "reason",
            "maxWaitSeconds",
            "pollIntervalMs");
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            var requestResponse = await client.RequestProtectedInformationAccessAsync(
                new ProtectedInformationAccessRequestDto(
                    McpToolArguments.ReadRequiredString(arguments, "memoryItemId"),
                    McpToolArguments.ReadOptionalString(arguments, "reason")),
                cancellationToken);
            if (!string.Equals(requestResponse.Status, "requested", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(requestResponse.AccessHandle))
            {
                return RequestNotStarted(requestResponse.Status);
            }

            var waitResponse = await client.WaitForProtectedInformationAccessAsync(
                new ProtectedInformationAccessWaitRequestDto(
                    requestResponse.AccessHandle,
                    ReadBoundedOptionalInt(
                        arguments,
                        "maxWaitSeconds",
                        DefaultMaxWaitSeconds,
                        MinMaxWaitSeconds,
                        MaxMaxWaitSeconds),
                    ReadBoundedOptionalInt(
                        arguments,
                        "pollIntervalMs",
                        DefaultPollIntervalMs,
                        MinPollIntervalMs,
                        MaxPollIntervalMs)),
                cancellationToken);
            if (!string.Equals(waitResponse.Status, "approved", StringComparison.OrdinalIgnoreCase))
            {
                return FromWaitResponse(waitResponse);
            }

            var result = await client.GetProtectedInformationResultAsync(
                new ProtectedInformationResultRequestDto(requestResponse.AccessHandle),
                cancellationToken);
            return FromResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
    }

    private static int ReadBoundedOptionalInt(
        JsonElement arguments,
        string propertyName,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetProperty(propertyName, out var element))
        {
            return defaultValue;
        }

        if (!element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"{propertyName} must be an integer.", nameof(arguments));
        }

        if (value < minimum || value > maximum)
        {
            throw new ArgumentException(
                $"{propertyName} must be between {minimum} and {maximum}.",
                nameof(arguments));
        }

        return value;
    }

    private static ProtectedInformationAccessOrchestrationResponseDto RequestNotStarted(string? status) =>
        new(
            string.IsNullOrWhiteSpace(status) ? "request-unavailable" : status,
            false,
            null,
            null,
            null,
            null,
            null,
            "The protected information confirmation request could not be started.",
            []);

    private static ProtectedInformationAccessOrchestrationResponseDto FromWaitResponse(
        ProtectedInformationAccessWaitResponseDto response) =>
        new(
            response.Status,
            false,
            null,
            null,
            null,
            null,
            null,
            response.Message,
            string.IsNullOrWhiteSpace(response.Message) ? [] : [response.Message]);

    private static ProtectedInformationAccessOrchestrationResponseDto FromResult(
        ProtectedInformationResultDto result) =>
        new(
            result.Status,
            result.ContentAvailable,
            result.Title,
            result.Content,
            result.GrantExpiresAt,
            result.RemainingReads,
            result.MaxReads,
            result.Reasons.FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason)) ??
                "The approved protected information was returned.",
            result.Reasons);

    private static ProtectedInformationAccessOrchestrationResponseDto Cancelled() =>
        new(
            "cancelled",
            false,
            null,
            null,
            null,
            null,
            null,
            "The confirmation wait was cancelled. No protected information was opened.",
            ["The confirmation wait was cancelled. No protected information was opened."]);
}
