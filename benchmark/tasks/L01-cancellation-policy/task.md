Move appointment storage to a relational database and, on top of it, implement appointment
cancellation with a late-cancellation fee. This changes the persistence layer, the domain and the
HTTP contract together.

**Persistence (new dependency and schema)**
- Add the `Microsoft.EntityFrameworkCore.Sqlite` package to the API and replace
  `InMemoryAppointmentRepository` with an EF Core implementation of `IAppointmentRepository`.
- Appointments live in an `appointments` table that has columns for the existing fields plus
  `price`, `cancellation_fee` and `cancelled_at`. Create the schema when the application starts.
- Every application host uses its own private in-memory SQLite database (`Data Source=:memory:` on a
  connection kept open for the life of the host), so two hosts never share data and no file is
  written to disk. Veterinarians stay in memory.

**Price**
- `POST /appointments` accepts an optional `price` (decimal, default `0`). A negative price returns
  `400` with code `appointment.invalid`.
- Appointment responses (create, get, cancel) include `price`, `cancellationFee` (`0` while the
  appointment is not cancelled) and `cancelledAt` (`null` while the appointment is not cancelled).

**Cancel**
- New endpoint `POST /appointments/{id}/cancel`, no request body. On success it returns `200` with
  the appointment, `status` = `Cancelled`, the computed `cancellationFee` and `cancelledAt` set to
  the current time.
- Fee: if the cancellation happens less than 24 hours before `start`, the fee is 50% of the price,
  rounded to 2 decimal places with `MidpointRounding.AwayFromZero`. Exactly 24 hours before, or
  earlier, the fee is `0`. Use `IClock` for the current time.
- Unknown appointment: `404`.
- Already cancelled: `409` with code `appointment.already_cancelled`.
- Appointment that has already started (`start` <= now): `422` with code `appointment.already_started`.

**Agenda**
- A cancelled appointment no longer blocks its time slot: the same veterinarian can be booked again
  in that slot.
- `GET /appointments/{id}` keeps returning cancelled appointments, with their status and fee.

Keep all existing behavior and tests working. Keep the API project at
`src/VetAgenda.Api/VetAgenda.Api.csproj`, `IClock` in the `VetAgenda.Api.Domain` namespace, and
`Program` public.
