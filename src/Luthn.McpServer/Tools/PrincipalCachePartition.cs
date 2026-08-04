using System.Security.Cryptography;
using System.Text;

namespace Luthn.McpServer.Tools;

internal static class PrincipalCachePartition
{
    public static string Create(
        string? bearer,
        Uri? baseUri = null,
        string? workspaceId = null)
    {
        var material = string.Join(
            "|",
            baseUri?.AbsoluteUri.TrimEnd('/').ToLowerInvariant() ?? "local-endpoint",
            string.IsNullOrWhiteSpace(workspaceId) ? "server-bound-workspace" : workspaceId.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(bearer) ? "local-anonymous" : bearer);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()}";
    }
}
