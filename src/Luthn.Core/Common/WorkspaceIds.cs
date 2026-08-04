namespace Luthn.Core.Common;

public static class WorkspaceIds
{
    public const string Default = "default";
    public const int MaxLength = 160;

    public static string ForLegacyUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var normalized = userId.Trim().ToLowerInvariant();
        return string.Equals(normalized, "local-owner", StringComparison.Ordinal)
            ? Default
            : $"personal:{normalized}";
    }
}
