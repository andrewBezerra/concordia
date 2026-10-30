using System.Net;

namespace VetAgenda.Tests.Hidden.S01;

public sealed class S01Tests : IDisposable
{
    private readonly HiddenApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Start_in_the_past_returns_400_in_past()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.Now.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("appointment.in_past", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Start_yesterday_returns_400_in_past()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.Now.AddDays(-1));

        Assert.Equal("appointment.in_past", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Start_exactly_now_is_allowed()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.Now);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Start_in_the_future_is_allowed()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.Now.AddHours(1));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Uses_the_clock_abstraction()
    {
        _app.Clock.UtcNow = HiddenApp.Now.AddYears(5);

        var response = await _app.ScheduleAsync(1, HiddenApp.Now.AddYears(1));

        Assert.Equal("appointment.in_past", await HiddenApp.ErrorCode(response));
    }
}
