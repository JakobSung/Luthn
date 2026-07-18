using System.Net;
using System.Diagnostics;

namespace Luthn.Host.Api;

public sealed record ClassificationProviderRuntimeOptions
{
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxAttempts { get; init; } = 2;
    public int RetryDelayMilliseconds { get; init; } = 200;

    public TimeSpan EffectiveTimeout =>
        TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300));

    public int EffectiveMaxAttempts =>
        Math.Clamp(MaxAttempts, 1, 5);

    public TimeSpan EffectiveRetryDelay =>
        TimeSpan.FromMilliseconds(Math.Clamp(RetryDelayMilliseconds, 0, 5_000));
}

public sealed class ClassificationProviderException : Exception
{
    public ClassificationProviderException(string message)
        : base(message)
    {
    }

    public ClassificationProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class ClassificationProviderHttp
{
    public static async Task<HttpResponseMessage> SendAsync(
        IHttpClientFactory httpClientFactory,
        string clientName,
        Func<HttpRequestMessage> createRequest,
        ClassificationProviderRuntimeOptions runtimeOptions,
        ILogger logger,
        string providerName,
        IOperationalMetrics metrics,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(clientName);
        var maxAttempts = runtimeOptions.EffectiveMaxAttempts;
        var timeout = runtimeOptions.EffectiveTimeout;
        var retryDelay = runtimeOptions.EffectiveRetryDelay;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            var attemptRecorded = false;
            void RecordAttempt(string outcome)
            {
                if (attemptRecorded)
                {
                    return;
                }

                attemptRecorded = true;
                metrics.RecordClassificationProviderRequest(
                    providerName,
                    outcome,
                    Stopwatch.GetElapsedTime(started));
            }

            using var request = createRequest();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            LuthnHostMetrics.ClassificationProviderAttempts.Add(
                1,
                new KeyValuePair<string, object?>("provider", providerName));

            try
            {
                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeoutSource.Token);

                if (response.IsSuccessStatusCode)
                {
                    RecordAttempt("succeeded");
                    return response;
                }

                if (attempt < maxAttempts && IsTransient(response.StatusCode))
                {
                    RecordAttempt("retry");
                    logger.LogWarning(
                        "Classification provider {ProviderName} returned transient HTTP {StatusCode} on attempt {Attempt}.",
                        providerName,
                        (int)response.StatusCode,
                        attempt);
                    LuthnHostMetrics.ClassificationProviderRetries.Add(
                        1,
                        new KeyValuePair<string, object?>("provider", providerName),
                        new KeyValuePair<string, object?>("reason", "http_status"));
                    response.Dispose();
                    await DelayAsync(retryDelay, cancellationToken);
                    continue;
                }

                var statusCode = (int)response.StatusCode;
                RecordAttempt("http_failure");
                response.Dispose();
                LuthnHostMetrics.ClassificationProviderFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", providerName),
                    new KeyValuePair<string, object?>("reason", "http_status"));
                throw new ClassificationProviderException(
                    $"Classification provider request failed with HTTP {statusCode}.");
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                RecordAttempt(attempt < maxAttempts ? "retry" : "timeout");
                lastError = error;
                if (attempt < maxAttempts)
                {
                    logger.LogWarning(
                        "Classification provider {ProviderName} timed out on attempt {Attempt}.",
                        providerName,
                        attempt);
                    LuthnHostMetrics.ClassificationProviderRetries.Add(
                        1,
                        new KeyValuePair<string, object?>("provider", providerName),
                        new KeyValuePair<string, object?>("reason", "timeout"));
                    await DelayAsync(retryDelay, cancellationToken);
                    continue;
                }

                LuthnHostMetrics.ClassificationProviderFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", providerName),
                    new KeyValuePair<string, object?>("reason", "timeout"));
                throw new ClassificationProviderException(
                    $"Classification provider request timed out after {timeout.TotalSeconds:0} seconds.",
                    error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordAttempt("canceled");
                throw;
            }
            catch (HttpRequestException error) when (attempt < maxAttempts)
            {
                RecordAttempt("retry");
                lastError = error;
                logger.LogWarning(
                    error,
                    "Classification provider {ProviderName} request failed on attempt {Attempt}.",
                    providerName,
                    attempt);
                LuthnHostMetrics.ClassificationProviderRetries.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", providerName),
                    new KeyValuePair<string, object?>("reason", "http_exception"));
                await DelayAsync(retryDelay, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                RecordAttempt("http_exception");
                LuthnHostMetrics.ClassificationProviderFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", providerName),
                    new KeyValuePair<string, object?>("reason", "http_exception"));
                throw new ClassificationProviderException(
                    "Classification provider request failed.",
                    error);
            }
        }

        LuthnHostMetrics.ClassificationProviderFailures.Add(
            1,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("reason", "exhausted"));
        throw new ClassificationProviderException(
            "Classification provider request failed.",
            lastError ?? new InvalidOperationException("Provider attempts were exhausted."));
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
}
