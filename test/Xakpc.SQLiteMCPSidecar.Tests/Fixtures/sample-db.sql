-- The sample database of the test suite and of scripts/dev-sidecar.ps1. It is the one source of
-- truth for both, thus a manual session and an automated run see the same schema.

PRAGMA journal_mode = WAL;

CREATE TABLE users (
  id       INTEGER PRIMARY KEY,
  email    TEXT NOT NULL UNIQUE,
  name     TEXT NOT NULL,
  created  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE jobs (
  id        INTEGER PRIMARY KEY,
  status    TEXT NOT NULL DEFAULT 'pending',
  retry     INTEGER NOT NULL DEFAULT 0,
  owner_id  INTEGER REFERENCES users(id),
  payload   TEXT,
  CHECK (status IN ('pending', 'running', 'failed', 'done'))
);

CREATE TABLE logs (
  id      INTEGER PRIMARY KEY,
  job_id  INTEGER NOT NULL REFERENCES jobs(id),
  level   TEXT NOT NULL,
  message TEXT NOT NULL
);

-- job_tags cascades on purpose. It is the one table that makes the blast radius of a delete larger
-- than the rows the filter selects, thus it is what proves that maxRows bounds the cascade too.
-- logs does NOT cascade: a delete of a job with logs must still fail with a foreign key violation.
CREATE TABLE job_tags (
  id      INTEGER PRIMARY KEY,
  job_id  INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  tag     TEXT NOT NULL
);

CREATE INDEX idx_jobs_status ON jobs(status);
CREATE INDEX idx_logs_job ON logs(job_id);

CREATE VIEW failed_jobs AS
  SELECT j.id, j.status, j.retry, u.email
  FROM jobs j LEFT JOIN users u ON u.id = j.owner_id
  WHERE j.status = 'failed';

INSERT INTO users (id, email, name) VALUES
  (1, 'ada@example.com', 'Ada'),
  (2, 'grace@example.com', 'Grace');

INSERT INTO jobs (id, status, retry, owner_id, payload) VALUES
  (41, 'failed', 2, 1, '{"kind":"import"}'),
  (52, 'failed', 1, 2, '{"kind":"export"}'),
  (53, 'pending', 0, 1, '{"kind":"import"}'),
  (54, 'done', 0, 2, NULL);

INSERT INTO logs (job_id, level, message) VALUES
  (41, 'error', 'import failed'),
  (52, 'error', 'export failed'),
  (53, 'info', 'queued');

INSERT INTO job_tags (job_id, tag) VALUES
  (41, 'import'),
  (41, 'retry'),
  (52, 'export');
