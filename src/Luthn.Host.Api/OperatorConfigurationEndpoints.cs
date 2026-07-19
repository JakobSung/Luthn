using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Luthn.Host.Api;

public static class OperatorConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapOperatorConfiguration(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator")
            .RequireServiceScope(ServiceScopes.ConfigWrite);

        group.MapGet("/classification-provider", ReadClassificationProvider)
            .WithName("ReadClassificationProviderConfiguration");

        group.MapPut("/classification-provider", SaveClassificationProvider)
            .WithName("SaveClassificationProviderConfiguration");

        group.MapPost("/classification-provider/test", TestClassificationProvider)
            .WithName("TestClassificationProviderConfiguration");

        return app;
    }

    public static async Task<Ok<ClassificationProviderConfigurationResponse>> ReadClassificationProvider(
        IOperatorClassificationSettingsStore settingsStore,
        IOptions<ClassificationProviderOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.ReadAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(settings, options.Value));
    }

    public static async Task<Results<Ok<ClassificationProviderConfigurationResponse>, BadRequest<ProblemDetails>>> SaveClassificationProvider(
        SaveClassificationProviderConfigurationRequest request,
        IOperatorClassificationSettingsStore settingsStore,
        IOptions<ClassificationProviderOptions> options,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.SaveAsync(request, cancellationToken);
            db.AuditEvents.Add(CreateAudit(
                httpContext,
                "operator.classification_provider.updated",
                settings.Provider.ToString(),
                settings));
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToResponse(settings, options.Value));
        }
        catch (InvalidOperationException error)
        {
            return TypedResults.BadRequest(CreateProblem(error.Message));
        }
    }

    public static async Task<Results<Ok<TestClassificationProviderConfigurationResponse>, BadRequest<ProblemDetails>>> TestClassificationProvider(
        TestClassificationProviderConfigurationRequest request,
        IOperatorClassificationSettingsStore settingsStore,
        ConfiguredContentClassifier classifier,
        IPolicyEngine policyEngine,
        IOptions<ClassificationProviderOptions> options,
        LuthnDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateTestRequest(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        try
        {
            var settings = await settingsStore.ReadAsync(cancellationToken);
            var sourceId = new PublicRecordId("operator-provider-test");
            var classification = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
                sourceId,
                string.IsNullOrWhiteSpace(request.Content)
                    ? "Public implementation note for provider connectivity testing."
                    : request.Content,
                string.IsNullOrWhiteSpace(request.SourceType) ? "note" : request.SourceType,
                cancellationToken));
            var decision = policyEngine.Decide(classification);
            db.AuditEvents.Add(CreateAudit(
                httpContext,
                "operator.classification_provider.tested",
                settings.Provider.ToString(),
                settings));
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new TestClassificationProviderConfigurationResponse(
                ToResponse(settings, options.Value),
                new ClassificationPreviewClassification(
                    classification.Sensitivity,
                    classification.Confidence,
                    classification.Categories,
                    classification.ContainsSensitiveMaterial),
                decision));
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or JsonException or ClassificationProviderException)
        {
            return TypedResults.BadRequest(CreateProblem(error.Message));
        }
    }

    private static ClassificationProviderConfigurationResponse ToResponse(
        OperatorClassificationProviderSettings settings,
        ClassificationProviderOptions options) =>
        new(
            settings.Provider.ToString(),
            settings.Model,
            settings.Endpoint,
            settings.AuthHeaderName,
            settings.PayloadClass,
            settings.RedactionState,
            settings.HasApiKey,
            options.AllowMock,
            StatusFor(settings, options),
            StatusDetailFor(settings, options));

    private static string StatusFor(
        OperatorClassificationProviderSettings settings,
        ClassificationProviderOptions options) =>
        settings.Provider switch
        {
            OperatorClassificationProviderKind.Unconfigured => "unconfigured",
            OperatorClassificationProviderKind.Mock when !options.AllowMock => "mock-disabled",
            OperatorClassificationProviderKind.Mock => "mock-non-production",
            _ => "configured"
        };

    private static string StatusDetailFor(
        OperatorClassificationProviderSettings settings,
        ClassificationProviderOptions options) =>
        settings.Provider switch
        {
            OperatorClassificationProviderKind.Unconfigured => ClassificationProviderOptions.ProviderRequiredMessage,
            OperatorClassificationProviderKind.Mock when !options.AllowMock => ClassificationProviderOptions.MockDisabledMessage,
            OperatorClassificationProviderKind.Mock =>
                "Mock classification is enabled for explicit development or test use only.",
            _ => $"{settings.Provider} classification is configured."
        };

    private static AuditEventRecord CreateAudit(
        HttpContext httpContext,
        string action,
        string subjectId,
        OperatorClassificationProviderSettings settings) =>
        new()
        {
            Id = $"audit-{Guid.NewGuid():N}",
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = ServiceTokenAuthorization.GetActor(httpContext),
            Action = action,
            SubjectId = subjectId,
            PayloadClass = settings.PayloadClass,
            RedactionState = settings.RedactionState
        };

    private static ProblemDetails CreateProblem(string detail) =>
        new()
        {
            Title = "Invalid classification provider configuration.",
            Detail = detail
        };

    private static ProblemDetails? ValidateTestRequest(TestClassificationProviderConfigurationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            var contentError = ApiValidation.ValidateRequiredText(
                request.Content,
                "content",
                ApiValidation.SourceContentMaxLength,
                "Invalid classification provider test request.");
            if (contentError is not null)
            {
                return contentError;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType) &&
            request.SourceType.Trim().Length > ApiValidation.SourceTextMaxLength)
        {
            return ApiValidation.CreateProblem(
                "Invalid classification provider test request.",
                $"sourceType must be {ApiValidation.SourceTextMaxLength} characters or fewer.");
        }

        return null;
    }
}
