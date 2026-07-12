using Microsoft.AspNetCore.Http.HttpResults;

namespace Luthn.Host.Api;

internal static class ApiProblems
{
    public static ProblemHttpResult ClassificationProviderUnavailable(Exception error) =>
        TypedResults.Problem(
            title: "Classification provider unavailable.",
            detail: error.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
