using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Luthn.Core.Persistence;
using Luthn.Sdk.Console;
using Luthn.Sdk.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ConsoleEnrollmentOptions
{
    public const string SectionName = "Luthn:Console:Enrollment";

    public ConsoleEnrollmentAdapter Adapter { get; set; } = ConsoleEnrollmentAdapter.Disabled;
    public int PendingMinutes { get; set; } = 10;
    public string ProviderLabel { get; set; } = "Luthn Cloud";

    public TimeSpan EffectivePendingLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(PendingMinutes, 2, 30));
}

internal sealed record PersistedConsoleLifecycle(
    InstallationEnrollmentState? EnrollmentState,
    string ProtectedInstallationProof,
    string InstallationFingerprint,
    DateTimeOffset? EnrollmentExpiresAt,
    DateTimeOffset? EnrolledAt,
    string PendingReference,
    IReadOnlyList<string> Capabilities);

public sealed record ConsoleLifecycleSnapshot(
    InstallationEnrollmentState? EnrollmentState,
    string InstallationFingerprint,
    DateTimeOffset? EnrollmentExpiresAt,
    DateTimeOffset? EnrolledAt,
    string PendingReference,
    IReadOnlyList<string> Capabilities)
{
    public bool IsEnrolled => EnrollmentState == InstallationEnrollmentState.Approved;
}

public interface IConsoleLifecycleStore : IConsoleInstallationState
{
    ConsoleLifecycleSnapshot Current { get; }
    ValueTask<ConsoleLifecycleSnapshot> BeginEnrollmentAsync(
        string pendingReference,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken);
    ValueTask<ConsoleLifecycleSnapshot> ActivateEnrollmentAsync(
        string pendingReference,
        string installationFingerprint,
        IReadOnlyList<string> capabilities,
        DateTimeOffset enrolledAt,
        CancellationToken cancellationToken);
}

public sealed class ConsoleLifecycleStore(
    IOptions<OperatorConfigOptions> operatorOptions,
    IDataProtectionProvider dataProtectionProvider) : IConsoleLifecycleStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Luthn.Console.InstallationProof.v1");
    private ConsoleLifecycleSnapshot? _current;

    public ConsoleLifecycleSnapshot Current => _current ??= ReadOrCreate();
    public bool IsEnrolled => Current.IsEnrolled;

    public async ValueTask<ConsoleLifecycleSnapshot> BeginEnrollmentAsync(
        string pendingReference,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            if (current.IsEnrolled)
            {
                throw new InvalidOperationException("This installation is already enrolled.");
            }

            var next = current with
            {
                EnrollmentState = InstallationEnrollmentState.Pending,
                EnrollmentExpiresAt = expiresAt,
                PendingReference = BoundReference(pendingReference),
                Capabilities = capabilities.ToArray()
            };
            await PersistAsync(next, cancellationToken);
            _current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ConsoleLifecycleSnapshot> ActivateEnrollmentAsync(
        string pendingReference,
        string installationFingerprint,
        IReadOnlyList<string> capabilities,
        DateTimeOffset enrolledAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            if (current.EnrollmentState != InstallationEnrollmentState.Pending ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(current.PendingReference),
                    System.Text.Encoding.UTF8.GetBytes(BoundReference(pendingReference))) ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(current.InstallationFingerprint),
                    System.Text.Encoding.UTF8.GetBytes(installationFingerprint)))
            {
                throw new InvalidOperationException("The enrollment grant is not bound to the active installation challenge.");
            }

            if (current.EnrollmentExpiresAt is not { } expiresAt || enrolledAt >= expiresAt)
            {
                throw new InvalidOperationException("The enrollment challenge has expired.");
            }

            var next = current with
            {
                EnrollmentState = InstallationEnrollmentState.Approved,
                EnrollmentExpiresAt = null,
                EnrolledAt = enrolledAt,
                PendingReference = "",
                Capabilities = capabilities.ToArray()
            };
            await PersistAsync(next, cancellationToken);
            _current = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ConsoleLifecycleSnapshot ReadOrCreate()
    {
        if (File.Exists(StatePath))
        {
            using var stream = File.OpenRead(StatePath);
            var persisted = JsonSerializer.Deserialize<PersistedConsoleLifecycle>(stream, SerializerOptions);
            if (persisted is not null)
            {
                _ = _protector.Unprotect(persisted.ProtectedInstallationProof);
                return new ConsoleLifecycleSnapshot(
                    persisted.EnrollmentState,
                    persisted.InstallationFingerprint,
                    persisted.EnrollmentExpiresAt,
                    persisted.EnrolledAt,
                    persisted.PendingReference,
                    persisted.Capabilities);
            }
        }

        Directory.CreateDirectory(StateDirectory);
        var proof = RandomNumberGenerator.GetBytes(32);
        var fingerprint = Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant();
        var initial = new ConsoleLifecycleSnapshot(null, fingerprint, null, null, "", []);
        Persist(initial, _protector.Protect(Convert.ToBase64String(proof)));
        return initial;
    }

    private async ValueTask PersistAsync(
        ConsoleLifecycleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var persisted = ReadPersisted();
        var next = new PersistedConsoleLifecycle(
            snapshot.EnrollmentState,
            persisted.ProtectedInstallationProof,
            snapshot.InstallationFingerprint,
            snapshot.EnrollmentExpiresAt,
            snapshot.EnrolledAt,
            snapshot.PendingReference,
            snapshot.Capabilities);
        await WriteAtomicallyAsync(next, cancellationToken);
    }

    private void Persist(ConsoleLifecycleSnapshot snapshot, string protectedProof)
    {
        var persisted = new PersistedConsoleLifecycle(
            snapshot.EnrollmentState,
            protectedProof,
            snapshot.InstallationFingerprint,
            snapshot.EnrollmentExpiresAt,
            snapshot.EnrolledAt,
            snapshot.PendingReference,
            snapshot.Capabilities);
        var temporaryPath = TemporaryPath();
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, SerializerOptions));
        File.Move(temporaryPath, StatePath);
    }

    private PersistedConsoleLifecycle ReadPersisted()
    {
        using var stream = File.OpenRead(StatePath);
        return JsonSerializer.Deserialize<PersistedConsoleLifecycle>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The console lifecycle state is invalid.");
    }

    private async ValueTask WriteAtomicallyAsync(
        PersistedConsoleLifecycle persisted,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var temporaryPath = TemporaryPath();
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(StatePath))
            {
                File.Move(temporaryPath, StatePath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, StatePath);
            }
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private string StateDirectory => Path.GetFullPath(operatorOptions.Value.Directory);
    private string StatePath => Path.Combine(StateDirectory, "console-lifecycle.json");
    private string TemporaryPath() =>
        Path.Combine(StateDirectory, $".console-lifecycle.{Guid.NewGuid():N}.tmp");

    private static string BoundReference(string value) =>
        value.Length <= 128 ? value : value[..128];
}

