namespace Luthn.Host.Api;

public sealed record LuthnHostOperationalOptions
{
    public bool EnableForwardedHeaders { get; init; }
    public bool TrustAllForwardedHeaders { get; init; }
    public bool EnforceHttps { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 65;
    public int RateLimitPermitLimit { get; init; } = 600;
    public int RateLimitWindowSeconds { get; init; } = 60;

    public TimeSpan EffectiveRequestTimeout =>
        TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 300));

    public int EffectiveRateLimitPermitLimit =>
        Math.Clamp(RateLimitPermitLimit, 1, 60_000);

    public TimeSpan EffectiveRateLimitWindow =>
        TimeSpan.FromSeconds(Math.Clamp(RateLimitWindowSeconds, 1, 3_600));

}
