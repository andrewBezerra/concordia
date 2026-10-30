using System.Net;

namespace VetAgenda.Tests.Hidden.L02;

public sealed class L02Tests : IDisposable
{
    // 3 March 2026 is a Tuesday; 8 March 2026 is a Sunday; 7 March 2026 is a Saturday.
    private readonly HiddenApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<List<string>> Slots(int veterinarianId, string date)
    {
        var response = await _app.Client.GetAsync($"/veterinarians/{veterinarianId}/availability?date={date}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HiddenApp.Json(response);
        Assert.Equal(date, body.GetProperty("date").GetString());
        return body.GetProperty("slots").EnumerateArray().Select(s => s.GetString()!).ToList();
    }

    [Fact]
    public async Task Starting_before_opening_is_rejected()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 7, 30));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("appointment.outside_working_hours", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Ending_after_closing_is_rejected()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 16, 45));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Day_off_is_rejected()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 8, 10, 0));

        Assert.Equal("appointment.outside_working_hours", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Ending_exactly_at_closing_is_allowed()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 16, 30));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Second_veterinarian_works_on_saturday_morning_only()
    {
        Assert.Equal(HttpStatusCode.Created, (await _app.ScheduleAsync(2, HiddenApp.At(3, 7, 9, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _app.ScheduleAsync(2, HiddenApp.At(3, 7, 13, 0))).StatusCode);
    }

    [Fact]
    public async Task Unknown_veterinarian_is_still_404()
    {
        var response = await _app.ScheduleAsync(99, HiddenApp.At(3, 3, 7, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Availability_lists_free_half_hour_slots()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 10, 0), 60);

        var slots = await Slots(1, "2026-03-03");

        Assert.Equal(16, slots.Count);
        Assert.Equal("08:00", slots[0]);
        Assert.Equal("16:30", slots[^1]);
        Assert.Contains("09:30", slots);
        Assert.DoesNotContain("10:00", slots);
        Assert.DoesNotContain("10:30", slots);
        Assert.Contains("11:00", slots);
    }

    [Fact]
    public async Task Partially_overlapping_appointment_blocks_both_slots()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 10, 15), 30);

        var slots = await Slots(1, "2026-03-03");

        Assert.DoesNotContain("10:00", slots);
        Assert.DoesNotContain("10:30", slots);
        Assert.Contains("11:00", slots);
    }

    [Fact]
    public async Task Day_off_has_no_slots()
    {
        Assert.Empty(await Slots(2, "2026-03-08"));
    }

    [Fact]
    public async Task Availability_for_unknown_veterinarian_returns_404()
    {
        var response = await _app.Client.GetAsync("/veterinarians/99/availability?date=2026-03-03");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
