using Luthn.Tools;

namespace Luthn.Tools.Tests;

public sealed class ServiceTokenDigestTests
{
    [Fact]
    public void CreateSha256DigestUsesStablePrefixedLowercaseHex()
    {
        var digest = ServiceTokenDigest.CreateSha256Digest("abc");

        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", digest);
    }

    [Fact]
    public void ReadTokenFromStdinRemovesOnlyTrailingNewline()
    {
        var token = ServiceTokenDigest.ReadTokenFromStdin("  token-value  \n");

        Assert.Equal("  token-value  ", token);
    }

    [Fact]
    public void CreateSha256DigestRejectsEmptyToken()
    {
        var error = Assert.Throws<ArgumentException>(() => ServiceTokenDigest.CreateSha256Digest(""));

        Assert.Equal("token", error.ParamName);
    }
}
