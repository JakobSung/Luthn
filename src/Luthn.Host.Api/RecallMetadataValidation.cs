using Luthn.Core.Search;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

internal sealed record NormalizedRecallMetadata(
    string? ProjectKey,
    string? TaskKey,
    IReadOnlyList<string> TopicTags);

internal static class RecallMetadataValidation
{
    public static ProblemDetails? TryNormalize(
        string? projectKey,
        string? taskKey,
        IEnumerable<string>? topicTags,
        string title,
        out NormalizedRecallMetadata metadata)
    {
        try
        {
            metadata = new NormalizedRecallMetadata(
                RecallMetadata.NormalizeKey(projectKey),
                RecallMetadata.NormalizeKey(taskKey),
                RecallMetadata.NormalizeTopicTags(topicTags));
            return null;
        }
        catch (ArgumentException error)
        {
            metadata = new NormalizedRecallMetadata(null, null, []);
            return ApiValidation.CreateProblem(title, error.Message);
        }
    }
}
