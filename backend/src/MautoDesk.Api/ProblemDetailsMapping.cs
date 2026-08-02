using System.Diagnostics;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace MautoDesk.Api;

/// <summary>
/// Turns a domain <see cref="Error"/> into an RFC 9457 problem response.
/// </summary>
/// <remarks>
/// One place, so every endpoint answers the same shape and no handler invents
/// its own error format. See docs/04-api-contracts.md §4.
/// </remarks>
public static class ProblemDetailsMapping
{
    private const string ProblemBaseUri = "https://api.mautodesk.com/problems/";

    public static IResult ToProblem(this Error error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);

        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status422UnprocessableEntity,

            // Deliberately 404 rather than 403 for a cross-tenant identifier.
            // Answering 403 would confirm that the record exists, turning any id
            // into an existence oracle for other dealers' data (ADR-0002).
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problem = new ProblemDetails
        {
            Type = ProblemBaseUri + error.Code.Replace('.', '-'),
            Title = TitleFor(error.Kind),
            Status = status,
            Detail = error.Message,
            Instance = context.Request.Path,
        };

        // The trace id is the only internal identifier that ever crosses the
        // boundary. It lets support correlate a user's report with logs without
        // exposing a stack trace, a SQL statement, or a table name.
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        problem.Extensions["code"] = error.Code;

        if (error.Field is not null)
        {
            problem.Extensions["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [error.Field] = [error.Message],
            };
        }

        return TypedResults.Problem(problem);
    }

    /// <summary>Unwraps a result into either a 200 payload or a problem.</summary>
    public static IResult ToHttp<T>(this Result<T> result, HttpContext context) =>
        result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : result.Error!.ToProblem(context);

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "One or more validation errors occurred.",
        ErrorKind.NotFound => "The requested resource was not found.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        ErrorKind.Forbidden => "You do not have permission to perform this action.",
        ErrorKind.Unavailable => "A required service is temporarily unavailable.",
        _ => "An unexpected error occurred.",
    };
}
