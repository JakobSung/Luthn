using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Luthn.Host.Api;

public static class OperationalMetricsEndpoints
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOperationalMetrics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operator/metrics").RequireServiceScope(ServiceScopes.MetricsRead);
        group.MapGet("", Read).WithName("ReadOperationalMetrics");
        group.MapGet("/export", Export).WithName("ExportOperationalMetrics");
        return app;
    }

    public static Ok<OperationalMetricsSnapshot> Read(IOperationalMetrics metrics) => TypedResults.Ok(metrics.Snapshot());

    public static IResult Export(IOperationalMetrics metrics) => Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metrics.Snapshot(), ExportJsonOptions)), "application/json", "luthn-operational-metrics.json");
}
