using VetAgenda.Api.Domain;

namespace VetAgenda.Api.Endpoints;

public sealed record ErrorBody(string Code, string Message);

public static class ErrorResults
{
    public static IResult From(DomainError error)
    {
        var status = error.Code switch
        {
            _ when error.Code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
            _ when error.Code.EndsWith(".conflict", StringComparison.Ordinal) => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ErrorBody(error.Code, error.Message), statusCode: status);
    }
}
