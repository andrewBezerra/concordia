Implement appointment cancellation with a late-cancellation fee.

**Price**
- `POST /appointments` accepts an optional `price` (decimal, default `0`). A negative price returns
  `400` with code `appointment.invalid`.
- Appointment responses (create, get, cancel) include `price` and `cancellationFee`
  (`0` while the appointment is not cancelled).

**Cancel**
- New endpoint `POST /appointments/{id}/cancel`, no request body. On success it returns `200` with
  the appointment, `status` = `Cancelled` and the computed `cancellationFee`.
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

Keep all existing behavior and tests working.
