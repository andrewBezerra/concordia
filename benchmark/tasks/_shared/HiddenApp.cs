using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VetAgenda.Api.Domain;

namespace VetAgenda.Tests.Hidden;

/// <summary>
/// Benchmark-only harness, copied in after the agent finishes. It depends only on public HTTP
/// behavior plus <see cref="IClock"/>, which exists in the base app, so it works with any implementation.
/// </summary>
public sealed class HiddenApp : IDisposable
{
    /// <summary>Monday, 2 March 2026, 12:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;

    public HiddenApp()
    {
        Clock = new HiddenClock(Now);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
            }));
        Client = _factory.CreateClient();
    }

    public HiddenClock Clock { get; }

    public HttpClient Client { get; }

    public static DateTimeOffset At(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    public Task<HttpResponseMessage> ScheduleAsync(int veterinarianId, DateTimeOffset start, int durationMinutes = 30, decimal? price = null)
    {
        object body = price is null
            ? new { veterinarianId, petName = "Rex", ownerName = "Carla", start, durationMinutes }
            : new { veterinarianId, petName = "Rex", ownerName = "Carla", start, durationMinutes, price };
        return Client.PostAsJsonAsync("/appointments", body);
    }

    public async Task<Guid> ScheduleOkAsync(int veterinarianId, DateTimeOffset start, int durationMinutes = 30, decimal? price = null)
    {
        var response = await ScheduleAsync(veterinarianId, start, durationMinutes, price);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }
}

public sealed class HiddenClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
