namespace Luthn.AgentConnector.Http;

public sealed class LuthnClientOptions
{
    public Uri BaseUrl { get; init; } = new("http://localhost:8080");

    public string? BearerToken { get; init; }

    public Action<HttpRequestMessage>? ConfigureRequest { get; init; }

    public Func<HttpMessageHandler>? ConfigureHttpMessageHandler { get; init; }
}
