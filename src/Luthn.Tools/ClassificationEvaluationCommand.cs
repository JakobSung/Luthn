using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.AgentConnector.Http;
using Luthn.Core.Classification;
using Luthn.Core.Common;
using Luthn.Core.Policy;
using Luthn.Sdk.Classification;

namespace Luthn.Tools;

public interface IClassificationPreviewClient
{
    Task<ClassificationPreviewDto> ClassifyPreviewAsync(
        ClassificationPreviewRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed class ClassificationEvaluationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Func<Uri, string?, IClassificationPreviewClient> _previewClientFactory;
    private readonly Func<string, string?> _readEnvironmentVariable;

    public ClassificationEvaluationCommand()
        : this(
            (baseUrl, bearerToken) => new LuthnClassificationPreviewClient(new LuthnClientOptions
            {
                BaseUrl = baseUrl,
                BearerToken = bearerToken
            }),
            Environment.GetEnvironmentVariable)
    {
    }

    public ClassificationEvaluationCommand(
        Func<Uri, string?, IClassificationPreviewClient> previewClientFactory,
        Func<string, string?> readEnvironmentVariable)
    {
        _previewClientFactory = previewClientFactory;
        _readEnvironmentVariable = readEnvironmentVariable;
    }

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = ParseOptions(args);
            var dataset = await LoadDatasetAsync(options.DatasetPath, cancellationToken);
            var evaluator = new ClassificationGoldenEvaluator();
            var report = options.Provider switch
            {
                EvaluationProvider.Mock => await EvaluateMockAsync(evaluator, dataset, cancellationToken),
                EvaluationProvider.ConfiguredApi => await EvaluateConfiguredApiAsync(
                    evaluator,
                    dataset,
                    options,
                    cancellationToken),
                _ => throw new InvalidOperationException("Unsupported classification evaluation provider.")
            };

            var json = JsonSerializer.Serialize(report, JsonOptions);
            await output.WriteLineAsync(json);
            if (options.OutputPath is not null)
            {
                var fullOutputPath = Path.GetFullPath(options.OutputPath);
                var outputDirectory = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                await File.WriteAllTextAsync(fullOutputPath, json + Environment.NewLine, cancellationToken);
            }

            return 0;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            JsonException or
            HttpRequestException or
            IOException or
            UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    private static async Task<ClassificationGoldenDataset> LoadDatasetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var dataset = JsonSerializer.Deserialize<ClassificationGoldenDataset>(json, JsonOptions)
            ?? throw new InvalidOperationException("Classification golden dataset is empty.");
        ClassificationGoldenDatasetValidator.Validate(dataset);
        return dataset;
    }

    private static Task<ClassificationEvaluationReport> EvaluateMockAsync(
        ClassificationGoldenEvaluator evaluator,
        ClassificationGoldenDataset dataset,
        CancellationToken cancellationToken)
    {
        var classifier = new MockContentClassifier();
        var policyEngine = new PolicyEngine();
        return evaluator.EvaluateAsync(
            dataset,
            "mock",
            async (goldenCase, caseCancellationToken) =>
            {
                var classification = ClassificationResultNormalizer.Normalize(await classifier.ClassifyAsync(
                    new PublicRecordId($"golden-{goldenCase.Id}"),
                    ComposeInput(goldenCase),
                    goldenCase.SourceType,
                    caseCancellationToken));
                return new ClassificationEvaluationObservation(
                    classification,
                    policyEngine.Decide(classification));
            },
            cancellationToken);
    }

    private async Task<ClassificationEvaluationReport> EvaluateConfiguredApiAsync(
        ClassificationGoldenEvaluator evaluator,
        ClassificationGoldenDataset dataset,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.AllowExternalProvider)
        {
            throw new InvalidOperationException(
                "configured-api evaluation requires --allow-external-provider because the configured classifier may send corpus text outside the local boundary.");
        }

        if (options.ApiUrl is null)
        {
            throw new InvalidOperationException("configured-api evaluation requires --api-url <absolute-url>.");
        }

        string? bearerToken = null;
        if (options.TokenEnvironmentVariable is not null)
        {
            bearerToken = _readEnvironmentVariable(options.TokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                throw new InvalidOperationException(
                    $"Configured token environment variable '{options.TokenEnvironmentVariable}' is not set.");
            }
        }

        var client = _previewClientFactory(options.ApiUrl, bearerToken);
        return await evaluator.EvaluateAsync(
            dataset,
            "configured-api",
            async (goldenCase, caseCancellationToken) =>
            {
                var expectedSourceId = $"golden-{goldenCase.Id}";
                var response = await client.ClassifyPreviewAsync(
                    new ClassificationPreviewRequestDto(
                        expectedSourceId,
                        ComposeInput(goldenCase),
                        goldenCase.SourceType),
                    caseCancellationToken);
                if (!string.Equals(response.SourceId, expectedSourceId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Classification preview source id mismatch: expected '{expectedSourceId}', received '{response.SourceId}'.");
                }

                return MapObservation(response);
            },
            cancellationToken);
    }

