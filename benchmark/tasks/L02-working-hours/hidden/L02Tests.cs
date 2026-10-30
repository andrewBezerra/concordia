using System.Net;
using System.Net.Http.Json;

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

    private Task<HttpResponseMessage> PutHours(int veterinarianId, string? key, params object[] workingHours)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/veterinarians/{veterinarianId}/working-hours")
        {
            Content = JsonContent.Create(new { workingHours }),
        };
        if (key is not null) request.Headers.Add("X-Admin-Key", key);
        return _app.Client.SendAsync(request);
    }

    private static object Hours(string day, string open, string close) => new { day, open, close };

    [Fact]
    public async Task Veterinarian_lists_working_hours_from_monday_to_sunday()
    {
        var body = await HiddenApp.Json(await _app.Client.GetAsync("/veterinarians/2"));

        var hours = body.GetProperty("workingHours").EnumerateArray().ToList();
        Assert.Equal(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"], hours.Select(h => h.GetProperty("day").GetString()));
        Assert.Equal("09:00", hours[0].GetProperty("open").GetString());
        Assert.Equal("13:00", hours[0].GetProperty("close").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Changing_hours_without_the_admin_key_returns_401_and_changes_nothing(string? key)
    {
        var response = await PutHours(1, key, Hours("Monday", "10:00", "11:00"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_key", await HiddenApp.ErrorCode(response));
        Assert.Equal(HttpStatusCode.Created, (await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 8, 0))).StatusCode);
    }

    [Fact]
    public async Task Key_is_checked_before_the_veterinarian_lookup()
    {
        var response = await PutHours(99, null, Hours("Monday", "10:00", "11:00"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_hours_of_unknown_veterinarian_returns_404()
    {
        var response = await PutHours(99, "bench-admin-key", Hours("Monday", "10:00", "11:00"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Changed_hours_replace_the_week_for_scheduling_and_availability()
    {
        var response = await PutHours(1, "bench-admin-key", Hours("Sunday", "14:00", "15:00"), Hours("Tuesday", "14:00", "15:00"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HiddenApp.Json(response);
        Assert.Equal(["Tuesday", "Sunday"], body.GetProperty("workingHours").EnumerateArray().Select(h => h.GetProperty("day").GetString()));
        Assert.Equal(["14:00", "14:30"], await Slots(1, "2026-03-03"));
        Assert.Empty(await Slots(1, "2026-03-04"));
        Assert.Equal(HttpStatusCode.Created, (await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 14, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 10, 0))).StatusCode);
    }

    [Fact]
    public async Task Empty_week_makes_every_day_a_day_off()
    {
        var response = await PutHours(1, "bench-admin-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Slots(1, "2026-03-03"));
    }

    [Fact]
    public async Task Changing_hours_keeps_existing_appointments()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 10, 0));

        await PutHours(1, "bench-admin-key", Hours("Monday", "14:00", "15:00"));

        var body = await HiddenApp.Json(await _app.Client.GetAsync($"/appointments/{id}"));
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_hours_return_400()
    {
        (string Day, string Open, string Close)[] invalid =
        [
            ("Funday", "10:00", "11:00"),
            ("Monday", "25:00", "26:00"),
            ("Monday", "11:00", "11:00"),
            ("Monday", "12:00", "11:00"),
        ];

        foreach (var (day, open, close) in invalid)
        {
            var response = await PutHours(1, "bench-admin-key", Hours(day, open, close));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("working_hours.invalid", await HiddenApp.ErrorCode(response));
        }
    }

    [Fact]
    public async Task Repeated_day_returns_400()
    {
        var response = await PutHours(1, "bench-admin-key", Hours("Monday", "08:00", "09:00"), Hours("Monday", "10:00", "11:00"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("working_hours.invalid", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Changed_hours_stay_on_their_own_application_host()
    {
        await PutHours(1, "bench-admin-key");
        using var other = new HiddenApp();

        Assert.Equal(HttpStatusCode.Created, (await other.ScheduleAsync(1, HiddenApp.At(3, 3, 10, 0))).StatusCode);
    }
}
