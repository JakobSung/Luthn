using System.Text.Json.Serialization;
using Luthn.Host.Api;
using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Policy;
using Luthn.Core.Search;
using Luthn.Core.Wiki;
using Luthn.Core.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = ApiValidation.RequestBodyMaxBytes;
});

var operatorConfigDirectory = builder.Configuration["Luthn:OperatorConfig:Directory"] ?? ".luthn/operator";
var classificationOptions = builder.Configuration
    .GetSection("Luthn:Classification")
    .Get<ClassificationProviderOptions>() ?? new ClassificationProviderOptions();
classificationOptions.ResolveProvider();
var hostOptions = builder.Configuration
    .GetSection("Luthn:Host")
    .Get<LuthnHostOperationalOptions>() ?? new LuthnHostOperationalOptions();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(operatorConfigDirectory, "keys")));
builder.Services.AddSingleton<ISensitiveMemoryPayloadProtector, DataProtectionSensitiveMemoryPayloadProtector>();
builder.Services.AddSingleton<IHubIngressCapsuleProtector, DataProtectionHubIngressCapsuleProtector>();
builder.Services.AddSingleton<SensitiveMemoryProtectionState>();
builder.Services.AddScoped<SensitiveMemoryPayloadMigrator>();
builder.Services.Configure<OperatorConfigOptions>(builder.Configuration.GetSection("Luthn:OperatorConfig"));
builder.Services.AddSingleton<IOperatorClassificationSettingsStore, OperatorClassificationSettingsStore>();
builder.Services.Configure<LuthnHostOperationalOptions>(builder.Configuration.GetSection("Luthn:Host"));
builder.Services.AddOptions<LuthnMemoryOptions>()
    .Bind(builder.Configuration.GetSection("Luthn:Memory"))
    .Validate(
        options => options.HasValidAutomaticTurnRetention,
        LuthnMemoryOptions.AutomaticTurnRetentionValidationMessage)
    .Validate(
        options => options.HasValidAutomaticTurnCleanupInterval,
        LuthnMemoryOptions.AutomaticTurnCleanupIntervalValidationMessage)
    .Validate(
        options => options.HasValidAutomaticTurnCleanupBatch,
        LuthnMemoryOptions.AutomaticTurnCleanupBatchValidationMessage)
    .ValidateOnStart();
builder.Services.AddScoped<
    IAutomaticTurnRetentionCleanupProcessor,
    AutomaticTurnRetentionCleanupProcessor>();
builder.Services.AddHostedService<AutomaticTurnRetentionCleanupHostedService>();
builder.Services.AddOptions<AuditRetentionOptions>()
    .Bind(builder.Configuration.GetSection("Luthn:Audit:Retention"))
    .Validate(
        options => options.HasValidCleanupInterval,
        "Audit cleanup interval must be between 1 and 1440 minutes.")
    .Validate(
        options => options.HasValidCleanupBatch,
        "Audit cleanup batch size must be between 1 and 1000.")
    .Validate(
        options => options.HasValidRetentionDays,
        "Audit retention days must be between 1 and 3650 for every category.")
    .ValidateOnStart();
builder.Services.AddScoped<IAuditRetentionCleanupProcessor, AuditRetentionCleanupProcessor>();
builder.Services.AddHostedService<AuditRetentionCleanupHostedService>();
builder.Services.Configure<ClassificationProviderRuntimeOptions>(builder.Configuration.GetSection("Luthn:Classification:Runtime"));
builder.Services.AddHttpClient(nameof(ConfiguredContentClassifier), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddScoped<ConfiguredContentClassifier>();
builder.Services.AddSingleton<DeterministicSensitiveDataDetector>();
builder.Services.AddScoped<IContentClassifier>(provider =>
    new HybridContentClassifier(
        provider.GetRequiredService<ConfiguredContentClassifier>(),
        provider.GetRequiredService<DeterministicSensitiveDataDetector>()));
builder.Services.Configure<ClassificationProviderOptions>(builder.Configuration.GetSection("Luthn:Classification"));
builder.Services.AddSingleton<IPolicyEngine, PolicyEngine>();
builder.Services.AddScoped<AgentSafeMemoryProjectionSelector>();
builder.Services.AddScoped<ClassificationPreviewService>();
builder.Services.AddSingleton<SafeSearchIndex>();
builder.Services.AddSingleton<IRetrievalBackend, DeterministicRetrievalBackend>();
builder.Services.AddScoped<IRetrievalCandidateSelector, DbBackedRetrievalCandidateSelector>();
builder.Services.AddSingleton<ContextPackBuilder>();
builder.Services.AddSingleton<WikiMarkdownRenderer>();
builder.Services.AddSingleton<IOperationalMetrics, OperationalMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSafeProjectionSyncFoundation();
builder.Services.AddProblemDetails();
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = hostOptions.EffectiveRequestTimeout
    };
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = hostOptions.EffectiveRateLimitPermitLimit,
                Window = hostOptions.EffectiveRateLimitWindow,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
