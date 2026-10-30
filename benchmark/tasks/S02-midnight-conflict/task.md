Bug report from the clinic: a veterinarian who already has an appointment from 23:30 to 00:30 can be
booked again at 00:00 the next day. Both appointments end up in the agenda at the same time.

Expected: any overlap between appointments of the same veterinarian is a conflict (`409`,
`appointment.conflict`), no matter on which calendar day each one starts. Fix it and keep all
existing behavior and tests working.
