ALTER TABLE network_shares ADD COLUMN user_name TEXT;
CREATE TABLE IF NOT EXISTS sync_baseline(job_id INTEGER NOT NULL, rel_path TEXT NOT NULL, size INTEGER NOT NULL, mtime TEXT NOT NULL, PRIMARY KEY(job_id, rel_path));
CREATE TABLE IF NOT EXISTS sync_baseline_saved(job_id INTEGER PRIMARY KEY, saved_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_job_runs_sync ON job_runs(sync_id);
