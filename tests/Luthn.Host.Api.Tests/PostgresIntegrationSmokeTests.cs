using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luthn.Core.Persistence;
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
