using System.Net.Http.Json;
using System.Text.Json;
using Luthn.Core.Memory;
using Microsoft.Extensions.Options;

namespace Luthn.Core.Persistence;

/// <summary>
/// Opt-in transport for a local, provider-neutral extension running on the same
/// Docker network. It deliberately accepts neither a public URL nor arbitrary
/// network destinations, so a local installation cannot become an SSRF relay.
/// </summary>
public sealed class LocalExtensionSafeProjectionOptions
{
    public const string SectionName = "Luthn:Extensions:SafeProjection";
    public const string ServiceHost = "luthn-extension";
    public const string DeliveryPath = "/v1/safe-projections";

    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = $"http://{ServiceHost}:8080{DeliveryPath}";
    public string SharedSecretFile { get; init; } = "/run/secrets/local-extension-token";

    public bool IsValid => !Enabled ||
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp &&
        string.Equals(uri.Host, ServiceHost, StringComparison.Ordinal) &&
        uri.Port == 8080 &&
        string.Equals(uri.AbsolutePath, DeliveryPath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        Path.IsPathFullyQualified(SharedSecretFile) &&
        SharedSecretFile.Length <= 512;
}

public sealed class LocalExtensionSafeProjectionSyncTransport(
    HttpClient client,
    IOptions<LocalExtensionSafeProjectionOptions> options)
    : ISafeProjectionSyncTransport
{
    public const string HttpClientName = "luthn.local-safe-projection-extension";
    private const string SharedSecretHeader = "X-Luthn-Extension-Token";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly LocalExtensionSafeProjectionOptions _options = options.Value;

    public string Name => "local-extension";

    public SafeProjectionSyncTransportState State =>
        _options.Enabled && TryReadSecret(out _)
            ? SafeProjectionSyncTransportState.Ready
            : SafeProjectionSyncTransportState.NotConnected;

    public async Task<SafeProjectionSyncTransportResult> SendAsync(
        SafeProjectionSyncEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!SafeProjectionEnvelopeGuard.IsSafe(envelope))
        {
            return new SafeProjectionSyncTransportResult(false, ErrorCode: "extension.invalid_envelope");
        }
        if (!TryReadSecret(out var sharedSecret))
        {
            return new SafeProjectionSyncTransportResult(false, ErrorCode: "extension.unavailable");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(envelope, options: SerializerOptions),
        };
        request.Headers.TryAddWithoutValidation(SharedSecretHeader, sharedSecret);
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SafeProjectionSyncTransportResult(
                    false,
                    ErrorCode: response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                        ? "extension.backpressured"
                        : "extension.rejected",
                    Retryable: response.StatusCode is System.Net.HttpStatusCode.TooManyRequests);
            }

            var receipt = await response.Content.ReadFromJsonAsync<LocalExtensionReceipt>(
                SerializerOptions,
                cancellationToken);
            return receipt is { Accepted: true }
                ? new SafeProjectionSyncTransportResult(true, receipt.Checkpoint)
                : new SafeProjectionSyncTransportResult(false, ErrorCode: "extension.rejected");
        }
        catch (HttpRequestException)
        {
            return new SafeProjectionSyncTransportResult(false, ErrorCode: "extension.unavailable", Retryable: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SafeProjectionSyncTransportResult(false, ErrorCode: "extension.timeout", Retryable: true);
        }
    }

    private bool TryReadSecret(out string value)
    {
        value = string.Empty;
        try
        {
            var candidate = File.ReadAllText(_options.SharedSecretFile).Trim();
            if (candidate.Length is < 32 or > 512 || candidate.Any(char.IsWhiteSpace))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record LocalExtensionReceipt(bool Accepted, string? Checkpoint);
}

internal static class SafeProjectionEnvelopeGuard
{
    public static bool IsSafe(SafeProjectionSyncEnvelope envelope)
    {
        if (envelope.ContractVersion != SafeProjectionSyncContractVersions.Current ||
            !string.Equals(envelope.PayloadClass, ExternalMemoryAdapterCatalog.MetadataOnlyPayload, StringComparison.Ordinal) ||
            !string.Equals(envelope.RedactionState, ExternalMemoryAdapterCatalog.SafeProjectionOnly, StringComparison.Ordinal) ||
            !string.Equals(envelope.ProjectionKind, ExternalMemoryAdapterCatalog.SharedMemoryProjection, StringComparison.Ordinal) ||
            envelope.Title is not null || envelope.CoreTags is null || envelope.CoreTags.Count != 0 ||
            !SafeProjectionSyncPolicy.HasValidExtensionIdentity(
                envelope.WorkspaceId,
                envelope.OriginInstanceId,
                envelope.LocalRecordId) ||
            !SafeProjectionSyncPolicy.HasValidTimeline(
                envelope.CreatedAt,
                envelope.UpdatedAt,
                envelope.DecidedAt,
                envelope.ExpiresAt))
        {
            return false;
        }

        return envelope.Operation switch
        {
            SafeProjectionSyncOperation.Revoke => envelope.SafeSummary is null && envelope.ExpiresAt is null,
            SafeProjectionSyncOperation.Upsert => !string.IsNullOrWhiteSpace(envelope.SafeSummary) && envelope.SafeSummary.Length <= 4000,
            _ => false,
        };
    }

}
