using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Luthn.Host.Api;

public static partial class HostManagedExtensionEndpoints
{
    private static readonly TimeSpan ActionLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapHostManagedExtension(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/operator/managed-extensions/actions",
                CreateAction)
            .RequireServiceScope(ServiceScopes.ConfigWrite)
            .WithName("CreateHostManagedExtensionAction");

        app.MapGet(
                "/api/operator/managed-extensions/actions/{actionId}",
                ReadAction)
            .RequireServiceScope(ServiceScopes.ConfigWrite)
            .WithName("ReadHostManagedExtensionAction");

        app.MapPost(
                "/api/operator/managed-extensions/actions/{actionId}/finalize",
                FinalizeAction)
            .RequireServiceScope(ServiceScopes.ConfigWrite)
            .WithName("FinalizeHostManagedExtensionAction");

        app.MapPost(
                "/api/host/managed-extensions/actions/claim",
                ClaimAction)
            .RequireServiceScope(ServiceScopes.AgentConnectionWrite)
            .WithName("ClaimHostManagedExtensionAction");

        app.MapPost(
                "/api/host/managed-extensions/actions/{actionId}/complete",
                CompleteAction)
            .RequireServiceScope(ServiceScopes.AgentConnectionWrite)
            .WithName("CompleteHostManagedExtensionAction");

