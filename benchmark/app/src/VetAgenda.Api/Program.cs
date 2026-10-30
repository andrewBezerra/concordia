using System.Text.Json.Serialization;
using VetAgenda.Api.Domain;
using VetAgenda.Api.Endpoints;
using VetAgenda.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IVeterinarianRepository, InMemoryVeterinarianRepository>();
builder.Services.AddSingleton<IAppointmentRepository, InMemoryAppointmentRepository>();
builder.Services.AddScoped<AppointmentService>();

var app = builder.Build();

app.MapVeterinarianEndpoints();
app.MapAppointmentEndpoints();

app.Run();

public partial class Program
{
}
