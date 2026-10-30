Add veterinarian working hours and an availability endpoint.

**Working hours** (all times UTC, seeded with the veterinarians):
- Veterinarian 1 (Dr. Ana Souza): Monday to Friday, 08:00–17:00.
- Veterinarian 2 (Dr. Bruno Lima): Monday to Saturday, 09:00–13:00.

**Scheduling**
- `POST /appointments` must return `422` with code `appointment.outside_working_hours` when the
  appointment is not fully inside the veterinarian's working hours on that day (starts before
  opening, ends after closing, or falls on a day off). An appointment that ends exactly at closing
  time is allowed.
- Existing validations keep their current order and status codes: invalid input is still `400`,
  an unknown veterinarian is still `404`, and conflicts are still `409`.

**Availability**
- New endpoint `GET /veterinarians/{id}/availability?date=YYYY-MM-DD` returns `200` with
  `{ "date": "YYYY-MM-DD", "slots": ["08:00", "08:30", ...] }`.
- `slots` lists the start times (`HH:mm`, UTC) of every free 30-minute slot that fits fully inside
  the working hours of that date and does not overlap a `Scheduled` appointment. Slots start at
  opening time and repeat every 30 minutes.
- A day off returns an empty `slots` array. Unknown veterinarian: `404`.

Keep all existing behavior and tests working.
