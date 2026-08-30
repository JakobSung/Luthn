using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed record OperatorConfigOptions
{
    public string Directory { get; init; } = ".luthn/operator";
}

public interface IOperatorClassificationSettingsStore
{
    OperatorClassificationProviderSettings Current { get; }

    ValueTask<OperatorClassificationProviderSettings> ReadAsync(CancellationToken cancellationToken = default);
    ValueTask<OperatorClassificationProviderSettings> SaveAsync(
        SaveClassificationProviderConfigurationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OperatorClassificationSettingsStore(
    IOptions<OperatorConfigOptions> options,
    IConfiguration configuration) : IOperatorClassificationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private OperatorClassificationProviderSettings? _current;

    public OperatorClassificationProviderSettings Current => _current ??= ReadCurrent();

    public async ValueTask<OperatorClassificationProviderSettings> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return _current = ReadConfiguredFallback();
        }

        await using var stream = File.OpenRead(SettingsPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var settings = NormalizePersisted(document.RootElement, out var requiresRewrite);
        if (requiresRewrite)
        {
            await PersistAsync(settings, cancellationToken);
        }

        return _current = settings;
    }

    public async ValueTask<OperatorClassificationProviderSettings> SaveAsync(
        SaveClassificationProviderConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = ParseProvider(request.Provider);
        var settings = provider switch
        {
            OperatorClassificationProviderKind.LocalDeterministic => CreateLocalDeterministic(),
            OperatorClassificationProviderKind.LocalHttp => CreateLocalHttp(request.Endpoint),
            _ => throw new InvalidOperationException(ClassificationProviderOptions.ProviderRequiredMessage)
        };

        await PersistAsync(settings, cancellationToken);
        return _current = settings;
    }

    private OperatorClassificationProviderSettings ReadCurrent()
    {
        if (!File.Exists(SettingsPath))
        {
            return ReadConfiguredFallback();
        }

        using var stream = File.OpenRead(SettingsPath);
        using var document = JsonDocument.Parse(stream);
        var settings = NormalizePersisted(document.RootElement, out var requiresRewrite);
        if (requiresRewrite)
        {
            Persist(settings);
        }

        return settings;
    }

    private OperatorClassificationProviderSettings ReadConfiguredFallback()
    {
        var classification = configuration.GetSection("Luthn:Classification");
        var options = classification.Get<ClassificationProviderOptions>() ?? new ClassificationProviderOptions();
        return options.ResolveProvider() switch
        {
            OperatorClassificationProviderKind.LocalDeterministic => CreateLocalDeterministic(),
            OperatorClassificationProviderKind.LocalHttp => CreateLocalHttp(options.LocalHttp.Endpoint),
            _ => CreateUnconfigured()
        };
    }

    private static OperatorClassificationProviderSettings NormalizePersisted(
        JsonElement persisted,
        out bool requiresRewrite)
    {
        var providerName = ReadString(persisted, "provider");
        var endpoint = ReadString(persisted, "endpoint");
        var hasLegacyFields = HasNonEmptyValue(persisted, "model") ||
            HasNonEmptyValue(persisted, "authHeaderName") ||
            HasNonEmptyValue(persisted, "protectedApiKey") ||
            HasNonEmptyValue(persisted, "apiKey");

        OperatorClassificationProviderSettings settings;
        if (string.Equals(providerName, ClassificationProviderOptions.LocalDeterministicProvider, StringComparison.OrdinalIgnoreCase))
        {
            settings = CreateLocalDeterministic();
            requiresRewrite = hasLegacyFields || !string.IsNullOrWhiteSpace(endpoint);
            return settings;
        }

        if (string.Equals(providerName, ClassificationProviderOptions.LocalHttpProvider, StringComparison.OrdinalIgnoreCase) &&
            LocalHttpEndpointValidator.TryValidate(endpoint, out var validatedEndpoint))
        {
            settings = CreateLocalHttp(validatedEndpoint);
            requiresRewrite = hasLegacyFields ||
                !string.Equals(endpoint, validatedEndpoint.AbsoluteUri, StringComparison.Ordinal);
            return settings;
        }

        settings = CreateUnconfigured();
        requiresRewrite = !IsSanitizedUnconfigured(persisted);
        return settings;
    }

