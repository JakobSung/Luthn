using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Luthn.Core.Persistence;

public sealed class LuthnDbContextFactory : IDesignTimeDbContextFactory<LuthnDbContext>
{
    public LuthnDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__LuthnDb")
            ?? Environment.GetEnvironmentVariable("LUTHN_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=luthn;Username=luthn";

        var optionsBuilder = new DbContextOptionsBuilder<LuthnDbContext>();
        optionsBuilder.UseLuthnPostgres(new LuthnDatabaseOptions(connectionString));

        return new LuthnDbContext(optionsBuilder.Options);
    }
}
