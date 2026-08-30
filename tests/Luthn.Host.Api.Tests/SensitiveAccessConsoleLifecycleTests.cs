namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessConsoleLifecycleTests
{
    [Fact]
    public void ExistingAccessConsoleExposesBoundedPolicyAndGrantLifecycleControls()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Luthn.Host.Api", "wwwroot", "index.html"));
        var script = File.ReadAllText(Path.Combine(root, "src", "Luthn.Host.Api", "wwwroot", "assets", "operator.js"));
        var translations = File.ReadAllText(Path.Combine(root, "src", "Luthn.Host.Api", "wwwroot", "assets", "operator-i18n.js"));

        Assert.Contains("id=\"accessPolicyForm\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "name=\"requestTimeoutMinutes\" type=\"number\" min=\"1\" max=\"60\" step=\"any\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "name=\"grantDurationMinutes\" type=\"number\" min=\"1\" max=\"60\" step=\"any\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("name=\"maximumSuccessfulReads\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"statusCode\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"grantExpiresAt\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"readUsage\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"accessMode\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-protected-control", html, StringComparison.Ordinal);
        Assert.Contains(
            "name=\"protectedGrantDurationMinutes\" type=\"number\" min=\"1\" max=\"60\" value=\"60\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "name=\"protectedMaximumSuccessfulReads\" type=\"number\" min=\"1\" max=\"3\" value=\"1\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Protected content and credentials are never loaded into this console.",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<option value=\"Expired\">Expired</option>", html, StringComparison.Ordinal);
        Assert.Contains("data-tombstone-hidden", html, StringComparison.Ordinal);

        Assert.Contains("/api/access-requests/policy", script, StringComparison.Ordinal);
        Assert.Contains("state.selectedAccessDetail?.statusCode === \"request-pending\"", script, StringComparison.Ordinal);
        Assert.Contains("detail.usedReads", script, StringComparison.Ordinal);
        Assert.Contains("state.selectedAccessDetail?.accessMode === \"ProtectedMemory\"", script, StringComparison.Ordinal);
        Assert.Contains("protectedGrantDurationMinutes", script, StringComparison.Ordinal);
        Assert.Contains("protectedMaximumSuccessfulReads", script, StringComparison.Ordinal);
        Assert.Contains("maximumSuccessfulReads: Number(form.get(\"protectedMaximumSuccessfulReads\"))", script, StringComparison.Ordinal);
        Assert.Contains("Expired · content removed", script, StringComparison.Ordinal);
        Assert.Contains("element.hidden = isTombstone", script, StringComparison.Ordinal);
        Assert.Contains("!(\"sensitiveReferenceId\" in (detail || {}))", script, StringComparison.Ordinal);
        Assert.Contains("requestTimeoutSeconds || 600) / 60", script, StringComparison.Ordinal);
        Assert.Contains("grantDurationSeconds || 600) / 60", script, StringComparison.Ordinal);
        Assert.Contains(
            "requestTimeoutSeconds: Math.round(Number(form.requestTimeoutMinutes.value) * 60)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "grantDurationSeconds: Math.round(Number(form.grantDurationMinutes.value) * 60)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Math.round((policy?.requestTimeoutSeconds", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.round((policy?.grantDurationSeconds", script, StringComparison.Ordinal);
        Assert.Contains("access.policyTitle", translations, StringComparison.Ordinal);
        Assert.Contains("민감 접근 설정", translations, StringComparison.Ordinal);
        Assert.DoesNotContain("rawContent", html + script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessHandle", html + script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Luthn.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
