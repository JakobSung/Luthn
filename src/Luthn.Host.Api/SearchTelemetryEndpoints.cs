using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

public static class SearchTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapSearchTelemetry(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent/search-telemetry")
            .RequireServiceScope(ServiceScopes.MetricsWrite);
        group.MapPost("/observations", RecordObservation)
            .WithName("RecordSearchObservation");
        group.MapPost("/feedback", RecordFeedback)
            .WithName("RecordSearchFeedback");
        return app;
    }

    public static Results<Accepted<SearchTelemetryAcceptedResponse>, BadRequest<ProblemDetails>> RecordObservation(
        SearchObservationRequest request,
        IOperationalMetrics metrics)
    {
        var error = ValidateObservation(request);
        if (error is not null)
        {
            return TypedResults.BadRequest(error);
        }

        metrics.RecordSearchRequest(
            request.Surface,
            request.Outcome,
            request.CacheStatus,
            TimeSpan.FromMilliseconds(request.DurationMilliseconds),
            request.ResultCount);
        return TypedResults.Accepted(
            "/api/operator/metrics",
            new SearchTelemetryAcceptedResponse(true));
    }

    public static Results<Accepted<SearchTelemetryAcceptedResponse>, BadRequest<ProblemDetails>> RecordFeedback(
        SearchFeedbackRequest request,
        IOperationalMetrics metrics)
    {
        if (!SearchTelemetry.IsValidRetrievalId(request.RetrievalId))
        {
            return TypedResults.BadRequest(ApiValidation.CreateProblem(
                "Invalid search feedback.",
                "retrievalId must be an opaque Luthn retrieval id."));
        }

        if (SearchTelemetry.BoundJudgment(request.Judgment) == "other")
        {
            return TypedResults.BadRequest(ApiValidation.CreateProblem(
                "Invalid search feedback.",
                "judgment must be helpful or unhelpful."));
        }

        metrics.RecordSearchFeedback(request.Judgment);
        return TypedResults.Accepted(
            "/api/operator/metrics",
            new SearchTelemetryAcceptedResponse(true));
    }

    private static ProblemDetails? ValidateObservation(SearchObservationRequest request)
    {
        if (SearchTelemetry.BoundSurface(request.Surface) == "other" ||
            request.Surface != "mcp_context_pack")
        {
            return Problem("surface must be mcp_context_pack.");
        }

        if (SearchTelemetry.BoundOutcome(request.Outcome) == "other")
        {
            return Problem("outcome is not recognized.");
        }

        if (SearchTelemetry.BoundCacheStatus(request.CacheStatus) == "other" ||
            request.CacheStatus == "not_applicable")
        {
            return Problem("cacheStatus must be hit, miss, bypass, or expired.");
        }

        if (request.DurationMilliseconds is < 0 or > SearchTelemetry.MaximumDurationMilliseconds)
        {
            return Problem($"durationMilliseconds must be between 0 and {SearchTelemetry.MaximumDurationMilliseconds}.");
        }

        if (request.ResultCount is < 0 or > SearchTelemetry.MaximumResultCount)
        {
            return Problem($"resultCount must be between 0 and {SearchTelemetry.MaximumResultCount}.");
        }

        if ((request.Outcome == "zero_result" && request.ResultCount != 0) ||
            (request.Outcome == "succeeded" && request.ResultCount == 0) ||
            (request.Outcome is "timeout" or "canceled" or "error" && request.ResultCount != 0))
        {
            return Problem("outcome and resultCount are inconsistent.");
        }

        return null;
    }

    private static ProblemDetails Problem(string detail) =>
        ApiValidation.CreateProblem("Invalid search observation.", detail);
}

public sealed record SearchObservationRequest(
    string Surface,
    string Outcome,
    string CacheStatus,
    long DurationMilliseconds,
    int ResultCount);

public sealed record SearchFeedbackRequest(string RetrievalId, string Judgment);
public sealed record SearchTelemetryAcceptedResponse(bool Accepted);
