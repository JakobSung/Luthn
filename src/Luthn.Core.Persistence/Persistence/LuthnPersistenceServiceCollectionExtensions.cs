using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Luthn.Core.Persistence;

public static class LuthnPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddLuthnPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var databaseOptions = LuthnDatabaseOptions.FromConfiguration(configuration);

        services.AddDbContext<LuthnDbContext>(options =>
            options.UseLuthnPostgres(databaseOptions));

        return services;
    }

    public static DbContextOptionsBuilder UseLuthnPostgres(
        this DbContextOptionsBuilder optionsBuilder,
        LuthnDatabaseOptions databaseOptions)
    {
        return optionsBuilder.UseNpgsql(
            databaseOptions.ConnectionString,
            postgresOptions =>
            {
                if (databaseOptions.EnableRetries)
                {
                    postgresOptions.EnableRetryOnFailure(
                        databaseOptions.MaxRetryCount,
                        TimeSpan.FromSeconds(databaseOptions.MaxRetryDelaySeconds),
                        errorCodesToAdd: null);
                }
            });
    }
}

public sealed record LuthnDatabaseOptions(
    string ConnectionString,
    bool EnableRetries = true,
    int MaxRetryCount = 5,
    int MaxRetryDelaySeconds = 10)
{
    public static LuthnDatabaseOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Luthn:Database");
        var connectionString = configuration.GetConnectionString("LuthnDb")
            ?? "Host=localhost;Port=5432;Database=luthn;Username=luthn";

        return new LuthnDatabaseOptions(
            connectionString,
            ParseBoolean(section["EnableRetries"], true),
            ParseInteger(section["MaxRetryCount"], 5),
            ParseInteger(section["MaxRetryDelaySeconds"], 10));
    }

    private static bool ParseBoolean(string? value, bool defaultValue) =>
        bool.TryParse(value, out var result) ? result : defaultValue;

    private static int ParseInteger(string? value, int defaultValue) =>
        int.TryParse(value, out var result) && result > 0 ? result : defaultValue;
}
