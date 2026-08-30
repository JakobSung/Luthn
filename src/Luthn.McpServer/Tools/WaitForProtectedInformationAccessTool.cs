using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class WaitForProtectedInformationAccessTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "wait_for_protected_information_access";

    public async Task<object> InvokeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        McpToolArguments.RejectUnknownProperties(
            arguments,
            "accessHandle",
            "maxWaitSeconds",
            "pollIntervalMs");
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            return await client.WaitForProtectedInformationAccessAsync(
                new ProtectedInformationAccessWaitRequestDto(
                    McpToolArguments.ReadRequiredString(arguments, "accessHandle"),
                    ReadOptionalInt(arguments, "maxWaitSeconds", 30),
                    ReadOptionalInt(arguments, "pollIntervalMs", 250)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
    }

    private static int ReadOptionalInt(
        JsonElement arguments,
        string propertyName,
        int defaultValue)
    {
        if (!arguments.TryGetProperty(propertyName, out var element))
        {
            return defaultValue;
        }

        return element.TryGetInt32(out var value)
            ? value
            : throw new ArgumentException(
                $"{propertyName} must be an integer.",
                nameof(arguments));
    }

    private static ProtectedInformationAccessWaitResponseDto Cancelled() =>
        new(
            "cancelled",
            "The wait was cancelled. No protected information was opened.");
}
