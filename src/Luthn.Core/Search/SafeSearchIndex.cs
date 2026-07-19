using System.Text;
using Luthn.Core.Classification;
using Luthn.Core.Context;

namespace Luthn.Core.Search;

public sealed class SafeSearchIndex
{
    private readonly TimeProvider _timeProvider;

    public SafeSearchIndex()
        : this(TimeProvider.System)
    {
    }

    public SafeSearchIndex(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public SafeSearchResponse Search(
        SafeSearchRequest request,
        IEnumerable<ContextPackCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var normalizedRequest = new SafeSearchRequest(
            request.Query,
            request.CoreTags,
            request.MaxItems,
            request.ProjectKey,
            request.TaskKey,
            request.TopicTags);
        var requestedTags = normalizedRequest.CoreTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedTopics = normalizedRequest.TopicTags.ToHashSet(StringComparer.Ordinal);
        var normalizedQuery = SafeSearchText.Normalize(normalizedRequest.Query);
        var queryTokens = SafeSearchText.Tokenize(normalizedRequest.Query);
        var maxItems = SafeSearchLimits.ClampMaxItems(normalizedRequest.MaxItems);
        var now = _timeProvider.GetUtcNow();

        var results = candidates
            .Where(candidate => candidate.AllowsAgentContext)
            .Where(candidate => candidate.Sensitivity == SensitivityLevel.Public)
            .Where(candidate => normalizedRequest.ProjectKey is null ||
                candidate.ProjectKey is null ||
                string.Equals(candidate.ProjectKey, normalizedRequest.ProjectKey, StringComparison.Ordinal))
            .Where(candidate => requestedTags.Count == 0 ||
                candidate.CoreTags.Any(tag => requestedTags.Contains(tag)))
            .Select(candidate => Rank(
                candidate,
                normalizedRequest,
                requestedTags,
                requestedTopics,
                normalizedQuery,
                queryTokens,
                now))
            .Where(ranked => queryTokens.Count == 0 || ranked.QueryScore > 0)
            .OrderByDescending(ranked => ranked.Score)
            .ThenBy(ranked => ranked.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ranked => ranked.Candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .Select(ranked => new SafeSearchResult(
                ranked.Candidate.Id,
                ranked.Candidate.Title,
                ranked.Candidate.SafeSummary,
                ranked.Candidate.Sensitivity,
                ranked.Candidate.CoreTags,
                ranked.Score)
            {
                ProjectKey = ranked.Candidate.ProjectKey,
                TaskKey = ranked.Candidate.TaskKey,
                TopicTags = ranked.Candidate.TopicTags,
                ProjectionTimestamp = ranked.Candidate.ProjectionTimestamp
            })
            .ToArray();

        return new SafeSearchResponse(normalizedRequest.Query, normalizedRequest.CoreTags, results)
        {
            ProjectKey = normalizedRequest.ProjectKey,
            TaskKey = normalizedRequest.TaskKey,
            TopicTags = normalizedRequest.TopicTags
        };
    }

    private static RankedCandidate Rank(
        ContextPackCandidate candidate,
        SafeSearchRequest request,
        IReadOnlySet<string> requestedTags,
        IReadOnlySet<string> requestedTopics,
        string normalizedQuery,
        IReadOnlySet<string> queryTokens,
        DateTimeOffset now)
    {
        var score = 0;
        var queryScore = 0;

        if (queryTokens.Count > 0)
        {
            queryScore = ScoreField(candidate.Title, normalizedQuery, queryTokens, 1_000, 700, 120) +
                ScoreField(candidate.SafeSummary, normalizedQuery, queryTokens, 0, 400, 40) +
                candidate.CoreTags.Sum(tag => ScoreField(tag, normalizedQuery, queryTokens, 600, 500, 90));
            score += queryScore;
        }

        if (requestedTags.Count > 0)
        {
            score += candidate.CoreTags.Count(tag => requestedTags.Contains(tag)) * 40;
        }

        if (request.ProjectKey is not null &&
            string.Equals(candidate.ProjectKey, request.ProjectKey, StringComparison.Ordinal))
        {
            score += 300;
        }

        if (request.TaskKey is not null &&
            string.Equals(candidate.TaskKey, request.TaskKey, StringComparison.Ordinal))
        {
            score += 200;
        }

        score += candidate.TopicTags.Count(requestedTopics.Contains) * 100;
        score += RecencyScore(candidate.ProjectionTimestamp, now);

        return new RankedCandidate(candidate, score, queryScore);
    }

    private static int RecencyScore(DateTimeOffset projectionTimestamp, DateTimeOffset now)
    {
        if (projectionTimestamp == default)
        {
            return 0;
        }

        var age = now - projectionTimestamp;
        if (age < TimeSpan.Zero)
        {
            return 0;
        }

        if (age <= TimeSpan.FromDays(1))
        {
            return 80;
        }

        if (age <= TimeSpan.FromDays(7))
        {
            return 50;
        }

        return age <= TimeSpan.FromDays(30) ? 20 : 0;
    }

    private static int ScoreField(
        string value,
        string normalizedQuery,
        IReadOnlySet<string> queryTokens,
        int exactScore,
        int phraseScore,
        int tokenScore)
    {
        var normalizedValue = SafeSearchText.Normalize(value);
        if (normalizedValue.Length == 0)
        {
            return 0;
        }

        var score = 0;
        if (normalizedQuery.Length > 0)
        {
            if (string.Equals(normalizedValue, normalizedQuery, StringComparison.Ordinal))
            {
                score += exactScore;
            }
            else if (normalizedValue.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                score += phraseScore;
            }
        }

        var valueTokens = SafeSearchText.TokenizeNormalized(normalizedValue);
        score += queryTokens.Count(valueTokens.Contains) * tokenScore;
        return score;
    }

    private sealed record RankedCandidate(ContextPackCandidate Candidate, int Score, int QueryScore);
}

public static class SafeSearchText
{
    private const string IndexDelimiter = "|";

    public static IReadOnlySet<string> Tokenize(string? value) =>
        TokenizeNormalized(Normalize(value));

    public static IReadOnlySet<string> TokenizeNormalized(string normalizedValue) =>
        normalizedValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = true;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    public static string BuildTokenIndex(params IEnumerable<string?>[] values)
    {
        var tokens = values
            .SelectMany(value => value)
            .SelectMany(Tokenize)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return BuildIndex(tokens);
    }

    public static string BuildTagKey(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));

    public static string BuildTagKeyIndex(IEnumerable<string> coreTags) =>
        BuildIndex(coreTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(BuildTagKey)
            .Distinct(StringComparer.Ordinal));

    public static string ToIndexMarker(string value) => $"{IndexDelimiter}{value}{IndexDelimiter}";

    private static string BuildIndex(IEnumerable<string> values) =>
        $"{IndexDelimiter}{string.Join(IndexDelimiter, values)}{IndexDelimiter}";
}
