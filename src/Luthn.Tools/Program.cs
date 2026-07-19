using Luthn.Core.Classification;
using Luthn.Core.Context;
using Luthn.Core.Persistence;
using Luthn.Core.Policy;
using Luthn.Core.Wiki;
using Luthn.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

const string InitialCreateMigrationId = "20260703000455_InitialCreate";
const string EfCoreProductVersion = "10.0.0";

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();

switch (command)
{
    case "preview":
        await RunPreview(args);
        break;
    case "classification-eval":
        Environment.ExitCode = await new ClassificationEvaluationCommand().ExecuteAsync(
            args.Skip(1).ToArray(),
            Console.Out,
            Console.Error);
        break;
    case "context":
        RunContext();
        break;
    case "wiki-render":
        RunWikiRender();
        break;
    case "seed-demo":
        await RunSeedDemo();
        break;
    case "migrate-db":
        await RunMigrateDb();
        break;
    case "migration-script":
        RunMigrationScript();
        break;
    case "token-digest":
        await RunTokenDigest(args);
        break;
    case null:
        PrintUsage();
        break;
    case "":
        PrintUsage();
        break;
    default:
        PrintUsage();
        Environment.ExitCode = 1;
        break;
}

static async Task RunSeedDemo()
{
    await using var db = CreatePostgresDbContext();
    await ApplyMigrationsWithLegacyBaselineAsync(db);

    var result = await DemoDataSeeder.SeedAsync(db);
    var status = result.Created ? "seeded" : "already present";
    Console.WriteLine($"{status}: {result.WikiProposalId}");
}

static async Task RunMigrateDb()
{
    await using var db = CreatePostgresDbContext();
    await ApplyMigrationsWithLegacyBaselineAsync(db);
    Console.WriteLine("database migrations applied");
}

static async Task ApplyMigrationsWithLegacyBaselineAsync(LuthnDbContext db)
{
    if (await HasLegacyEnsureCreatedSchemaWithoutMigrationsAsync(db))
    {
        await StampInitialCreateBaselineAsync(db);
    }

    await db.Database.MigrateAsync();
}

static async Task<bool> HasLegacyEnsureCreatedSchemaWithoutMigrationsAsync(LuthnDbContext db)
{
    var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
    if (appliedMigrations.Any())
    {
        return false;
    }

    var sourceEventsTableExists = await db.Database
        .SqlQueryRaw<int>(
            "SELECT CASE WHEN to_regclass('public.source_events') IS NULL THEN 0 ELSE 1 END AS \"Value\"")
        .SingleAsync();
    var wikiProposalsTableExists = await db.Database
        .SqlQueryRaw<int>(
            "SELECT CASE WHEN to_regclass('public.wiki_proposals') IS NULL THEN 0 ELSE 1 END AS \"Value\"")
        .SingleAsync();

    return sourceEventsTableExists == 1 && wikiProposalsTableExists == 1;
}

static async Task StampInitialCreateBaselineAsync(LuthnDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """);

    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ({InitialCreateMigrationId}, {EfCoreProductVersion})
        ON CONFLICT ("MigrationId") DO NOTHING;
        """);
}

static void RunMigrationScript()
{
    using var db = CreatePostgresDbContext();
    var migrator = db.GetService<IMigrator>();
    Console.WriteLine(migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent));
}

static async Task RunTokenDigest(string[] args)
{
    if (!string.Equals(args.ElementAtOrDefault(1), "--stdin", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("usage: dotnet run --project src/Luthn.Tools -- token-digest --stdin");
        Console.Error.WriteLine("Read the service token from standard input to avoid shell history exposure.");
        Environment.ExitCode = 2;
        return;
    }

    var token = ServiceTokenDigest.ReadTokenFromStdin(await Console.In.ReadToEndAsync());
    if (token.Length == 0)
    {
        Console.Error.WriteLine("service token input is required");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine(ServiceTokenDigest.CreateSha256Digest(token));
}

static LuthnDbContext CreatePostgresDbContext()
{
    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__LuthnDb")
        ?? Environment.GetEnvironmentVariable("LUTHN_DB_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=luthn;Username=luthn";
    var optionsBuilder = new DbContextOptionsBuilder<LuthnDbContext>();
    optionsBuilder.UseLuthnPostgres(new LuthnDatabaseOptions(connectionString));

    return new LuthnDbContext(optionsBuilder.Options);
}

static async Task RunPreview(string[] args)
{
    var sourceId = args.ElementAtOrDefault(1) ?? "local-source";
    var content = args.ElementAtOrDefault(2) ?? "Public implementation note.";
    var service = new ClassificationPreviewService(
        new MockContentClassifier(),
        new PolicyEngine());

    Console.Error.WriteLine(MockContentClassifier.UsageNotice);

    var response = await service.PreviewAsync(new ClassificationPreviewRequest(sourceId, content, "cli"));

    Console.WriteLine($"source: {response.SourceId}");
    Console.WriteLine($"sensitivity: {response.Classification.Sensitivity}");
    Console.WriteLine($"decision: {response.StorageDecision.Kind}");
}

static void RunContext()
{
    var builder = new ContextPackBuilder();
    var pack = builder.Build(
        new ContextPackRequest(["runbook"], 10),
        [
            new ContextPackCandidate(
                "wiki-local-runbook",
                "Local runbook",
                "Use safe summaries and Core tags for agent context.",
                SensitivityLevel.Public,
                ["runbook", "local"],
                AllowsAgentContext: true),
            new ContextPackCandidate(
                "wiki-sensitive-placeholder",
                "Sensitive placeholder",
                "Redacted placeholder.",
                SensitivityLevel.Confidential,
                ["contract"],
                AllowsAgentContext: false)
        ]);

    foreach (var item in pack.Items)
    {
        Console.WriteLine($"{item.Id}: {item.Title}");
    }
}

static void RunWikiRender()
{
    var renderer = new WikiMarkdownRenderer();
    var markdown = renderer.Render(new WikiMarkdownProjection(
        "wiki-local-runbook",
        "Local runbook",
        "Use safe summaries and Core tags for agent context.",
        SensitivityLevel.Public,
        ["runbook", "local"],
        [new WikiSourceReference("local-source", "source-event", "redacted-summary", "safe-projection-only", "Safe projection only")]));

    Console.WriteLine(markdown);
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- preview [source-id] [content]");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- classification-eval [--dataset path] [--output path]");
    Console.WriteLine("    [--provider mock|guarded-mock|configured-api] [--api-url url] [--allow-external-provider] [--token-env name]");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- context");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- wiki-render");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- seed-demo");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- migrate-db");
    Console.WriteLine("  dotnet run --project src/Luthn.Tools -- migration-script");
    Console.WriteLine("  printf '%s' \"$LUTHN_SERVICE_VALUE\" | dotnet run --project src/Luthn.Tools -- token-digest --stdin");
}
