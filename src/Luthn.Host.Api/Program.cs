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
builder.Services.Configure<OperatorConfigOptions>(builder.Configuration.GetSection("Luthn:OperatorConfig"));
builder.Services.AddSingleton<IOperatorClassificationSettingsStore, OperatorClassificationSettingsStore>();
builder.Services.Configure<LuthnHostOperationalOptions>(builder.Configuration.GetSection("Luthn:Host"));
builder.Services.Configure<ClassificationProviderRuntimeOptions>(builder.Configuration.GetSection("Luthn:Classification:Runtime"));
builder.Services.AddHttpClient(nameof(ConfiguredContentClassifier), client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddScoped<ConfiguredContentClassifier>();
builder.Services.AddScoped<IContentClassifier>(provider =>
    provider.GetRequiredService<ConfiguredContentClassifier>());
builder.Services.Configure<ClassificationProviderOptions>(builder.Configuration.GetSection("Luthn:Classification"));
builder.Services.AddSingleton<IPolicyEngine, PolicyEngine>();
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

app.MapLuthnApi();
app.MapOperatorConfiguration();
app.MapOperationalMetrics();

app.Run();

public partial class Program;
