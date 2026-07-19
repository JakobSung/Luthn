using System.Security.Cryptography;
using System.Text;

namespace Luthn.McpServer.Tools;

internal static class PrincipalCachePartition
{
    public static string Create(string? bearer) =>
        string.IsNullOrWhiteSpace(bearer)
            ? "single-owner-local"
            : $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))).ToLowerInvariant()}";
}
