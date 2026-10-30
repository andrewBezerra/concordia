using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VetAgenda.Tests.Support;

namespace VetAgenda.Tests;

public sealed class AppointmentApiTests : IDisposable
{
    /// <summary>Tuesday, 3 March 2026, 10:00 UTC: the day after <see cref="TestApp.Now"/>.</summary>
    private static readonly DateTimeOffset Tomorrow10 = new(2026, 3, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Schedule_valid_request_returns_201_with_location()
    {
        var response = await _app.ScheduleAsync(1, Tomorrow10);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal(Tomorrow10.AddMinutes(30), body.GetProperty("end").GetDateTimeOffset());
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Schedule_overlapping_slot_returns_409_conflict()
    {
        await _app.ScheduleAsync(1, Tomorrow10, 60);

        var response = await _app.ScheduleAsync(1, Tomorrow10.AddMinutes(30));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("appointment.conflict", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Schedule_adjacent_slot_is_allowed()
    {
        await _app.ScheduleAsync(1, Tomorrow10);

        var response = await _app.ScheduleAsync(1, Tomorrow10.AddMinutes(30));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Same_slot_with_another_veterinarian_is_allowed()
    {
        await _app.ScheduleAsync(1, Tomorrow10);

        var response = await _app.ScheduleAsync(2, Tomorrow10);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_with_unknown_veterinarian_returns_404()
    {
        var response = await _app.ScheduleAsync(99, Tomorrow10);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    public async Task Schedule_with_invalid_duration_returns_400(int minutes)
    {
        var response = await _app.ScheduleAsync(1, Tomorrow10, minutes);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("appointment.invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_returns_the_scheduled_appointment()
    {
        var created = await _app.ScheduleAsync(1, Tomorrow10, petName: "Mia");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await _app.Client.GetAsync($"/appointments/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Mia", body.GetProperty("petName").GetString());
    }

    [Fact]
    public async Task Get_unknown_appointment_returns_404()
    {
        var response = await _app.Client.GetAsync($"/appointments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
