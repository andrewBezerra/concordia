using System.Collections.Concurrent;
using VetAgenda.Api.Domain;

namespace VetAgenda.Api.Infrastructure;

public sealed class InMemoryAppointmentRepository : IAppointmentRepository
{
    private readonly ConcurrentDictionary<Guid, Appointment> _appointments = new();

    public void Add(Appointment appointment) => _appointments[appointment.Id] = appointment;

    public Appointment? Find(Guid id) => _appointments.TryGetValue(id, out var appointment) ? appointment : null;

    public IReadOnlyList<Appointment> ListByVeterinarian(int veterinarianId) =>
        _appointments.Values.Where(a => a.VeterinarianId == veterinarianId).ToList();
}

public sealed class InMemoryVeterinarianRepository : IVeterinarianRepository
{
    private static readonly IReadOnlyList<Veterinarian> Seed =
    [
        new Veterinarian(1, "Dr. Ana Souza"),
        new Veterinarian(2, "Dr. Bruno Lima"),
    ];

    public Veterinarian? Find(int id) => Seed.FirstOrDefault(v => v.Id == id);

    public IReadOnlyList<Veterinarian> List() => Seed;
}