public sealed record EnrollmentChallenge(
    string PendingReference,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Capabilities);

public sealed record EnrollmentGrant(
    string PendingReference,
    string InstallationFingerprint,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Capabilities);

public interface IInstallationEnrollmentAdapter
{
    ConsoleEnrollmentAdapter Kind { get; }
    ValueTask<EnrollmentChallenge> BeginAsync(
        string installationFingerprint,
        CancellationToken cancellationToken);
    ValueTask<EnrollmentGrant> VerifyAsync(
        ConsoleLifecycleSnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed class DisabledInstallationEnrollmentAdapter : IInstallationEnrollmentAdapter
{
    public ConsoleEnrollmentAdapter Kind => ConsoleEnrollmentAdapter.Disabled;

    public ValueTask<EnrollmentChallenge> BeginAsync(
        string installationFingerprint,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<EnrollmentChallenge>(
            new InvalidOperationException("Cloud enrollment is disabled in this OSS installation."));

    public ValueTask<EnrollmentGrant> VerifyAsync(
        ConsoleLifecycleSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<EnrollmentGrant>(
            new InvalidOperationException("Cloud enrollment is disabled in this OSS installation."));
}

public sealed class FakeInstallationEnrollmentAdapter(
    TimeProvider timeProvider,
    IOptions<ConsoleEnrollmentOptions> options) : IInstallationEnrollmentAdapter
{
    private static readonly string[] SupportedCapabilities =
    ["console-login.v1", "safe-projection.v2", "metadata-audit.v1"];

    public ConsoleEnrollmentAdapter Kind => ConsoleEnrollmentAdapter.Fake;

    public ValueTask<EnrollmentChallenge> BeginAsync(
        string installationFingerprint,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EnrollmentChallenge(
            $"enroll-{Guid.NewGuid():N}",
            timeProvider.GetUtcNow() + options.Value.EffectivePendingLifetime,
            SupportedCapabilities));

    public ValueTask<EnrollmentGrant> VerifyAsync(
        ConsoleLifecycleSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.EnrollmentState != InstallationEnrollmentState.Pending ||
            string.IsNullOrWhiteSpace(snapshot.PendingReference) ||
            snapshot.EnrollmentExpiresAt is not { } expiresAt ||
            timeProvider.GetUtcNow() >= expiresAt)
        {
            return ValueTask.FromException<EnrollmentGrant>(
                new InvalidOperationException("There is no active enrollment challenge to verify."));
        }

        return ValueTask.FromResult(new EnrollmentGrant(
            snapshot.PendingReference,
            snapshot.InstallationFingerprint,
            expiresAt,
            SupportedCapabilities));
    }
}

public static class ConsoleEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapConsoleEnrollment(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/enrollment")
            .RequireServiceScope(ServiceScopes.ConfigWrite);
        group.MapGet("", Read).WithName("ReadConsoleEnrollment");
        group.MapPost("/start", Start).WithName("StartConsoleEnrollment");
        group.MapPost("/verify", Verify).WithName("VerifyConsoleEnrollment");
        return app;
    }

    private static Ok<ConsoleEnrollmentDto> Read(
        IConsoleLifecycleStore lifecycle,
        IInstallationEnrollmentAdapter adapter,
        IOptions<ConsoleEnrollmentOptions> options) =>
        TypedResults.Ok(ToDto(lifecycle.Current, adapter.Kind, options.Value.ProviderLabel));

    private static async Task<Results<Ok<ConsoleEnrollmentDto>, ProblemHttpResult>> Start(
        IConsoleLifecycleStore lifecycle,
        IInstallationEnrollmentAdapter adapter,
        IOptions<ConsoleEnrollmentOptions> options,
        LuthnDbContext db,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var challenge = await adapter.BeginAsync(
                lifecycle.Current.InstallationFingerprint,
                cancellationToken);
            var snapshot = await lifecycle.BeginEnrollmentAsync(
                challenge.PendingReference,
                challenge.ExpiresAt,
                challenge.Capabilities,
                cancellationToken);
            db.AuditEvents.Add(CreateAudit(context, "console.enrollment.started", "pending", timeProvider.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDto(snapshot, adapter.Kind, options.Value.ProviderLabel));
        }
        catch (InvalidOperationException error)
        {
            return EnrollmentProblem(error.Message, StatusCodes.Status409Conflict);
        }
    }

    private static async Task<Results<Ok<ConsoleEnrollmentDto>, ProblemHttpResult>> Verify(
        IConsoleLifecycleStore lifecycle,
        IInstallationEnrollmentAdapter adapter,
        IConsoleSessionStore sessions,
        IOptions<ConsoleEnrollmentOptions> options,
        LuthnDbContext db,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var grant = await adapter.VerifyAsync(lifecycle.Current, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (grant.ExpiresAt <= now)
            {
                throw new InvalidOperationException("The enrollment grant has expired.");
            }

            var snapshot = await lifecycle.ActivateEnrollmentAsync(
                grant.PendingReference,
                grant.InstallationFingerprint,
                grant.Capabilities,
                now,
                cancellationToken);
            sessions.RevokeAll(session => session.Mode == ConsoleAccessMode.LocalAuto);
            db.AuditEvents.Add(CreateAudit(context, "console.enrollment.activated", "approved", now));
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(ToDto(snapshot, adapter.Kind, options.Value.ProviderLabel));
        }
        catch (InvalidOperationException error)
        {
            return EnrollmentProblem(error.Message, StatusCodes.Status409Conflict);
        }
    }

    private static ConsoleEnrollmentDto ToDto(
        ConsoleLifecycleSnapshot snapshot,
        ConsoleEnrollmentAdapter adapter,
        string providerLabel) =>
        new(
            snapshot.EnrollmentState,
            adapter,
            snapshot.EnrollmentExpiresAt,
            snapshot.InstallationFingerprint,
            snapshot.Capabilities,
            string.IsNullOrWhiteSpace(providerLabel) ? "Cloud provider" : providerLabel.Trim()[..Math.Min(providerLabel.Trim().Length, 64)],
            snapshot.EnrollmentState switch
            {
                InstallationEnrollmentState.Pending => "verify-enrollment",
                InstallationEnrollmentState.Approved => "cloud-login",
                _ when adapter == ConsoleEnrollmentAdapter.Disabled => "enable-provider",
                _ => "start-enrollment"
            },
            true);

    private static ProblemHttpResult EnrollmentProblem(string detail, int statusCode) =>
        TypedResults.Problem(
            title: "Cloud enrollment could not continue.",
            detail: detail,
            statusCode: statusCode);

    private static AuditEventRecord CreateAudit(
        HttpContext context,
        string action,
        string outcome,
        DateTimeOffset occurredAt) =>
        AuditEventFactory.ForInstallation(
            ServiceTokenAuthorization.GetActor(context),
            action,
            "console-installation",
            "metadata-only",
            "no-content",
            occurredAt,
            actorKind: ServiceTokenAuthorization.GetActorKind(ServiceTokenAuthorization.GetPrincipal(context)),
            subjectType: "console_enrollment",
            outcome: outcome,
            actorUserId: ServiceTokenAuthorization.GetPrincipal(context).UserId);
}
