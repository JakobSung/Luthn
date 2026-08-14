using System.Reflection;
using Luthn.Core.Persistence;

namespace Luthn.Host.Api.Tests;

public sealed class SensitiveAccessWorkflowBoundaryTests
{
    [Fact]
    public void MinimalApiHandlersDependOnWorkflowInsteadOfDbContext()
    {
        var endpointHandlers = typeof(SensitiveAccessEndpoints)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name.EndsWith("Endpoint", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(11, endpointHandlers.Length);
        Assert.All(endpointHandlers, handler =>
        {
            Assert.Contains(handler.GetParameters(), parameter =>
                parameter.ParameterType == typeof(ISensitiveAccessWorkflow));
            Assert.DoesNotContain(handler.GetParameters(), parameter =>
                parameter.ParameterType == typeof(LuthnDbContext));
        });
    }

    [Fact]
    public void EndpointModuleContainsNoDirectSensitivePersistenceAccess()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Luthn.Host.Api",
            "SensitiveAccessEndpoints.cs"));

        Assert.DoesNotContain(".SensitiveAccessRequests", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".SensitiveAccessDecisions", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".SensitiveRecordReferences", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AuditEvents", source, StringComparison.Ordinal);
        Assert.Contains("ISensitiveAccessWorkflow workflow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowIsScopedAndReadPermitIsInternalAndNonSerializableByShape()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Luthn.Host.Api",
            "Program.cs"));
        var permitType = typeof(SensitiveAccessReadPermit);

        Assert.Contains(
            "AddScoped<ISensitiveAccessWorkflow, SensitiveAccessWorkflow>()",
            program,
            StringComparison.Ordinal);
        Assert.False(permitType.IsPublic);
        Assert.Empty(permitType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(permitType.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(SensitiveAccessEndpoints).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Permit", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Luthn.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
