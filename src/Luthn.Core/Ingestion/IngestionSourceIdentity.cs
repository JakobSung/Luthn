namespace Luthn.Core.Ingestion;

public sealed class IngestionSourceIdentity
{
    public IngestionSourceIdentity(
        string pluginId,
        string sourceSystem,
        IngestionSourceKind sourceKind,
        string externalSourceId,
        string? displayName = null)
    {
        PluginId = RequiredToken(pluginId, nameof(pluginId));
        SourceSystem = RequiredToken(sourceSystem, nameof(sourceSystem));
        SourceKind = sourceKind;
        ExternalSourceId = RequiredToken(externalSourceId, nameof(externalSourceId));
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
    }

    public string PluginId { get; }

    public string SourceSystem { get; }

    public IngestionSourceKind SourceKind { get; }

    public string ExternalSourceId { get; }

    public string? DisplayName { get; }

    private static string RequiredToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Ingestion source identity value is required.", parameterName);
        }

        var trimmed = value.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Ingestion source identity tokens cannot contain whitespace.", parameterName);
        }

        return trimmed;
    }
}
