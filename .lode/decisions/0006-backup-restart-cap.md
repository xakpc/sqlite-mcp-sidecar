# 0006 — The backup runaway cap counts restarts, not seconds

The background backup abandons itself after 50 restarts. It has no wall-clock deadline.

## The problem

A backup runs in the background and no caller waits for it, thus nothing outside the copy loop would
ever stop it. It therefore has to carry its own kill switch.

The pathology is specific. `sqlite3_backup_step` releases its shared lock on the source between
calls, which is what lets the owning application keep writing. SQLite then states: "the source
database may be modified mid-way through the backup process ... the backup will be automatically
restarted by the next call to sqlite3_backup_step()". A database under continuous write can restart
for ever, make no progress, and still consume read bandwidth against the live database.

## The options

**A wall-clock deadline**, which the first design drafted as 10 minutes hardcoded.

**A restart count**, which the step loop supplies for free: a rising `sqlite3_backup_remaining` is a
restart, because the value only falls while the copy makes progress.

## The decision

Count restarts. `MaxRestarts = 50`.

A deadline cannot tell a livelock from a large database. A 50 GB database copying steadily for
twenty minutes is healthy, and a deadline reports it as the same failure as one that can never
finish — a failure the operator cannot act on, because the only remedy is to stop using the tool. A
restart count names the real pathology and never punishes size.

A `SQLITE_BUSY` or `SQLITE_LOCKED` step spends the same budget. Both mean the owning application is
busy writing, which is the same condition from the same cause.

## What it costs

A backup that never restarts but crawls on failing storage is not stopped. That is accepted: it is
making progress, and a stuck filesystem is an operator problem that a backup timeout would only
disguise.

## Measured behaviour

An 18 MiB database on a local SSD, with one committed write to the source at the stated interval:

| Writes to the source | Outcome | Restarts | Duration |
| --- | --- | --- | --- |
| ~500 / second | abandoned | 50 | 823 ms |
| ~40 / second | succeeded | 2 | 188 ms |
| ~10 / second | succeeded | 1 | 98 ms |
| ~2.5 / second | succeeded | 0 | 79 ms |

The cap fires only under sustained extreme write load, and it fires in under a second rather than
spinning. Read it as a ratio: restart exposure grows with database size multiplied by write rate,
because a larger database needs more steps and every step is another chance for a writer to land.

## Consequence

A very large and very busy database is not backupable through the Online Backup API without blocking
the owning application. That limit belongs to SQLite, not to this code, and the honest report is
`BackupFailed` with the advice to retry when the database is quieter. Do not add a wall-clock cap to
paper over it, and do not raise `PagesPerStep` without measuring the lock hold time: a larger step
lowers restart exposure by holding the source lock longer, which is the cost this design refuses.

## Related

- [../database/backups.md](../database/backups.md)
- [0007-tasks-over-a-status-tool.md](0007-tasks-over-a-status-tool.md)