    private static ClassificationEvaluationObservation MapObservation(ClassificationPreviewDto response)
    {
        if (response.Classification is null || response.StorageDecision is null)
        {
            throw new InvalidOperationException("Classification preview returned an incomplete response.");
        }

        if (response.Classification.Categories is null || response.StorageDecision.Reasons is null)
        {
            throw new InvalidOperationException("Classification preview returned an incomplete classification contract.");
        }

        if (!Enum.TryParse<SensitivityLevel>(response.Classification.Sensitivity, true, out var sensitivity) ||
            !Enum.IsDefined(sensitivity))
        {
            throw new InvalidOperationException(
                $"Classification preview returned unsupported sensitivity '{response.Classification.Sensitivity}'.");
        }

        if (!Enum.TryParse<StorageDecisionKind>(response.StorageDecision.Kind, true, out var decisionKind) ||
            !Enum.IsDefined(decisionKind))
        {
            throw new InvalidOperationException(
                $"Classification preview returned unsupported storage decision '{response.StorageDecision.Kind}'.");
        }

        var classification = ClassificationResultNormalizer.Normalize(new ClassificationResult(
            new PublicRecordId(response.SourceId),
            sensitivity,
            response.Classification.Confidence,
            response.Classification.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase),
            response.Classification.ContainsSensitiveMaterial));
        var decision = new StorageDecision(
            decisionKind,
            response.StorageDecision.Reasons,
            response.StorageDecision.AllowsWikiProjection,
            response.StorageDecision.AllowsAgentContext,
            response.StorageDecision.RequiresHumanReview);
        return new ClassificationEvaluationObservation(classification, decision);
    }

    private static string ComposeInput(ClassificationGoldenCase goldenCase) =>
        AgentVisibleClassificationInput.Compose(
            goldenCase.Content,
            goldenCase.Title,
            goldenCase.SafeSummary,
            goldenCase.CoreTags);

    private static CommandOptions ParseOptions(IReadOnlyList<string> args)
    {
        var datasetPath = FindDefaultDatasetPath();
        string? outputPath = null;
        var provider = EvaluationProvider.Mock;
        Uri? apiUrl = null;
        var allowExternalProvider = false;
        string? tokenEnvironmentVariable = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--dataset":
                    datasetPath = ReadValue(args, ref index, "--dataset");
                    break;
                case "--output":
                    outputPath = ReadValue(args, ref index, "--output");
                    break;
                case "--provider":
                    provider = ReadProvider(ReadValue(args, ref index, "--provider"));
                    break;
                case "--api-url":
                    var apiUrlValue = ReadValue(args, ref index, "--api-url");
                    if (!Uri.TryCreate(apiUrlValue, UriKind.Absolute, out apiUrl))
                    {
                        throw new ArgumentException("--api-url must be an absolute URL.");
                    }
                    break;
                case "--allow-external-provider":
                    allowExternalProvider = true;
                    break;
                case "--token-env":
                    tokenEnvironmentVariable = ReadValue(args, ref index, "--token-env");
                    break;
                default:
                    throw new ArgumentException($"Unknown classification-eval option '{args[index]}'.");
            }
        }

        return new CommandOptions(
            Path.GetFullPath(datasetPath),
            outputPath,
            provider,
            apiUrl,
            allowExternalProvider,
            tokenEnvironmentVariable);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        index++;
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static EvaluationProvider ReadProvider(string value) =>
        value.ToLowerInvariant() switch
        {
            "mock" => EvaluationProvider.Mock,
            "configured-api" => EvaluationProvider.ConfiguredApi,
            _ => throw new ArgumentException("--provider must be 'mock' or 'configured-api'.")
        };

    private static string FindDefaultDatasetPath()
    {
        var workingDirectoryPath = Path.Combine("data", "classification", "golden-v1.json");
        if (File.Exists(workingDirectoryPath))
        {
            return workingDirectoryPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "data", "classification", "golden-v1.json");
    }

    private sealed record CommandOptions(
        string DatasetPath,
        string? OutputPath,
        EvaluationProvider Provider,
        Uri? ApiUrl,
        bool AllowExternalProvider,
        string? TokenEnvironmentVariable);

    private enum EvaluationProvider
    {
        Mock,
        ConfiguredApi
    }

    private sealed class LuthnClassificationPreviewClient(LuthnClientOptions options) : IClassificationPreviewClient
    {
        private readonly LuthnClient _client = new(options);

        public Task<ClassificationPreviewDto> ClassifyPreviewAsync(
            ClassificationPreviewRequestDto request,
            CancellationToken cancellationToken = default) =>
            _client.ClassifyPreviewAsync(request, cancellationToken);
    }
}
