using System.Security.Cryptography;
using System.Text;

namespace Luthn.Tools;

public static class ServiceTokenDigest
{
    public static string ReadTokenFromStdin(string input) =>
        input.TrimEnd('\r', '\n');

    public static string CreateSha256Digest(string token)
    {
        if (token.Length == 0)
        {
            throw new ArgumentException("Service token must not be empty.", nameof(token));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
