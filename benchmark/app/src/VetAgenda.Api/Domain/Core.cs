namespace VetAgenda.Api.Domain;

public sealed record Veterinarian(int Id, string Name);

public sealed record DomainError(string Code, string Message);

public sealed class Result<T>
{
    private Result(T? value, DomainError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(DomainError error) => new(default, error);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IAppointmentRepository
{
    void Add(Appointment appointment);

    Appointment? Find(Guid id);

    IReadOnlyList<Appointment> ListByVeterinarian(int veterinarianId);
}

public interface IVeterinarianRepository
{
    Veterinarian? Find(int id);

    IReadOnlyList<Veterinarian> List();
}

public static class AppointmentErrors
{
    public static readonly DomainError Conflict =
        new("appointment.conflict", "The veterinarian already has an appointment in this time slot.");

    public static readonly DomainError VeterinarianNotFound =
        new("veterinarian.not_found", "Veterinarian not found.");

    public static DomainError Invalid(string message) => new("appointment.invalid", message);
}
