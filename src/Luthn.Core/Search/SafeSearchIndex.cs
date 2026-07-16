using System.Text;
using Luthn.Core.Classification;
using Luthn.Core.Context;

namespace Luthn.Core.Search;

public sealed class SafeSearchIndex
{
    public SafeSearchResponse Search(
        SafeSearchRequest request,
        IEnumerable<ContextPackCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var normalizedRequest = new SafeSearchRequest(request.Query, request.CoreTags, request.MaxItems);
        var requestedTags = normalizedRequest.CoreTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedQuery = SafeSearchText.Normalize(normalizedRequest.Query);
        var queryTokens = SafeSearchText.Tokenize(normalizedRequest.Query);
        var maxItems = SafeSearchLimits.ClampMaxItems(normalizedRequest.MaxItems);

        var results = candidates
            .Where(candidate => candidate.AllowsAgentContext)
            .Where(candidate => candidate.Sensitivity == SensitivityLevel.Public)
            .Where(candidate => requestedTags.Count == 0 ||
                candidate.CoreTags.Any(tag => requestedTags.Contains(tag)))
            .Select(candidate => Rank(candidate, requestedTags, normalizedQuery, queryTokens))
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
                ranked.Score))
            .ToArray();

        return new SafeSearchResponse(normalizedRequest.Query, normalizedRequest.CoreTags, results);
    }

    private static RankedCandidate Rank(
        ContextPackCandidate candidate,
        IReadOnlySet<string> requestedTags,
        string normalizedQuery,
        IReadOnlySet<string> queryTokens)
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

        return new RankedCandidate(candidate, score, queryScore);
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
}
