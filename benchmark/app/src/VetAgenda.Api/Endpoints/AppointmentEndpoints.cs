using VetAgenda.Api.Domain;

namespace VetAgenda.Api.Endpoints;

public sealed record CreateAppointmentRequest(
    int VeterinarianId,
    string PetName,
    string OwnerName,
    DateTimeOffset Start,
    int DurationMinutes);

public sealed record AppointmentResponse(
    Guid Id,
    int VeterinarianId,
    string PetName,
    string OwnerName,
    DateTimeOffset Start,
    DateTimeOffset End,
    AppointmentStatus Status)
{
    public static AppointmentResponse From(Appointment a) =>
        new(a.Id, a.VeterinarianId, a.PetName, a.OwnerName, a.Start, a.End, a.Status);
}

public static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/appointments");

        group.MapPost("/", (CreateAppointmentRequest request, AppointmentService service) =>
        {
            var result = service.Schedule(new ScheduleAppointment(
                request.VeterinarianId,
                request.PetName,
                request.OwnerName,
                request.Start,
                request.DurationMinutes));

            return result.IsSuccess
                ? Results.Created($"/appointments/{result.Value!.Id}", AppointmentResponse.From(result.Value))
                : ErrorResults.From(result.Error!);
        });

        group.MapGet("/{id:guid}", (Guid id, IAppointmentRepository repository) =>
            repository.Find(id) is { } appointment
                ? Results.Ok(AppointmentResponse.From(appointment))
                : Results.NotFound());

        return app;
    }
}