        return app;
    }

    private static Results<Ok<HostManagedExtensionAction>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> CreateAction(
        CreateHostManagedExtensionActionRequest request,
        HostManagedExtensionStore store,
        HostManagedExtensionVerifier verifier,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var problem = ValidateRequest(request, verifier, now);
        if (problem is not null)
        {
            return TypedResults.BadRequest(problem);
        }

        var created = store.Create(request, now, ActionLifetime);
        return created is null
            ? TypedResults.Conflict(Problem(
                "An extension connection is already pending.",
                "Wait for the current locally approved connection to finish or expire."))
            : TypedResults.Ok(created.WithoutSecret());
    }

    private static Results<Ok<HostManagedExtensionAction>, NotFound> ReadAction(
        string actionId,
        HostManagedExtensionStore store,
        TimeProvider timeProvider)
    {
        if (!IsOpaqueId(actionId))
        {
            return TypedResults.NotFound();
        }

        var action = store.Read(actionId, timeProvider.GetUtcNow());
        return action is null ? TypedResults.NotFound() : TypedResults.Ok(action);
    }

    private static Ok<HostManagedExtensionActionClaim> ClaimAction(
        HostManagedExtensionStore store,
        TimeProvider timeProvider) =>
        TypedResults.Ok(new HostManagedExtensionActionClaim(store.Claim(timeProvider.GetUtcNow())));

    private static Results<Ok<HostManagedExtensionAction>, BadRequest<ProblemDetails>, NotFound> FinalizeAction(
        string actionId,
        FinalizeHostManagedExtensionActionRequest request,
        HostManagedExtensionStore store,
        TimeProvider timeProvider)
    {
        if (!IsOpaqueId(actionId) || request.Outcome is not ("activated" or "failed"))
        {
            return TypedResults.BadRequest(Problem(
                "Invalid extension activation result.",
                "The authenticated service must explicitly confirm activation or failure."));
        }

        var finalized = store.Finalize(actionId, request.Outcome, timeProvider.GetUtcNow());
        return finalized is null ? TypedResults.NotFound() : TypedResults.Ok(finalized);
    }

    private static Results<Ok<HostManagedExtensionAction>, BadRequest<ProblemDetails>, NotFound> CompleteAction(
        string actionId,
        CompleteHostManagedExtensionActionRequest request,
        HostManagedExtensionStore store,
        TimeProvider timeProvider)
    {
        if (!IsOpaqueId(actionId) ||
            request.Outcome is not ("succeeded" or "failed") ||
            request.FailureCode is not null && !IsToken(request.FailureCode, 64) ||
            request.VerificationCode is not null && !IsVerificationCode(request.VerificationCode))
        {
            return TypedResults.BadRequest(Problem(
                "Invalid extension connection result.",
                "The result must contain a bounded outcome and a verification code only after success."));
        }

        var completed = store.Complete(
            actionId,
            request.Outcome,
            request.FailureCode,
            request.VerificationCode,
            timeProvider.GetUtcNow());
        if (completed.Invalid)
        {
            return TypedResults.BadRequest(Problem(
                "Invalid extension connection result.",
                "The completion payload does not match the claimed extension operation."));
        }
        return completed.Action is null ? TypedResults.NotFound() : TypedResults.Ok(completed.Action);
    }

    private static ProblemDetails? ValidateRequest(
        CreateHostManagedExtensionActionRequest request,
        HostManagedExtensionVerifier verifier,
        DateTimeOffset now)
    {
        if (request.AgentKind is not ("codex" or "claude") ||
            string.IsNullOrWhiteSpace(request.ProvisioningToken) ||
            request.ProvisioningToken.Length is < 32 or > 512 ||
            !ProvisioningTokenPattern().IsMatch(request.ProvisioningToken) ||
            request.Manifest is null ||
            !verifier.Verify(request.Manifest, request.Signature, now))
        {
            return Problem(
                "Invalid extension connection offer.",
                "The offer must be current, signed by a trusted publisher, and contain bounded provisioning values.");
        }

        return null;
    }

    private static bool IsVerificationCode(string value) =>
        value.Length is >= 8 and <= 32 && VerificationCodePattern().IsMatch(value);

    private static bool IsOpaqueId(string value) =>
        value.Length is >= 16 and <= 128 && TokenPattern().IsMatch(value);

    private static bool IsToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && TokenPattern().IsMatch(value);

    private static ProblemDetails Problem(string title, string detail) => new()
    {
        Title = title,
        Detail = detail,
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:@-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[A-Z0-9]+(?:-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VerificationCodePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProvisioningTokenPattern();
}

public sealed class HostManagedExtensionOptions
{
    public const string SectionName = "Luthn:ManagedExtensions";
    public const string DefaultTrustedSigningPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6VdIO2gbjn02fB5FSw3XljoicJe6
        lTHnsdu/xHb0FVOp8UVZnJMOA0XDQA2x6+IPnIFklXYGOgRF9xj/Mu5jRw==
        -----END PUBLIC KEY-----
        """;

    public string TrustedSigningPublicKeyPem { get; init; } = DefaultTrustedSigningPublicKeyPem;
}

public sealed class HostManagedExtensionVerifier(IOptions<HostManagedExtensionOptions> options)
{
    public bool Verify(HostManagedExtensionManifest manifest, string? signature, DateTimeOffset now)
    {
        if (!manifest.IsValid(now) || string.IsNullOrWhiteSpace(signature) || signature.Length > 256)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(options.Value.TrustedSigningPublicKeyPem);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(manifest.CanonicalPayload()),
                DecodeBase64Url(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
            .PadRight((value.Length + 3) / 4 * 4, '='));
}

public sealed record HostManagedExtensionManifest(
    int SchemaVersion,
    string ExtensionId,
    string Publisher,
    string DisplayName,
    string PackageUri,
    string PackageSha256,
    string PackageVersion,
    string RuntimeBaseImage,
    string ServiceOrigin,
    DateTimeOffset ExpiresAt)
{
    public bool IsValid(DateTimeOffset now)
    {
        if (SchemaVersion != 1 || !IsIdentifier(ExtensionId) || !IsIdentifier(Publisher) ||
            string.IsNullOrWhiteSpace(DisplayName) ||
            DisplayName.Length > 128 || DisplayName.Any(char.IsControl) ||
            !PackageSha256.StartsWith("sha256:", StringComparison.Ordinal) ||
            PackageSha256.Length != 71 ||
            !PackageSha256.AsSpan(7).ToString().All(char.IsAsciiHexDigit) ||
            !IsVersionToken(PackageVersion) ||
            !IsPinnedRuntimeBaseImage(RuntimeBaseImage) ||
            ExpiresAt <= now || ExpiresAt > now.AddMinutes(10))
        {
            return false;
        }

        return TryHttpsUri(PackageUri, requiredPathPrefix: "/") is not null &&
            TryHttpsUri(ServiceOrigin, requiredPathPrefix: "/") is { AbsolutePath: "/" };
    }

    public string CanonicalPayload() => string.Join('\n',
        SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ExtensionId,
        Publisher,
        DisplayName,
        PackageUri,
        PackageSha256.ToLowerInvariant(),
        PackageVersion,
        RuntimeBaseImage,
        ServiceOrigin,
        ExpiresAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static Uri? TryHttpsUri(string value, string requiredPathPrefix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !uri.AbsolutePath.StartsWith(requiredPathPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        return uri;
    }

    private static bool IsPinnedRuntimeBaseImage(string value)
    {
        const string prefix = "mcr.microsoft.com/dotnet/aspnet:10.0@sha256:";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.Length == prefix.Length + 64 &&
            value.AsSpan(prefix.Length).ToString().All(char.IsAsciiHexDigit);
    }

    private static bool IsVersionToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public sealed class HostManagedExtensionStore
{
    private readonly Lock _gate = new();
    private HostManagedExtensionAction? _action;

    public HostManagedExtensionAction? Create(
        CreateHostManagedExtensionActionRequest request,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        lock (_gate)
        {
            Expire(now);
            if (_action?.State is "pending" or "claimed" or "prepared" or "cleanup-pending" or "cleanup-claimed") return null;
            _action = new HostManagedExtensionAction(
                $"extension-action-{Guid.NewGuid():N}",
                request.AgentKind,
                request.Manifest!,
                request.ProvisioningToken,
                "install",
                "pending",
                null,
                null,
                now,
                new[] { now.Add(lifetime), request.Manifest!.ExpiresAt }.Min(),
                null);
            return _action;
        }
    }

    public HostManagedExtensionAction? Read(string actionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);
            return _action?.Id == actionId ? _action.WithoutSecret() : null;
        }
    }

    public HostManagedExtensionAction? Claim(DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);
            if (_action?.State is not ("pending" or "cleanup-pending")) return null;
            _action = _action with
            {
                State = _action.State == "pending" ? "claimed" : "cleanup-claimed",
                UpdatedAt = now,
            };
            return _action;
        }
    }

    public HostManagedExtensionMutationResult Complete(
        string actionId,
        string outcome,
        string? failureCode,
        string? verificationCode,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);
            if (_action is null || _action.Id != actionId || _action.State is not ("claimed" or "cleanup-claimed"))
            {
                return new(null, false);
            }
            if ((_action.State == "claimed" &&
                 (outcome == "succeeded" && verificationCode is null ||
                  outcome == "failed" && verificationCode is not null)) ||
                (_action.State == "cleanup-claimed" && verificationCode is not null))
            {
                return new(null, true);
            }

            if (_action.State == "cleanup-claimed")
            {
                _action = _action with
                {
                    State = "failed",
                    FailureCode = outcome == "succeeded" ? "extension.activation_failed" : "extension.cleanup_failed",
                    VerificationCode = null,
                    ProvisioningToken = string.Empty,
                    UpdatedAt = now,
                };
                return new(_action.WithoutSecret(), false);
            }

            _action = _action with
            {
                State = outcome == "succeeded" ? "prepared" : "failed",
                FailureCode = outcome == "failed" ? failureCode ?? "extension.bootstrap_failed" : null,
                VerificationCode = outcome == "succeeded" ? verificationCode : null,
                ProvisioningToken = string.Empty,
                UpdatedAt = now,
            };
            return new(_action.WithoutSecret(), false);
        }
    }

    public HostManagedExtensionAction? Finalize(string actionId, string outcome, DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);
            if (_action is null || _action.Id != actionId) return null;
            if ((outcome == "activated" && _action.State == "succeeded") ||
                (outcome == "failed" && _action.State is "cleanup-pending" or "cleanup-claimed"))
            {
                return _action.WithoutSecret();
            }
            if (_action.State != "prepared") return null;
            _action = outcome == "activated"
                ? _action with
                {
                    State = "succeeded",
                    FailureCode = null,
                    VerificationCode = null,
                    UpdatedAt = now,
                }
                : _action with
                {
                    Operation = "remove",
                    State = "cleanup-pending",
                    FailureCode = "extension.activation_failed",
                    VerificationCode = null,
                    UpdatedAt = now,
                };
            return _action.WithoutSecret();
        }
    }

    private void Expire(DateTimeOffset now)
    {
        if (_action is not null && _action.ExpiresAt <= now)
        {
            if (_action.State == "pending")
            {
                _action = _action with
                {
                    State = "expired",
                    FailureCode = "extension.bootstrap_expired",
                    ProvisioningToken = string.Empty,
                    UpdatedAt = now,
                };
            }
            else if (_action.State is "claimed" or "prepared")
            {
                _action = _action with
                {
                    Operation = "remove",
                    State = "cleanup-pending",
                    FailureCode = "extension.bootstrap_expired",
                    VerificationCode = null,
                    ProvisioningToken = string.Empty,
                    UpdatedAt = now,
                };
            }
        }
    }
}

public sealed record CreateHostManagedExtensionActionRequest(
    string AgentKind,
    HostManagedExtensionManifest? Manifest,
    string Signature,
    string ProvisioningToken);

public sealed record CompleteHostManagedExtensionActionRequest(
    string Outcome,
    string? FailureCode = null,
    string? VerificationCode = null);

public sealed record FinalizeHostManagedExtensionActionRequest(string Outcome);

public sealed record HostManagedExtensionMutationResult(
    HostManagedExtensionAction? Action,
    bool Invalid);

public sealed record HostManagedExtensionActionClaim(HostManagedExtensionAction? Action);

public sealed record HostManagedExtensionAction(
    string Id,
    string AgentKind,
    HostManagedExtensionManifest Manifest,
    string ProvisioningToken,
    string Operation,
    string State,
    string? FailureCode,
    string? VerificationCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UpdatedAt)
{
    public HostManagedExtensionAction WithoutSecret() => this with { ProvisioningToken = string.Empty };
}
