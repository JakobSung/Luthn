using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Memory;
using Luthn.Core.Search;
using Luthn.Core.Wiki;
using Luthn.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static class ClassificationEndpoints
{
    public static IEndpointRouteBuilder MapLuthnApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("Health");

        app.MapGet("/readyz", CheckReadiness)
            .WithName("Readiness");

        var classification = app.MapGroup("/api/classification");

        classification.MapPost("/preview", Preview)
            .RequireServiceScope(ServiceScopes.ClassificationPreview)
            .WithName("PreviewClassification");

        app.MapSourceIntake();
        app.MapMemoryItems();
        app.MapTurnSummaryIntake();
        app.MapAgentConnections();
        app.MapExternalPublication();

        var agent = app.MapGroup("/api/agent")
            .RequireServiceScope(ServiceScopes.AgentRead);

        agent.MapPost("/context-packs", CreateContextPack)
            .WithName("CreateAgentContextPack");

        agent.MapPost("/search", SearchAgentContext)
            .WithName("SearchAgentContext");

        var wiki = app.MapGroup("/api/wiki");

        wiki.MapGet("/proposals/{id}", ReadWikiProposal)
            .RequireServiceScope(ServiceScopes.AgentRead)
            .WithName("ReadWikiSafeProposal");

        app.MapSensitiveAccessRequests();
        app.MapAuditEvents();
        app.MapCollectionProvenance();

        return app;
    }

    public static async Task<Results<Ok<ClassificationPreviewResponse>, BadRequest<ProblemDetails>, ProblemHttpResult>> Preview(
        ClassificationPreviewRequest request,
        ClassificationPreviewService service,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var idError = ApiValidation.ValidatePublicRecordId(
            request.SourceId,
            "sourceId",
            "Invalid classification preview request.",
            out var sourceId);
        if (idError is not null || sourceId is null)
        {
            return TypedResults.BadRequest(idError!);
        }

        var contentError = ApiValidation.ValidateRequiredText(
            request.Content,
            "content",
            ApiValidation.SourceContentMaxLength,
            "Invalid classification preview request.");
        if (contentError is not null)
        {
            return TypedResults.BadRequest(contentError);
        }

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = $"audit-{Guid.NewGuid():N}",
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = ServiceTokenAuthorization.GetActor(httpContext),
            Action = "classification.provider.invoked",
            SubjectId = sourceId.Value,
            PayloadClass = service.ProviderBoundary.PayloadClass,
            RedactionState = service.ProviderBoundary.RedactionState
        });
        await db.SaveChangesAsync(cancellationToken);

        var normalizedRequest = request with { SourceId = sourceId.Value };
        try
        {
            return TypedResults.Ok(await service.PreviewAsync(normalizedRequest, cancellationToken));
        }
        catch (ClassificationProviderException error)
        {
            return ApiProblems.ClassificationProviderUnavailable(error);
        }
    }

    public static async Task<IResult> CheckReadiness(
        LuthnDbContext db,
        IHostEnvironment environment,
        IOptions<LuthnAuthOptions> authOptions,
        IOptions<LuthnHostOperationalOptions> hostOptions,
        IOptions<ClassificationProviderOptions> classificationOptions,
        IOperatorClassificationSettingsStore classificationSettings,
        SensitiveMemoryProtectionState sensitiveMemoryProtection,
        CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheck>();
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                checks.Add(new ReadinessCheck("database", "not_ready", "Database connection failed."));
                return NotReady("database", checks);
            }

            if (db.Database.IsRelational())
            {
                var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pendingMigrations.Any())
                {
                    checks.Add(new ReadinessCheck("database", "not_ready", "Database has pending migrations."));
                    return NotReady("database", checks);
                }
            }

            checks.Add(new ReadinessCheck("database", "ready", "Database connection and migrations are ready."));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Readiness reports dependency availability; liveness remains independent at /healthz.
            checks.Add(new ReadinessCheck("database", "not_ready", "Database readiness check failed."));
            return NotReady("database", checks);
        }

        var tokenIssue = ServiceTokenAuthorization.GetProductionReadinessIssue(
            environment,
            authOptions.Value,
            DateTimeOffset.UtcNow);
        if (tokenIssue is not null)
        {
            checks.Add(new ReadinessCheck("service-token", "not_ready", tokenIssue));
            return NotReady("service-token", checks);
        }
        checks.Add(new ReadinessCheck("service-token", "ready", "Service token configuration is ready for the current environment."));

        if (!sensitiveMemoryProtection.IsReady)
        {
            checks.Add(new ReadinessCheck(
                "sensitive-memory-protection",
                "not_ready",
                "Sensitive memory key-ring and encrypted payload verification has not completed."));
            return NotReady("sensitive-memory-protection", checks);
        }
        checks.Add(new ReadinessCheck(
            "sensitive-memory-protection",
            "ready",
            "Sensitive memory payload protection and separate key-ring verification are ready."));

        string? providerIssue;
        try
        {
            providerIssue = GetClassificationProviderReadinessIssue(
                environment,
                classificationSettings.Current,
                classificationOptions.Value);
        }
        catch (InvalidOperationException error)
        {
            providerIssue = error.Message;
        }
        if (providerIssue is not null)
        {
            checks.Add(new ReadinessCheck("classification-provider", "not_ready", providerIssue));
            return NotReady("classification-provider", checks);
        }
        var providerDetail = classificationSettings.Current.Provider == OperatorClassificationProviderKind.ExternalHttp
            ? "Self-hosted-capable ExternalHttp provider configuration is ready for the current environment."
            : "Classification provider configuration is ready for the current environment.";
        checks.Add(new ReadinessCheck("classification-provider", "ready", providerDetail));
        checks.Add(new ReadinessCheck(
            "classification-guard",
            "ready",
            $"Local secret/PII guard version {DeterministicSensitiveDataDetector.Version} is active."));

        var transportStatus = environment.IsProduction() &&
            (!hostOptions.Value.EnforceHttps && !hostOptions.Value.EnableForwardedHeaders ||
                hostOptions.Value.TrustAllForwardedHeaders)
                ? "warning"
                : "ready";
        var transportDetail = hostOptions.Value.TrustAllForwardedHeaders
            ? "Forwarded headers trust all proxies; restrict the proxy boundary before exposing production traffic."
            : transportStatus == "warning"
                ? "Production transport does not enforce HTTPS and forwarded headers are not enabled."
                : "Transport hardening configuration is explicit.";
        checks.Add(new ReadinessCheck("transport", transportStatus, transportDetail));

        return TypedResults.Ok(new ReadinessResponse("ready", "database", checks));
    }

    private static IResult NotReady(string dependency, IReadOnlyList<ReadinessCheck> checks) => TypedResults.Json(
        new ReadinessResponse("not_ready", dependency, checks),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string? GetClassificationProviderReadinessIssue(
        IHostEnvironment environment,
        OperatorClassificationProviderSettings settings,
        ClassificationProviderOptions options)
    {
        if (settings.Provider == OperatorClassificationProviderKind.Unconfigured)
        {
            return ClassificationProviderOptions.ProviderRequiredMessage;
        }

        if (settings.Provider == OperatorClassificationProviderKind.Mock)
        {
            if (!options.AllowMock)
            {
                return ClassificationProviderOptions.MockDisabledMessage;
            }

            if (!environment.IsProduction())
            {
                return null;
            }

            return "Production classification requires an operator-configured non-mock provider.";
        }

        if (settings.Provider != OperatorClassificationProviderKind.ExternalHttp && !settings.HasApiKey)
        {
            return $"{settings.Provider} provider requires an API key.";
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return $"{settings.Provider} provider requires an endpoint.";
        }

        return null;
    }

    public static async Task<Results<Ok<ContextPack>, BadRequest<ProblemDetails>>> CreateContextPack(
        ContextPackRequest request,
        ContextPackBuilder builder,
        IRetrievalCandidateSelector candidateSelector,
        IOperationalMetrics metrics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateSafeSearchRequest(
            request.Query,
            request.CoreTags,
            "Invalid context pack request.");
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var metadataError = RecallMetadataValidation.TryNormalize(
            request.ProjectKey,
            request.TaskKey,
            request.TopicTags,
            "Invalid context pack request.",
            out var recallMetadata);
        if (metadataError is not null)
        {
            return TypedResults.BadRequest(metadataError);
        }

        var normalizedRequest = new ContextPackRequest(
            request.CoreTags,
            request.MaxItems,
            request.Query,
            recallMetadata.ProjectKey,
            recallMetadata.TaskKey,
            recallMetadata.TopicTags);

        var telemetry = new SearchTelemetryScope(metrics, timeProvider, "context_pack");
        try
        {
            var candidates = await candidateSelector.SelectAgentContextAsync(
                new SafeSearchRequest(
                    normalizedRequest.Query,
                    normalizedRequest.CoreTags,
                    normalizedRequest.MaxItems,
                    normalizedRequest.ProjectKey,
                    normalizedRequest.TaskKey,
                    normalizedRequest.TopicTags),
                cancellationToken);
            var pack = builder.Build(normalizedRequest, candidates) with
            {
                RetrievalId = telemetry.RetrievalId
            };
            telemetry.Complete(pack.Items.Count);
            return TypedResults.Ok(pack);
        }
        catch (TimeoutException)
        {
            telemetry.Timeout();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Canceled();
            throw;
        }
        catch
        {
            telemetry.Error();
            throw;
        }
    }

    public static async Task<Results<Ok<SafeSearchResponse>, BadRequest<ProblemDetails>>> SearchAgentContext(
        SafeSearchRequest request,
        IRetrievalBackend retrievalBackend,
        IRetrievalCandidateSelector candidateSelector,
        IOperationalMetrics metrics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateSafeSearchRequest(
            request.Query,
            request.CoreTags,
            "Invalid agent search request.");
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        SafeSearchRequest normalizedRequest;
        try
        {
            normalizedRequest = new SafeSearchRequest(
                request.Query,
                request.CoreTags,
                request.MaxItems,
                request.ProjectKey,
                request.TaskKey,
                request.TopicTags);
        }
        catch (ArgumentException error)
        {
            return TypedResults.BadRequest(ApiValidation.CreateProblem(
                "Invalid agent search request.",
                error.Message));
        }

        var telemetry = new SearchTelemetryScope(metrics, timeProvider, "agent_search");
        try
        {
            var candidates = await candidateSelector.SelectAgentContextAsync(normalizedRequest, cancellationToken);
            var search = retrievalBackend.Search(normalizedRequest, candidates) with
            {
                RetrievalId = telemetry.RetrievalId
            };
            telemetry.Complete(search.Results.Count);
            return TypedResults.Ok(search);
        }
        catch (TimeoutException)
        {
            telemetry.Timeout();
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Canceled();
            throw;
        }
        catch
        {
            telemetry.Error();
            throw;
        }
    }

    public static async Task<Results<ContentHttpResult, NotFound>> ReadWikiProposal(
        string id,
        LuthnDbContext db,
        WikiMarkdownRenderer renderer,
        CancellationToken cancellationToken)
    {
        var proposal = await db.WikiProposals
            .AsNoTracking()
            .Where(record => record.Id == id &&
                record.AllowsAgentContext &&
                record.Sensitivity == SensitivityLevel.Public)
            .Select(record => new WikiMarkdownProjection(
                record.Id,
                record.Title,
                record.SafeSummary,
                record.Sensitivity,
                record.CoreTags,
                new[]
                {
                    new WikiSourceReference(
                        record.SourceEventId,
                        "source-event",
                        "redacted-summary",
                        "safe-projection-only",
                        "Safe projection only")
                }))
            .SingleOrDefaultAsync(cancellationToken);

        if (proposal is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Content(renderer.Render(proposal), "text/markdown");
    }

    private static ProblemDetails? ValidateSafeSearchRequest(
        string? query,
        IReadOnlyList<string>? coreTags,
        string title)
    {
        var queryError = ApiValidation.ValidateOptionalSearchQuery(query, title);
        if (queryError is not null)
        {
            return queryError;
        }

        return ApiValidation.ValidateCoreTags(coreTags, "coreTags", title, required: false);
    }
}

public sealed record HealthResponse(string Status);
public sealed record ReadinessCheck(string Name, string Status, string Detail);
public sealed record ReadinessResponse(
    string Status,
    string Dependency,
    IReadOnlyList<ReadinessCheck> Checks);
