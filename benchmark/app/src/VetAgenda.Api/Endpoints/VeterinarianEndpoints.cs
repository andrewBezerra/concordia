using VetAgenda.Api.Domain;

namespace VetAgenda.Api.Endpoints;

public static class VeterinarianEndpoints
{
    public static IEndpointRouteBuilder MapVeterinarianEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/veterinarians", (IVeterinarianRepository repository) => Results.Ok(repository.List()));

        app.MapGet("/veterinarians/{id:int}", (int id, IVeterinarianRepository repository) =>
            repository.Find(id) is { } veterinarian ? Results.Ok(veterinarian) : Results.NotFound());

        return app;
    }
}