    private async Task PersistAsync(
        OperatorClassificationProviderSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = TemporaryPath();
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, ToPersisted(settings), SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            ReplaceSettingsFile(temporaryPath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private void Persist(OperatorClassificationProviderSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = TemporaryPath();
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, ToPersisted(settings), SerializerOptions);
                stream.Flush();
            }

            ReplaceSettingsFile(temporaryPath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static OperatorClassificationProviderKind ParseProvider(string? value)
    {
        if (string.Equals(value?.Trim(), ClassificationProviderOptions.LocalDeterministicProvider, StringComparison.OrdinalIgnoreCase))
        {
            return OperatorClassificationProviderKind.LocalDeterministic;
        }

        if (string.Equals(value?.Trim(), ClassificationProviderOptions.LocalHttpProvider, StringComparison.OrdinalIgnoreCase))
        {
            return OperatorClassificationProviderKind.LocalHttp;
        }

        throw new InvalidOperationException(
            $"Unsupported classification provider '{value}'. Choose LocalDeterministic or LocalHttp.");
    }

    private static OperatorClassificationProviderSettings CreateLocalDeterministic() =>
        new()
        {
            Provider = OperatorClassificationProviderKind.LocalDeterministic,
            PayloadClass = "local-classification-input",
            RedactionState = "local-only"
        };

    private static OperatorClassificationProviderSettings CreateLocalHttp(string? endpoint)
    {
        if (!LocalHttpEndpointValidator.TryValidate(endpoint, out var validatedEndpoint, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return CreateLocalHttp(validatedEndpoint);
    }

    private static OperatorClassificationProviderSettings CreateLocalHttp(Uri endpoint) =>
        new()
        {
            Provider = OperatorClassificationProviderKind.LocalHttp,
            Endpoint = endpoint.AbsoluteUri,
            PayloadClass = "classification-input",
            RedactionState = "same-device-local-http"
        };

    private static OperatorClassificationProviderSettings CreateUnconfigured() =>
        new()
        {
            Provider = OperatorClassificationProviderKind.Unconfigured,
            PayloadClass = "classification-input",
            RedactionState = "provider-unconfigured"
        };

    private static PersistedSettings ToPersisted(OperatorClassificationProviderSettings settings) =>
        new(
            settings.Provider.ToString(),
            settings.Endpoint,
            "",
            "",
            "",
            settings.PayloadClass,
            settings.RedactionState);

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? ""
            : "";

    private static bool HasNonEmptyValue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind != JsonValueKind.Null &&
        property.ValueKind != JsonValueKind.Undefined &&
        (property.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(property.GetString()));

    private static bool IsSanitizedUnconfigured(JsonElement element) =>
        string.Equals(
            ReadString(element, "provider"),
            ClassificationProviderOptions.UnconfiguredProvider,
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(ReadString(element, "endpoint")) &&
        !HasNonEmptyValue(element, "model") &&
        !HasNonEmptyValue(element, "authHeaderName") &&
        !HasNonEmptyValue(element, "protectedApiKey") &&
        !HasNonEmptyValue(element, "apiKey");

    private string SettingsDirectory => Path.GetFullPath(options.Value.Directory);
    private string SettingsPath => Path.Combine(SettingsDirectory, "classification-provider.json");
    private string TemporaryPath() => Path.Combine(
        SettingsDirectory,
        $".classification-provider.{Guid.NewGuid():N}.tmp");

    private void ReplaceSettingsFile(string temporaryPath)
    {
        if (File.Exists(SettingsPath))
        {
            File.Replace(temporaryPath, SettingsPath, null);
        }
        else
        {
            File.Move(temporaryPath, SettingsPath);
        }
    }

    private sealed record PersistedSettings(
        string Provider,
        string Endpoint,
        string Model,
        string AuthHeaderName,
        string ProtectedApiKey,
        string PayloadClass,
        string RedactionState);
}

internal static class LocalHttpEndpointValidator
{
    public const string ValidationMessage =
        "LocalHttp endpoint must be an absolute HTTP or HTTPS URL on localhost, an IPv4/IPv6 loopback address, or host.docker.internal, without user information.";

    public static bool TryValidate(string? value, out Uri endpoint) =>
        TryValidate(value, out endpoint, out _);

    public static bool TryValidate(string? value, out Uri endpoint, out string error)
    {
        error = ValidationMessage;
        endpoint = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !IsSameDeviceHost(candidate.Host))
        {
            return false;
        }

        endpoint = candidate;
        return true;
    }

    private static bool IsSameDeviceHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
