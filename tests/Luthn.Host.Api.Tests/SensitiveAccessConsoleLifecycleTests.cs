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
        Assert.Contains("name=\"requestTimeoutMinutes\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"grantDurationMinutes\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"maximumSuccessfulReads\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"statusCode\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"grantExpiresAt\"", html, StringComparison.Ordinal);
        Assert.Contains("data-access-field=\"readUsage\"", html, StringComparison.Ordinal);

        Assert.Contains("/api/access-requests/policy", script, StringComparison.Ordinal);
        Assert.Contains("state.selectedAccessDetail?.statusCode === \"request-pending\"", script, StringComparison.Ordinal);
        Assert.Contains("detail.usedReads", script, StringComparison.Ordinal);
        Assert.Contains("access.policyTitle", translations, StringComparison.Ordinal);
        Assert.Contains("민감 접근 설정", translations, StringComparison.Ordinal);
        Assert.DoesNotContain("rawContent", html + script, StringComparison.OrdinalIgnoreCase);
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
