using System.Net;
using System.Text.Json;

namespace VetAgenda.Tests.Hidden.S03;

public sealed class S03Tests : IDisposable
{
    private readonly HiddenApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Lists_only_that_day_and_veterinarian_ordered_by_start()
    {
        var late = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 15, 0));
        var early = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 9, 0));
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 4, 9, 0));
        await _app.ScheduleOkAsync(2, HiddenApp.At(3, 3, 10, 0));

        var response = await _app.Client.GetAsync("/veterinarians/1/appointments?date=2026-03-03");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await HiddenApp.Json(response)).EnumerateArray().ToList();
        Assert.Equal(new[] { early, late }, items.Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.All(items, i => Assert.Equal(1, i.GetProperty("veterinarianId").GetInt32()));
    }

    [Fact]
    public async Task Items_have_the_appointment_shape()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 9, 0));

        var response = await _app.Client.GetAsync("/veterinarians/1/appointments?date=2026-03-03");

        var item = Assert.Single((await HiddenApp.Json(response)).EnumerateArray());
        Assert.Equal("Rex", item.GetProperty("petName").GetString());
        Assert.Equal("Scheduled", item.GetProperty("status").GetString());
        Assert.Equal(HiddenApp.At(3, 3, 9, 30), item.GetProperty("end").GetDateTimeOffset());
    }

    [Fact]
    public async Task Day_without_appointments_returns_empty_array()
    {
        var response = await _app.Client.GetAsync("/veterinarians/1/appointments?date=2026-03-10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HiddenApp.Json(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task Unknown_veterinarian_returns_404()
    {
        var response = await _app.Client.GetAsync("/veterinarians/99/appointments?date=2026-03-03");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
