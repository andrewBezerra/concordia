using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VetAgenda.Api.Domain;

namespace VetAgenda.Tests.Support;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Starts the API in memory with a controllable clock. Each instance has its own data.</summary>
public sealed class TestApp : IDisposable
{
    /// <summary>Monday, 2 March 2026, 12:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;

    public TestApp()
    {
        Clock = new FakeClock(Now);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
            }));
        Client = _factory.CreateClient();
    }

    public FakeClock Clock { get; }

    public HttpClient Client { get; }

    public Task<HttpResponseMessage> ScheduleAsync(int veterinarianId, DateTimeOffset start, int durationMinutes = 30, string petName = "Rex") =>
        Client.PostAsJsonAsync("/appointments", new
        {
            veterinarianId,
            petName,
            ownerName = "Carla",
            start,
            durationMinutes,
        });

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }
}
