Add veterinarian working hours that an administrator can change through a protected endpoint, and
an availability endpoint. This changes the veterinarian data model, the repositories, the
scheduling rules and the HTTP contract together, and adds the first authenticated route to the API.

**Working hours** (all times UTC). Veterinarians start with these hours:
- Veterinarian 1 (Dr. Ana Souza): Monday to Friday, 08:00–17:00.
- Veterinarian 2 (Dr. Bruno Lima): Monday to Saturday, 09:00–13:00.

Each application host has its own copy of this data, so a change made on one host never reaches
another.

**Veterinarian contract**
- `GET /veterinarians` and `GET /veterinarians/{id}` add `workingHours`: an array of
  `{ "day": "Monday", "open": "08:00", "close": "17:00" }`, ordered from Monday to Sunday. Days off
  are not listed.

**Changing working hours (protected)**
- New endpoint `PUT /veterinarians/{id}/working-hours` with the body
  `{ "workingHours": [ { "day": "Monday", "open": "08:00", "close": "17:00" } ] }`. It replaces the
  whole week: a day that is not listed becomes a day off, and an empty array is valid. On success it
  returns `200` with the updated veterinarian.
- The request must carry the header `X-Admin-Key` with the value of the configuration setting
  `Admin:ApiKey`. Add that setting to a new `src/VetAgenda.Api/appsettings.json`, with the value
  `bench-admin-key`. Read the key from configuration rather than repeating it in code, compare the
  keys in constant time with `CryptographicOperations.FixedTimeEquals`, and never log them.
- Checks run in this order: missing or wrong key returns `401` with code `auth.invalid_key`; unknown
  veterinarian returns `404`; invalid hours return `400` with code `working_hours.invalid`.
- Hours are invalid when `day` is not an English day name, `open` or `close` is not a valid `HH:mm`
  time, `open` is not before `close`, or a day appears twice.
- Changing the hours does not touch appointments that already exist.

**Scheduling**
- `POST /appointments` must return `422` with code `appointment.outside_working_hours` when the
  appointment is not fully inside the current working hours of the veterinarian on that day (starts
  before opening, ends after closing, or falls on a day off). An appointment that ends exactly at
  closing time is allowed.
- Existing validations keep their current order and status codes: invalid input is still `400`,
  an unknown veterinarian is still `404`, and conflicts are still `409`. The working-hours check
  runs after the veterinarian lookup and before the conflict check.

**Availability**
- New endpoint `GET /veterinarians/{id}/availability?date=YYYY-MM-DD` returns `200` with
  `{ "date": "YYYY-MM-DD", "slots": ["08:00", "08:30", ...] }`.
- `slots` lists the start times (`HH:mm`, UTC) of every free 30-minute slot that fits fully inside
  the working hours of that date and does not overlap a `Scheduled` appointment. Slots start at
  opening time and repeat every 30 minutes.
- A day off returns an empty `slots` array. Unknown veterinarian: `404`.

Keep all existing behavior and tests working. Keep the API project at
`src/VetAgenda.Api/VetAgenda.Api.csproj`, `IClock` in the `VetAgenda.Api.Domain` namespace, and
`Program` public.
