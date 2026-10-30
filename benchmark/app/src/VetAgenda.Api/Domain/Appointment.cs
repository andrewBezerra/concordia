namespace VetAgenda.Api.Domain;

public enum AppointmentStatus
{
    Scheduled,
    Cancelled,
}

public sealed class Appointment
{
    public required Guid Id { get; init; }

    public required int VeterinarianId { get; init; }

    public required string PetName { get; init; }

    public required string OwnerName { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required TimeSpan Duration { get; init; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    public DateTimeOffset End => Start + Duration;

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && start < End;
}
