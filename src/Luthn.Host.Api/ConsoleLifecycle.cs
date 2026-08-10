using Luthn.Core.Persistence;
using Luthn.Sdk.Console;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public sealed class ConsoleRecoveryOptions
{
    public const string SectionName = "Luthn:Console:Recovery";
    public ConsoleRecoveryVerifier Verifier { get; set; } = ConsoleRecoveryVerifier.Disabled;
    public bool FakeProofVerified { get; set; }
}

public interface IConsoleOfflineRecoveryVerifier
{
    ConsoleRecoveryVerifier Kind { get; }
    ValueTask<bool> VerifyAsync(CancellationToken cancellationToken);
}

public sealed class DisabledConsoleOfflineRecoveryVerifier : IConsoleOfflineRecoveryVerifier
{
    public ConsoleRecoveryVerifier Kind => ConsoleRecoveryVerifier.Disabled;
    public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class FakeConsoleOfflineRecoveryVerifier(
    IOptions<ConsoleRecoveryOptions> options,
    IHostEnvironment environment)
    : IConsoleOfflineRecoveryVerifier
{
    public ConsoleRecoveryVerifier Kind => ConsoleRecoveryVerifier.Fake;
    public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(!environment.IsProduction() && options.Value.FakeProofVerified);
}

public static class ConsoleLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapConsoleLifecycle(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/lifecycle");
        group.MapGet("", Read).WithName("ReadConsoleLifecycle");
        group.MapPost("/fake-membership-removed", RemoveMembership)
            .WithName("RemoveFakeConsoleMembership");
        group.MapPost("/fake-organization-restricted", RestrictOrganization)
            .WithName("RestrictFakeConsoleOrganization");
        group.MapPost("/reconnect", Reconnect)
            .WithName("ReconnectConsoleOrganization");
        group.MapPost("/reclaim", Reclaim)
            .WithName("ReclaimLocalConsole");
        return app;
    }

    private static Ok<ConsoleLifecycleDto> Read(
        HttpContext context,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IConsoleOfflineRecoveryVerifier recovery) =>
        TypedResults.Ok(ToDto(lifecycle.Current, sessions.Authenticate(context), recovery.Kind));

    private static async Task<Results<Ok<ConsoleLifecycleDto>, ProblemHttpResult>> RemoveMembership(
        HttpContext context,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IConsoleCloudLoginProvider provider,
        IConsoleOfflineRecoveryVerifier recovery,
        IAntiforgery antiforgery,
        LuthnDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = sessions.Authenticate(context);
        var failure = await RequireFakeCloudMutationAsync(context, session, provider, antiforgery);
        if (failure is not null)
        {
            return failure;
        }

        if (session!.CloudSubjectKey is null)
        {
            return LifecycleProblem("The Cloud subject binding is unavailable.", StatusCodes.Status409Conflict);
        }

        await lifecycle.RemoveSubjectAsync(session.CloudSubjectKey, cancellationToken);
        sessions.RevokeSubject(session.CloudSubjectKey);
        InMemoryConsoleSessionStore.DeleteCookie(context);
        await AuditAsync(
            db,
            "console.membership.removed",
            "revoked",
            timeProvider.GetUtcNow(),
            cancellationToken);
        return TypedResults.Ok(ToDto(
            lifecycle.Current,
            null,
            recovery.Kind,
            ConsoleMembershipState.Removed));
    }

    private static async Task<Results<Ok<ConsoleLifecycleDto>, ProblemHttpResult>> RestrictOrganization(
        HttpContext context,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IConsoleCloudLoginProvider provider,
        IConsoleOfflineRecoveryVerifier recovery,
        IAntiforgery antiforgery,
        LuthnDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = sessions.Authenticate(context);
        var failure = await RequireFakeCloudMutationAsync(context, session, provider, antiforgery);
        if (failure is not null)
        {
            return failure;
        }

        if (!session!.CloudOwner)
        {
            return LifecycleProblem("Only a Cloud owner can change the Organization lifecycle.", StatusCodes.Status403Forbidden);
        }

        var now = timeProvider.GetUtcNow();
        await lifecycle.RevokeConnectionAuthorityAsync(now, cancellationToken);
        await lifecycle.RestrictOrganizationAsync(cancellationToken);
        sessions.RestrictCloudSessions();
        var restricted = sessions.Authenticate(context);
        await AuditAsync(db, "console.organization.restricted", "restricted", now, cancellationToken);
        return TypedResults.Ok(ToDto(lifecycle.Current, restricted, recovery.Kind));
    }

