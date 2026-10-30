using System.Net;

namespace VetAgenda.Tests.Hidden.S02;

public sealed class S02Tests : IDisposable
{
    private readonly HiddenApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Overlap_after_midnight_is_a_conflict()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 23, 30), 60);

        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 4, 0, 0), 30);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("appointment.conflict", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Overlap_before_midnight_is_a_conflict()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 4, 0, 0), 30);

        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 3, 23, 45), 30);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Slot_right_after_a_midnight_appointment_is_allowed()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 3, 23, 30), 60);

        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 4, 0, 30), 30);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Overlap_with_a_non_utc_offset_is_a_conflict()
    {
        await _app.ScheduleOkAsync(1, HiddenApp.At(3, 4, 1, 0), 60);

        // 22:30 on 3 March at UTC-03:00 is 01:30 UTC on 4 March.
        var response = await _app.ScheduleAsync(1, new DateTimeOffset(2026, 3, 3, 22, 30, 0, TimeSpan.FromHours(-3)), 30);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
