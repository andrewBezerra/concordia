Add a daily agenda endpoint for veterinarians:

`GET /veterinarians/{id}/appointments?date=YYYY-MM-DD`

- Returns `200` with a JSON array of that veterinarian's appointments whose `start` falls on that
  date in UTC, ordered by `start` ascending. Each item has the same shape as
  `GET /appointments/{id}`.
- Only appointments with status `Scheduled` are listed.
- Unknown veterinarian: `404`.

Keep all existing behavior and tests working.
