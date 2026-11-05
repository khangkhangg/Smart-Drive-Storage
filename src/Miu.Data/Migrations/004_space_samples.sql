CREATE TABLE IF NOT EXISTS space_samples(id INTEGER PRIMARY KEY, root TEXT NOT NULL, ts TEXT NOT NULL, used INTEGER NOT NULL, total INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_space_samples_root ON space_samples(root, ts);
CREATE INDEX IF NOT EXISTS ix_scans_root ON scans(root, started);
