using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luthn.Core.Classification;
using Luthn.Core.Persistence;
using Luthn.Core.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Luthn.Host.Api.Tests;

public sealed class PostgresIntegrationSmokeTests
{
    [Fact]
    public async Task DisposablePostgresDatabaseRunsMigrationsAndApiReadinessWhenEnabled()
    {
        var connectionString = Environment.GetEnvironmentVariable("LUTHN_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        if (!string.Equals(
            Environment.GetEnvironmentVariable("LUTHN_POSTGRES_TEST_ALLOW_RESET"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsDisposableTestDatabase(connectionString))
        {
            throw new InvalidOperationException(
                "LUTHN_POSTGRES_TEST_CONNECTION must target a disposable database whose name starts with luthn_test.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<LuthnDbContext>();
        optionsBuilder.UseLuthnPostgres(new LuthnDatabaseOptions(connectionString, EnableRetries: false));
        var options = optionsBuilder.Options;

        await using var db = new LuthnDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var pending = await db.Database.GetPendingMigrationsAsync();
        Assert.Empty(pending);

        var now = DateTimeOffset.UtcNow;
        db.WikiProposals.Add(new WikiProposalRecord
        {
            Id = "wiki-old-db-match",
            SourceEventId = "source-old-db-match",
            Title = "Needle recovery runbook",
            SafeSummary = "Public-safe database recovery steps.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["needle"],
            AllowsAgentContext = true,
            CreatedAt = now.AddDays(-2)
        });
        db.WikiProposals.AddRange(Enumerable.Range(0, 1001).Select(index => new WikiProposalRecord
        {
            Id = $"wiki-newer-db-nonmatch-{index}",
            SourceEventId = $"source-newer-db-nonmatch-{index}",
            Title = $"General database release {index}",
            SafeSummary = "Public-safe unmatched projection.",
            Sensitivity = SensitivityLevel.Public,
            CoreTags = ["release"],
            AllowsAgentContext = true,
            CreatedAt = now.AddMinutes(index)
        }));
        await db.SaveChangesAsync();

        var selector = new DbBackedRetrievalCandidateSelector(db, TimeProvider.System);
        var candidates = await selector.SelectAgentContextAsync(
            new SafeSearchRequest("needle", ["needle"], 10),
            CancellationToken.None);
        var candidate = Assert.Single(candidates);
        Assert.Equal("wiki-old-db-match", candidate.Id);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:LuthnDb", connectionString);
                builder.UseSetting("Luthn:Database:EnableRetries", "false");
                builder.UseSetting(
                    "Luthn:OperatorConfig:Directory",
                    Path.Combine(Path.GetTempPath(), "luthn-postgres-smoke", Guid.NewGuid().ToString("N")));
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/readyz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("database", body.RootElement.GetProperty("dependency").GetString());
    }

    private static bool IsDisposableTestDatabase(string connectionString) =>
        Regex.IsMatch(
            connectionString,
            @"(^|;)\s*(Database|Db)\s*=\s*luthn_test[\w-]*\s*(;|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