    private static async Task<Results<Ok<ConsoleLifecycleDto>, ProblemHttpResult>> Reconnect(
        HttpContext context,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IConsoleCloudLoginProvider provider,
        IConsoleOfflineRecoveryVerifier recovery,
        IAntiforgery antiforgery,
        LuthnDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = sessions.Authenticate(context);
        var failure = await RequireCloudMutationAsync(context, session, antiforgery);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var authority = await provider.AuthenticateAsync(cancellationToken);
            if (!authority.Owner || session!.CloudSubjectKey != authority.SubjectKey)
            {
                return LifecycleProblem("Cloud owner reauthentication is required.", StatusCodes.Status403Forbidden);
            }

            await lifecycle.ReconnectOrganizationAsync(cancellationToken);
            sessions.RevokeAll();
            var reconnectedSession = sessions.CreateCloud(context, authority);
            await AuditAsync(
                db,
                "console.organization.reconnected",
                "active",
                timeProvider.GetUtcNow(),
                cancellationToken);
            ConsoleSessionEndpoints.WriteAntiforgeryHeader(context, antiforgery);
            return TypedResults.Ok(ToDto(lifecycle.Current, reconnectedSession, recovery.Kind));
        }
        catch (InvalidOperationException error)
        {
            return LifecycleProblem(error.Message, StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<Results<Ok<ConsoleLifecycleDto>, ProblemHttpResult>> Reclaim(
        ConsoleReclaimRequestDto request,
        HttpContext context,
        IConsoleLifecycleStore lifecycle,
        IConsoleSessionStore sessions,
        IConsoleCloudLoginProvider provider,
        IConsoleOfflineRecoveryVerifier recovery,
        IOptions<LuthnIdentityOptions> identity,
        IAntiforgery antiforgery,
        LuthnDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ConsoleRequestSecurity.IsSameOriginOrNonBrowser(context.Request))
        {
            return LifecycleProblem("Local reclaim must begin from the same console origin.", StatusCodes.Status403Forbidden);
        }

        if (!Enum.IsDefined(request.Method))
        {
            return LifecycleProblem("The Local reclaim method is invalid.", StatusCodes.Status400BadRequest);
        }

        if (identity.Value.Mode != LuthnIdentityMode.SingleOwner || !lifecycle.IsEnrolled)
        {
            return LifecycleProblem(
                "Local reclaim requires an enrolled SingleOwner installation.",
                StatusCodes.Status409Conflict);
        }

        var session = sessions.Authenticate(context);
        if (request.Method == ConsoleReclaimMethod.CloudOwnerReauthentication)
        {
            var failure = await RequireCloudMutationAsync(context, session, antiforgery);
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                var authority = await provider.AuthenticateAsync(cancellationToken);
                if (!authority.Owner || session!.CloudSubjectKey != authority.SubjectKey)
                {
                    return LifecycleProblem("Cloud owner reauthentication failed.", StatusCodes.Status403Forbidden);
                }
            }
            catch (InvalidOperationException error)
            {
                return LifecycleProblem(error.Message, StatusCodes.Status403Forbidden);
            }
        }
        else if (!await recovery.VerifyAsync(cancellationToken))
        {
            return LifecycleProblem("Offline recovery verification failed or is disabled.", StatusCodes.Status403Forbidden);
        }

        var now = timeProvider.GetUtcNow();
        await lifecycle.RevokeConnectionAuthorityAsync(now, cancellationToken);
        sessions.RevokeAll();
        sessions.Revoke(context);
        await lifecycle.CompleteLocalReclaimAsync(now, cancellationToken);
        await AuditAsync(db, "console.local_reclaim.completed", "reclaimed", now, cancellationToken);
        return TypedResults.Ok(ToDto(lifecycle.Current, null, recovery.Kind));
    }

    private static async ValueTask<ProblemHttpResult?> RequireFakeCloudMutationAsync(
        HttpContext context,
        ConsoleSessionIdentity? session,
        IConsoleCloudLoginProvider provider,
        IAntiforgery antiforgery)
    {
        if (provider.Kind != ConsoleCloudLoginProvider.Fake || !provider.Available)
        {
            return LifecycleProblem(
                "Lifecycle simulation is available only with the fake Cloud provider.",
                StatusCodes.Status404NotFound);
        }

        return await RequireCloudMutationAsync(context, session, antiforgery);
    }

    private static async ValueTask<ProblemHttpResult?> RequireCloudMutationAsync(
        HttpContext context,
        ConsoleSessionIdentity? session,
        IAntiforgery antiforgery)
    {
        if (session is null || session.Mode == ConsoleAccessMode.LocalAuto)
        {
            return LifecycleProblem("An authenticated Cloud console session is required.", StatusCodes.Status401Unauthorized);
        }

        return await ConsoleRequestSecurity.ValidateMutationAsync(context, antiforgery);
    }

    private static ConsoleLifecycleDto ToDto(
        ConsoleLifecycleSnapshot lifecycle,
        ConsoleSessionIdentity? session,
        ConsoleRecoveryVerifier verifier,
        ConsoleMembershipState? membershipOverride = null)
    {
        var actions = lifecycle.OrganizationState switch
        {
            ConsoleOrganizationState.RestrictedOffboarding =>
                new[] { "reconnect", "export-metadata", "detach", "local-reclaim" },
            ConsoleOrganizationState.Detached => new[] { "create-local-session" },
            _ when session is null && lifecycle.IsEnrolled => new[] { "cloud-login", "switch-account", "contact-admin" },
            _ => new[] { "continue" }
        };
        return new ConsoleLifecycleDto(
            lifecycle.OrganizationState,
            membershipOverride ?? session?.Membership,
            lifecycle.IsEnrolled && lifecycle.ConnectionAuthorityRevokedAt is null,
            verifier,
            actions,
            actions[0],
            true);
    }

    private static async ValueTask AuditAsync(
        LuthnDbContext db,
        string action,
        string outcome,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(AuditEventFactory.ForInstallation(
            "console:lifecycle",
            action,
            "console-installation",
            "metadata-only",
            "no-content",
            occurredAt,
            actorKind: "user",
            subjectType: "console_lifecycle",
            outcome: outcome));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ProblemHttpResult LifecycleProblem(string detail, int statusCode) =>
        TypedResults.Problem(
            title: "Console lifecycle transition could not continue.",
            detail: detail,
            statusCode: statusCode);
}
