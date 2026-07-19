using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Classification;
using Luthn.Sdk.Classification;
using Luthn.Tools;

namespace Luthn.Tools.Tests;

public sealed class ClassificationEvaluationCommandTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task DefaultMockProducesKnownBaselineWithoutRawCorpusText()
    {
        var command = new ClassificationEvaluationCommand(
            (_, _) => throw new InvalidOperationException("The default mock path must not create an API client."),
            _ => null);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await command.ExecuteAsync([], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        var report = DeserializeReport(output.ToString());
        Assert.Equal("mock", report.Provider);
        Assert.Equal(20, report.Summary.TotalCount);
        Assert.Equal(17, report.Summary.PassedCount);
        Assert.Equal(2, report.Summary.FalseNegativeCount);
        Assert.Equal(1, report.Summary.FalsePositiveCount);
        Assert.Equal(3, report.Summary.SensitivityMismatchCount);
        Assert.Equal(3, report.Summary.CategoryMismatchCount);
        Assert.Equal(3, report.Summary.SensitiveFlagMismatchCount);
        Assert.Equal(3, report.Summary.RoutingMismatchCount);
        Assert.DoesNotContain("은행 계좌번호", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rotate the private key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputFileMatchesStandardOutput()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"luthn-classification-eval-{Guid.NewGuid():N}.json");
        try
        {
            var command = new ClassificationEvaluationCommand(
                (_, _) => throw new InvalidOperationException("The mock path must not create an API client."),
                _ => null);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await command.ExecuteAsync(
                ["--dataset", DatasetPath, "--output", outputPath],
                output,
                error);

            Assert.Equal(0, exitCode);
            Assert.Equal(output.ToString(), await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ConfiguredApiRequiresExplicitExternalProviderOptIn()
    {
        var factoryCalled = false;
        var command = new ClassificationEvaluationCommand(
            (_, _) =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("Factory should not be called.");
            },
            _ => null);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await command.ExecuteAsync(
            [
                "--dataset", DatasetPath,
                "--provider", "configured-api",
                "--api-url", "http://127.0.0.1:5089"
            ],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.False(factoryCalled);
        Assert.Contains("requires --allow-external-provider", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredApiMapsPerfectResponsesAndNeverPrintsToken()
    {
        const string tokenName = "LUTHN_EVAL_TEST_TOKEN";
        const string tokenValue = "do-not-print-this-token";
        var dataset = await LoadDatasetAsync();
        var client = new PerfectPreviewClient(dataset);
        Uri? capturedUrl = null;
        string? capturedToken = null;
        var command = new ClassificationEvaluationCommand(
            (url, token) =>
            {
                capturedUrl = url;
                capturedToken = token;
                return client;
            },
            name => name == tokenName ? tokenValue : null);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await command.ExecuteAsync(
            [
                "--dataset", DatasetPath,
                "--provider", "configured-api",
                "--api-url", "http://127.0.0.1:5089",
                "--allow-external-provider",
                "--token-env", tokenName
            ],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(new Uri("http://127.0.0.1:5089"), capturedUrl);
        Assert.Equal(tokenValue, capturedToken);
        Assert.Equal(20, client.CallCount);
        var report = DeserializeReport(output.ToString());
        Assert.Equal("configured-api", report.Provider);
        Assert.Equal(20, report.Summary.PassedCount);
        Assert.DoesNotContain(tokenValue, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(tokenValue, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredApiRejectsResponseForAnotherSourceId()
    {
        var dataset = await LoadDatasetAsync();
        var client = new PerfectPreviewClient(dataset, "golden-another-case");
        var command = new ClassificationEvaluationCommand((_, _) => client, _ => null);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await command.ExecuteAsync(
            [
                "--dataset", DatasetPath,
                "--provider", "configured-api",
                "--api-url", "http://127.0.0.1:5089",
                "--allow-external-provider"
            ],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Equal(1, client.CallCount);
        Assert.Contains("source id mismatch", error.ToString(), StringComparison.Ordinal);
    }

    private static string DatasetPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "classification", "golden-v1.json");

    private static ClassificationEvaluationReport DeserializeReport(string json) =>
        JsonSerializer.Deserialize<ClassificationEvaluationReport>(json, JsonOptions)
        ?? throw new InvalidOperationException("Evaluation report was empty.");

    private static async Task<ClassificationGoldenDataset> LoadDatasetAsync() =>
        JsonSerializer.Deserialize<ClassificationGoldenDataset>(
            await File.ReadAllTextAsync(DatasetPath),
            JsonOptions)
        ?? throw new InvalidOperationException("Golden dataset was empty.");

    private sealed class PerfectPreviewClient(
        ClassificationGoldenDataset dataset,
        string? responseSourceId = null) : IClassificationPreviewClient
    {
        private readonly IReadOnlyDictionary<string, ClassificationGoldenCase> _cases = dataset.Cases
            .ToDictionary(goldenCase => $"golden-{goldenCase.Id}", StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public Task<ClassificationPreviewDto> ClassifyPreviewAsync(
            ClassificationPreviewRequestDto request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var goldenCase = _cases[request.SourceId];
            var sensitive = goldenCase.ExpectedContainsSensitiveMaterial;
            return Task.FromResult(new ClassificationPreviewDto(
                responseSourceId ?? request.SourceId,
                new ClassificationResultDto(
                    goldenCase.ExpectedSensitivity.ToString(),
                    1,
                    goldenCase.ExpectedCategories,
                    sensitive),
                new StorageDecisionDto(
                    goldenCase.ExpectedStorageDecision.ToString(),
                    [],
                    !sensitive,
                    !sensitive,
                    goldenCase.ExpectedSensitivity == SensitivityLevel.Restricted)));
        }
    }
}
