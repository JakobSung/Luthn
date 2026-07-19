using Luthn.AgentConnector.Http;
using Luthn.McpServer;
using Luthn.McpServer.Tools;

var baseUrl = Environment.GetEnvironmentVariable("LUTHN_BASE_URL");
var bearer = Environment.GetEnvironmentVariable("LUTHN_SERVICE_BEARER") ??
    Environment.GetEnvironmentVariable("LUTHN_SERVICE_VALUE");
var options = new LuthnClientOptions
{
    BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
        ? new Uri("http://localhost:8080")
        : new Uri(baseUrl),
    BearerToken = string.IsNullOrWhiteSpace(bearer) ? null : bearer
};
var client = new LuthnClient(options);
var principalCachePartition = PrincipalCachePartition.Create(bearer);
var tools = LuthnMcpToolRegistry.CreateDefault(client, principalCachePartition);
var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();

switch (command)
{
    case "--list-tools":
    case "list-tools":
        foreach (var tool in tools)
        {
            Console.WriteLine(tool.Name);
        }

        break;
    case null:
    case "":
        await new McpJsonRpcServer(tools).RunAsync(Console.In, Console.Out);
        break;
    default:
        PrintUsage();
        Environment.ExitCode = 1;
        break;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/Luthn.McpServer -- --list-tools");
    Console.WriteLine("  dotnet run --project src/Luthn.McpServer");
    Console.WriteLine("Environment:");
    Console.WriteLine("  LUTHN_BASE_URL=http://localhost:8080");
    Console.WriteLine("  LUTHN_SERVICE_BEARER=<external bearer value>");
    Console.WriteLine("  LUTHN_SERVICE_VALUE=<local .env bearer value>");
}
