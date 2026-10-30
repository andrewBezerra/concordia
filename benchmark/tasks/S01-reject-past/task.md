Reject appointments that start in the past.

`POST /appointments` must return `400` with error code `appointment.in_past` when `start` is earlier
than the current time. Use the existing `IClock` abstraction for the current time, not
`DateTimeOffset.UtcNow`. An appointment that starts exactly now is allowed. Keep all existing
behavior and tests working.
