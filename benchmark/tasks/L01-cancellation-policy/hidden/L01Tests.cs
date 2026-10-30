using System.Net;
using System.Text.Json;

namespace VetAgenda.Tests.Hidden.L01;

public sealed class L01Tests : IDisposable
{
    private readonly HiddenApp _app = new();

    public void Dispose() => _app.Dispose();

    private Task<HttpResponseMessage> Cancel(Guid id) => _app.Client.PostAsync($"/appointments/{id}/cancel", content: null);

    [Fact]
    public async Task Price_defaults_to_zero_and_is_returned()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 5, 10, 0));

        var body = await HiddenApp.Json(response);
        Assert.Equal(0m, body.GetProperty("price").GetDecimal());
        Assert.Equal(0m, body.GetProperty("cancellationFee").GetDecimal());
    }

    [Fact]
    public async Task Negative_price_returns_400_invalid()
    {
        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 5, 10, 0), price: -1m);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("appointment.invalid", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Early_cancellation_is_free()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 5, 10, 0), price: 200m);

        var response = await Cancel(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HiddenApp.Json(response);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(0m, body.GetProperty("cancellationFee").GetDecimal());
    }

    [Fact]
    public async Task Late_cancellation_charges_half_rounded_away_from_zero()
    {
        // Now is 2 March 12:00; the appointment starts 2 March 20:00 (8 hours later).
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 2, 20, 0), price: 99.99m);

        var body = await HiddenApp.Json(await Cancel(id));

        Assert.Equal(50.00m, body.GetProperty("cancellationFee").GetDecimal());
    }

    [Fact]
    public async Task Exactly_24_hours_before_is_free()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.Now.AddHours(24), price: 100m);

        var body = await HiddenApp.Json(await Cancel(id));

        Assert.Equal(0m, body.GetProperty("cancellationFee").GetDecimal());
    }

    [Fact]
    public async Task Just_under_24_hours_before_is_charged()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.Now.AddHours(24).AddMinutes(-1), price: 100m);

        var body = await HiddenApp.Json(await Cancel(id));

        Assert.Equal(50m, body.GetProperty("cancellationFee").GetDecimal());
    }

    [Fact]
    public async Task Cancelling_twice_returns_409()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 5, 10, 0));
        await Cancel(id);

        var response = await Cancel(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("appointment.already_cancelled", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Cancelling_a_started_appointment_returns_422()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 2, 13, 0));
        _app.Clock.UtcNow = HiddenApp.At(3, 2, 13, 0);

        var response = await Cancel(id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("appointment.already_started", await HiddenApp.ErrorCode(response));
    }

    [Fact]
    public async Task Cancelling_unknown_appointment_returns_404()
    {
        var response = await Cancel(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancelled_slot_can_be_booked_again()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 5, 10, 0), 60);
        await Cancel(id);

        var response = await _app.ScheduleAsync(1, HiddenApp.At(3, 5, 10, 30));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Get_shows_cancelled_status_and_fee()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 2, 20, 0), price: 80m);
        await Cancel(id);

        var body = await HiddenApp.Json(await _app.Client.GetAsync($"/appointments/{id}"));

        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(40m, body.GetProperty("cancellationFee").GetDecimal());
        Assert.Equal(80m, body.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Cancelled_at_is_null_until_cancelled_and_comes_from_the_clock()
    {
        var created = await HiddenApp.Json(await _app.ScheduleAsync(1, HiddenApp.At(3, 5, 10, 0)));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("cancelledAt").ValueKind);

        _app.Clock.UtcNow = HiddenApp.At(3, 3, 9, 0);
        var body = await HiddenApp.Json(await Cancel(created.GetProperty("id").GetGuid()));

        Assert.Equal(HiddenApp.At(3, 3, 9, 0), body.GetProperty("cancelledAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Price_survives_the_round_trip_through_storage()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 5, 10, 0), price: 99.99m);

        var body = await HiddenApp.Json(await _app.Client.GetAsync($"/appointments/{id}"));

        Assert.Equal(99.99m, body.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Two_application_hosts_do_not_share_data()
    {
        var id = await _app.ScheduleOkAsync(1, HiddenApp.At(3, 5, 10, 0));
        using var other = new HiddenApp();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/appointments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await other.ScheduleAsync(1, HiddenApp.At(3, 5, 10, 0))).StatusCode);
    }
}
