# VetAgenda

Appointment scheduling API for veterinary clinics. ASP.NET Core minimal APIs, in-memory storage.

## Commands
- Build: `dotnet build`
- Test: `dotnet test`

## Layout
- `src/VetAgenda.Api/Domain/` — entities, rules and the `AppointmentService`; returns `Result<T>` instead of throwing for business errors
- `src/VetAgenda.Api/Infrastructure/` — in-memory repositories
- `src/VetAgenda.Api/Endpoints/` — HTTP endpoints, one file per resource
- `tests/VetAgenda.Tests/` — xUnit tests through HTTP with `WebApplicationFactory`; time comes from `IClock` (use `TestApp` and its `FakeClock`)

## Conventions
- Error responses: `{ "code": "...", "message": "..." }`; codes look like `appointment.conflict`
- All times are `DateTimeOffset` in UTC