if (hostOptions.EnableForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedHost |
            ForwardedHeaders.XForwardedProto;
        if (hostOptions.TrustAllForwardedHeaders)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
    });
}
builder.Services.Configure<LuthnAuthOptions>(builder.Configuration.GetSection("Luthn:Auth"));
builder.Services.Configure<LuthnIdentityOptions>(builder.Configuration.GetSection("Luthn:Identity"));
builder.Services.Configure<ConsoleAccessOptions>(builder.Configuration.GetSection(ConsoleAccessOptions.SectionName));
builder.Services.Configure<ConsoleEnrollmentOptions>(builder.Configuration.GetSection(ConsoleEnrollmentOptions.SectionName));
builder.Services.Configure<ConsoleCloudLoginOptions>(builder.Configuration.GetSection(ConsoleCloudLoginOptions.SectionName));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = ConsoleAccessOptions.AntiforgeryHeaderName;
    options.Cookie.Name = "LuthnConsoleCsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton<ConsoleLifecycleStore>();
builder.Services.AddSingleton<IConsoleLifecycleStore>(provider =>
    provider.GetRequiredService<ConsoleLifecycleStore>());
builder.Services.AddSingleton<IConsoleInstallationState>(provider =>
    provider.GetRequiredService<ConsoleLifecycleStore>());
builder.Services.AddSingleton<DisabledInstallationEnrollmentAdapter>();
builder.Services.AddSingleton<FakeInstallationEnrollmentAdapter>();
builder.Services.AddSingleton<IInstallationEnrollmentAdapter>(provider =>
    provider.GetRequiredService<IOptions<ConsoleEnrollmentOptions>>().Value.Adapter switch
    {
        Luthn.Sdk.Console.ConsoleEnrollmentAdapter.Fake =>
            provider.GetRequiredService<FakeInstallationEnrollmentAdapter>(),
        _ => provider.GetRequiredService<DisabledInstallationEnrollmentAdapter>()
    });
builder.Services.AddSingleton<IConsoleSessionStore, InMemoryConsoleSessionStore>();
builder.Services.AddSingleton<DisabledConsoleCloudLoginProvider>();
builder.Services.AddSingleton<FakeConsoleCloudLoginProvider>();
builder.Services.AddSingleton<IConsoleCloudLoginProvider>(provider =>
    provider.GetRequiredService<IOptions<ConsoleCloudLoginOptions>>().Value.Provider switch
    {
        Luthn.Sdk.Console.ConsoleCloudLoginProvider.Fake =>
            provider.GetRequiredService<FakeConsoleCloudLoginProvider>(),
        _ => provider.GetRequiredService<DisabledConsoleCloudLoginProvider>()
    });
builder.Services.AddOptions<HubIngressOptions>()
    .Bind(builder.Configuration.GetSection("Luthn:Hub:Ingress"))
    .Validate(options => options.IsValid, "Luthn Hub ingress limits are invalid.")
    .ValidateOnStart();
builder.Services.AddScoped<HubIngressQueueService>();
builder.Services.AddScoped<HubIngressQueueProcessor>();
builder.Services.AddSingleton<IHubIngressAdmissionCoordinator, HubIngressAdmissionCoordinator>();
builder.Services.AddSingleton<IHubOperationalMetrics, HubOperationalMetrics>();
builder.Services.AddScoped<HubOperationalStatusService>();
builder.Services.AddHostedService<HubIngressWorkerHostedService>();
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<LuthnDbContext>(options =>
        options.UseInMemoryDatabase(builder.Configuration["Luthn:TestingDatabaseName"] ?? "luthn-api-tests"));
}
else
{
    builder.Services.AddLuthnPersistence(builder.Configuration);
}
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});

var app = builder.Build();

if (hostOptions.EnableForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && hostOptions.EnforceHttps)
{
    app.UseHsts();
}
if (hostOptions.EnforceHttps)
{
    app.UseHttpsRedirection();
}
app.UseRequestTimeouts();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LuthnDbContext>();
    db.Database.EnsureCreated();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    try
    {
        await scope.ServiceProvider
            .GetRequiredService<SensitiveMemoryPayloadMigrator>()
            .MigrateAndVerifyAsync();
    }
    catch (InvalidOperationException)
    {
        // Liveness and readiness stay observable, but product traffic is gated below.
    }
}

app.MapLuthnApi();
app.MapConsoleSessions();
app.MapConsoleEnrollment();
app.MapConsoleCloudLogin();
app.MapOperatorConfiguration();
app.MapOperationalMetrics();
app.MapSearchTelemetry();
app.MapHubIngress();
app.MapHubOperationalStatus();

app.Run();

public partial class Program;
