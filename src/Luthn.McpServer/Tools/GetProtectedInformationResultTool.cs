using System.Text.Json;
using Luthn.AgentConnector.Http;
using Luthn.Sdk.Access;

namespace Luthn.McpServer.Tools;

public sealed class GetProtectedInformationResultTool(ILuthnAgentClient client) : ILuthnMcpTool
{
    public string Name => "get_protected_information_result";

    public async Task<object> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        await client.GetProtectedInformationResultAsync(
            new ProtectedInformationResultRequestDto(
                McpToolArguments.ReadRequiredString(arguments, "accessHandle")),
            cancellationToken);
}
