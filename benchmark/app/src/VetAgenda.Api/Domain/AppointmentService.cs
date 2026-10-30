namespace VetAgenda.Api.Domain;

public sealed record ScheduleAppointment(
    int VeterinarianId,
    string PetName,
    string OwnerName,
    DateTimeOffset Start,
    int DurationMinutes);

public sealed class AppointmentService(IAppointmentRepository appointments, IVeterinarianRepository veterinarians)
{
    public Result<Appointment> Schedule(ScheduleAppointment request)
    {
        if (string.IsNullOrWhiteSpace(request.PetName))
        {
            return Result<Appointment>.Failure(AppointmentErrors.Invalid("Pet name is required."));
        }

        if (string.IsNullOrWhiteSpace(request.OwnerName))
        {
            return Result<Appointment>.Failure(AppointmentErrors.Invalid("Owner name is required."));
        }

        if (request.DurationMinutes is < 15 or > 240)
        {
            return Result<Appointment>.Failure(AppointmentErrors.Invalid("Duration must be between 15 and 240 minutes."));
        }

        if (veterinarians.Find(request.VeterinarianId) is null)
        {
            return Result<Appointment>.Failure(AppointmentErrors.VeterinarianNotFound);
        }

        var start = request.Start;
        var end = start.AddMinutes(request.DurationMinutes);

        var sameDay = appointments.ListByVeterinarian(request.VeterinarianId)
            .Where(a => a.Status == AppointmentStatus.Scheduled && a.Start.Date == start.Date);

        if (sameDay.Any(a => a.Overlaps(start, end)))
        {
            return Result<Appointment>.Failure(AppointmentErrors.Conflict);
        }

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            VeterinarianId = request.VeterinarianId,
            PetName = request.PetName.Trim(),
            OwnerName = request.OwnerName.Trim(),
            Start = start,
            Duration = TimeSpan.FromMinutes(request.DurationMinutes),
        };

        appointments.Add(appointment);
        return Result<Appointment>.Success(appointment);
    }
}
