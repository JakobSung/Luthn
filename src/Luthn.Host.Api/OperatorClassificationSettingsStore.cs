using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
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
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration) : IOperatorClassificationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Luthn.Operator.ClassificationProvider.ApiKey.v1");

    private OperatorClassificationProviderSettings? _current;

    public OperatorClassificationProviderSettings Current => _current ??= ReadCurrent();

    public async ValueTask<OperatorClassificationProviderSettings> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var path = SettingsPath;
        if (!File.Exists(path))
        {
            var fallback = ReadConfiguredFallback();
            _current = fallback;
            return fallback;
        }

        await using var stream = File.OpenRead(path);
        var persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (persisted is null)
        {
            var fallback = ReadConfiguredFallback();
            _current = fallback;
            return fallback;
        }

        var settings = ToSettings(persisted);
        _current = settings;
        return settings;
    }

    public async ValueTask<OperatorClassificationProviderSettings> SaveAsync(
        SaveClassificationProviderConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = ParseProvider(request.Provider);
        var classificationOptions = ReadClassificationOptions();
        if (provider == OperatorClassificationProviderKind.Mock)
        {
            classificationOptions.EnsureMockAllowed();
        }

        var apiKey = await ResolveApiKeyAsync(request, provider, cancellationToken);

        var settings = new OperatorClassificationProviderSettings
        {
            Provider = provider,
            Model = Normalize(request.Model, DefaultModel(provider)),
            Endpoint = Normalize(request.Endpoint, DefaultEndpoint(provider)),
            AuthHeaderName = Normalize(request.AuthHeaderName, "Authorization"),
            ApiKey = apiKey,
            PayloadClass = "classification-input",
            RedactionState = provider == OperatorClassificationProviderKind.Mock
                ? "local-only"
                : "operator-configured-provider"
        };

        Validate(settings);
        Directory.CreateDirectory(SettingsDirectory);
        var persisted = new PersistedSettings(
            settings.Provider,
            settings.Model,
            settings.Endpoint,
            settings.AuthHeaderName,
            string.IsNullOrWhiteSpace(settings.ApiKey) ? "" : _protector.Protect(settings.ApiKey),
            settings.PayloadClass,
            settings.RedactionState);

        var temporaryPath = Path.Combine(
            SettingsDirectory,
            $".classification-provider.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }

        _current = settings;
        return settings;
    }

    private async ValueTask<string> ResolveApiKeyAsync(
        SaveClassificationProviderConfigurationRequest request,
        OperatorClassificationProviderKind provider,
        CancellationToken cancellationToken)
    {
        if (request.ClearApiKey || provider == OperatorClassificationProviderKind.Mock)
        {
            return "";
        }

        if (request.ApiKey is not null)
        {
            return request.ApiKey.Trim();
        }

        var existing = await ReadAsync(cancellationToken);
        return existing.Provider == provider ? existing.ApiKey : "";
    }

    private OperatorClassificationProviderSettings ReadCurrent()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
        {
            return ReadConfiguredFallback();
        }

        using var stream = File.OpenRead(path);
        var persisted = JsonSerializer.Deserialize<PersistedSettings>(stream, SerializerOptions);
        return persisted is null
            ? ReadConfiguredFallback()
            : ToSettings(persisted);
    }

    private OperatorClassificationProviderSettings ReadConfiguredFallback()
    {
        var options = ReadClassificationOptions();
        var provider = options.ResolveProvider();

        if (string.Equals(provider, "external-http", StringComparison.OrdinalIgnoreCase))
        {
            return new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.ExternalHttp,
                Endpoint = options.ExternalHttp.Endpoint ?? "",
                AuthHeaderName = Normalize(options.ExternalHttp.AuthHeaderName, "Authorization"),
                PayloadClass = Normalize(options.ExternalHttp.PayloadClass, "classification-input"),
                RedactionState = Normalize(options.ExternalHttp.RedactionState, "external-provider-opt-in")
            };
        }

        if (string.Equals(
            provider,
            ClassificationProviderOptions.UnconfiguredProvider,
            StringComparison.OrdinalIgnoreCase))
        {
            return new OperatorClassificationProviderSettings
            {
                Provider = OperatorClassificationProviderKind.Unconfigured,
                PayloadClass = "classification-input",
                RedactionState = "provider-unconfigured"
            };
        }

        return new OperatorClassificationProviderSettings
        {
            Provider = OperatorClassificationProviderKind.Mock,
            PayloadClass = "local-classification-input",
            RedactionState = "local-only"
        };
    }

    private ClassificationProviderOptions ReadClassificationOptions() =>
        configuration
            .GetSection("Luthn:Classification")
            .Get<ClassificationProviderOptions>() ?? new ClassificationProviderOptions();

    private OperatorClassificationProviderSettings ToSettings(PersistedSettings persisted) =>
        new()
        {
            Provider = persisted.Provider,
            Model = persisted.Model,
            Endpoint = persisted.Endpoint,
            AuthHeaderName = Normalize(persisted.AuthHeaderName, "Authorization"),
            ApiKey = Unprotect(persisted.ProtectedApiKey),
            PayloadClass = Normalize(persisted.PayloadClass, "classification-input"),
            RedactionState = Normalize(persisted.RedactionState, "operator-configured-provider")
        };

    private string SettingsDirectory => Path.GetFullPath(options.Value.Directory);
    private string SettingsPath => Path.Combine(SettingsDirectory, "classification-provider.json");

    private string Unprotect(string protectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey))
        {
            return "";
        }

        try
        {
            return _protector.Unprotect(protectedApiKey);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "Stored classification provider API key could not be decrypted. Re-enter the key in the operator console.",
                error);
        }
    }

    private static OperatorClassificationProviderKind ParseProvider(string value)
    {
        if (Enum.TryParse<OperatorClassificationProviderKind>(
            value,
            ignoreCase: true,
            out var provider)
            && Enum.IsDefined(provider)
            && provider != OperatorClassificationProviderKind.Unconfigured)
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Unsupported classification provider '{value}'. Choose a configured provider instead of the Unconfigured system state.");
    }

    private static void Validate(OperatorClassificationProviderSettings settings)
    {
        if (settings.Provider == OperatorClassificationProviderKind.Unconfigured)
        {
            throw new InvalidOperationException(ClassificationProviderOptions.ProviderRequiredMessage);
        }

        if (settings.Provider == OperatorClassificationProviderKind.Mock)
        {
            return;
        }

        if (settings.Provider == OperatorClassificationProviderKind.ExternalHttp)
        {
            if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException("External HTTP provider endpoint must be an absolute URL.");
            }
            if (settings.HasApiKey && endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "External HTTP provider endpoint must be HTTPS when an API key is configured.");
            }
            return;
        }

        ValidateDirectProviderEndpoint(settings);

        if (!settings.HasApiKey)
        {
            throw new InvalidOperationException($"{settings.Provider} provider requires an API key.");
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException($"{settings.Provider} provider requires a model.");
        }
    }

    private static void ValidateDirectProviderEndpoint(OperatorClassificationProviderSettings settings)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{settings.Provider} provider endpoint must be an HTTPS absolute URL.");
        }

        var allowedHosts = settings.Provider switch
        {
            OperatorClassificationProviderKind.OpenAi => ["api.openai.com"],
            OperatorClassificationProviderKind.Anthropic => ["api.anthropic.com"],
            OperatorClassificationProviderKind.GoogleAi => ["generativelanguage.googleapis.com"],
            OperatorClassificationProviderKind.OpenRouter => ["openrouter.ai"],
            _ => Array.Empty<string>()
        };
        if (!allowedHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{settings.Provider} provider endpoint host must be {string.Join(", ", allowedHosts)}.");
        }
    }

    private static string DefaultModel(OperatorClassificationProviderKind provider) =>
        provider switch
        {
            OperatorClassificationProviderKind.OpenAi => "gpt-4.1-mini",
            OperatorClassificationProviderKind.Anthropic => "claude-sonnet-4-5",
            OperatorClassificationProviderKind.GoogleAi => "gemini-2.5-flash",
            OperatorClassificationProviderKind.OpenRouter => "openai/gpt-4.1-mini",
            _ => ""
        };

    private static string DefaultEndpoint(OperatorClassificationProviderKind provider) =>
        provider switch
        {
            OperatorClassificationProviderKind.ExternalHttp => "",
            OperatorClassificationProviderKind.OpenAi => "https://api.openai.com/v1/chat/completions",
            OperatorClassificationProviderKind.Anthropic => "https://api.anthropic.com/v1/messages",
            OperatorClassificationProviderKind.GoogleAi => "https://generativelanguage.googleapis.com/v1beta/models",
            OperatorClassificationProviderKind.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
            _ => ""
        };

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record PersistedSettings(
        OperatorClassificationProviderKind Provider,
        string Model,
        string Endpoint,
        string AuthHeaderName,
        string ProtectedApiKey,
        string PayloadClass,
        string RedactionState);
}
